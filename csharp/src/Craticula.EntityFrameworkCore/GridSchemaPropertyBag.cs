using Craticula.Core.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Craticula.EntityFrameworkCore;

/// <summary>
/// Implemented by a <see cref="DbContext"/> whose model was built from a <see cref="GridSchema"/>, so
/// that <see cref="GridSchemaModelCacheKeyFactory"/> can tell two such contexts apart.
///
/// <para>See that class for why this exists at all. In short: EF caches a built model per
/// <see cref="DbContext"/> <em>type</em>, and a schema-driven model is not a property of the type.</para>
/// </summary>
public interface IGridSchemaModel
{
    /// <summary>The schema <see cref="GridSchemaPropertyBag.Configure"/> was called with.</summary>
    GridSchema Schema { get; }
}

/// <summary>
/// Makes EF's model cache see the <see cref="GridSchema"/> a context was built from — the one piece of
/// EF plumbing a schema-driven model cannot do without.
///
/// <para><b>The trap it removes, because the symptom is deeply misleading.</b> EF builds the model once
/// per <see cref="DbContext"/> <em>type</em> and caches it: <c>OnModelCreating</c> is called for the
/// first instance and never again. With <see cref="GridSchemaPropertyBag.Configure"/> that is wrong by
/// construction, because the model is a function of a runtime argument rather than of the class. A second
/// context of the same type built from a different schema silently gets the FIRST schema's model, and
/// what surfaces is not "wrong schema" but one of two unrelated-looking errors from deep inside the
/// query pipeline: <i>"Translation of 'EF.Property&lt;long?&gt;(…, "age")' failed … the specified property
/// does not exist on the entity type"</i>, or <i>"Cannot create a DbSet for 'Dictionary&lt;string,
/// object&gt;' because it is configured as a shared-type entity type"</i>. Neither mentions caching, and
/// both are reproducible only when more than one schema is in play — so a host serving one grid never
/// sees it and a host serving two sees it immediately.</para>
///
/// <para><b>Register it on every schema-driven context:</b>
/// <c>options.ReplaceService&lt;IModelCacheKeyFactory, GridSchemaModelCacheKeyFactory&gt;()</c>, and have
/// the context implement <see cref="IGridSchemaModel"/>. A context whose schema is fixed at compile time
/// (a hand-written model over entity classes) needs neither.</para>
///
/// <para>The key includes the <see cref="GridSchema"/> itself, compared with the record equality it
/// already has. Note what that means in practice: <see cref="GridSchema.Fields"/> is a dictionary, and a
/// record compares a dictionary member by reference — so two independent parses of the SAME document are
/// two keys and the model is built twice. That is the safe direction to err (a rebuilt model is slow, a
/// reused wrong one is incorrect), and a host holding one parsed schema in DI — which is the arrangement
/// SC-1 implies, since the schema is host-declared and never client-influenced — caches exactly
/// once.</para>
/// </summary>
public sealed class GridSchemaModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc />
    public object Create(DbContext context, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Registering this factory is an explicit opt-in, so a context that registers it and does NOT
        // implement IGridSchemaModel is unambiguously a half-finished configuration rather than a host
        // that wanted EF's default. Left as `as ... ?.Schema`, the schema component would be null, the
        // key would collapse back to EF's own (Type, designTime), and the second schema would silently
        // reuse the first one's model — which is the exact defect this factory exists to remove, and it
        // resurfaces as one of the two misleading errors named in this file's remarks ("Cannot create a
        // DbSet for 'Dictionary<string, object>'…"), neither of which mentions caching. Naming the
        // missing interface here costs one cast and turns a silent mis-cache into a sentence.
        if (context is not IGridSchemaModel schemaModel)
        {
            throw new InvalidOperationException(
                $"'{context.GetType().Name}' registers {nameof(GridSchemaModelCacheKeyFactory)} but does not " +
                $"implement {nameof(IGridSchemaModel)}, so the schema cannot take part in the model cache key. " +
                "EF caches one model per DbContext type, so without it every schema after the first would " +
                "silently reuse the first one's model and fail later with an error that does not mention " +
                $"caching. Implement {nameof(IGridSchemaModel)} on the context (returning the same " +
                $"{nameof(GridSchema)} its OnModelCreating builds from), or remove the factory registration.");
        }

        return (context.GetType(), schemaModel.Schema, designTime);
    }
}

/// <summary>
/// Turns a <see cref="GridSchema"/> into an EF model, for the host that has no entity classes to point
/// at — <b>one CLR type serving every schema</b>, with no code generation and no class per table.
///
/// <para><b>The mechanism, and why it is not a workaround.</b> EF Core's <em>property-bag
/// shared-type entity</em> maps <see cref="Dictionary{TKey,TValue}"/> under an entity-type NAME rather
/// than a CLR type, with each column declared as an indexer property. The same
/// <see cref="Dictionary{TKey,TValue}"/> can therefore be registered any number of times under
/// different names with different property sets, and a query over one is an ordinary translated LINQ
/// query — <c>EF.Property&lt;T&gt;(row, "customerName")</c> compiles to
/// <c>SELECT "customerName"</c>, not to client-side evaluation. It is the shape EF ships for
/// many-to-many join entities and for keyless views, which is exactly this situation: a table whose
/// shape is data.</para>
///
/// <para><b>Why any of this is needed at all.</b> The alternative for a project whose conformance suite
/// carries 51 distinct schemas is 51 entity classes, regenerated whenever a host adds a column — and
/// worse, a public contract in which this adapter only works for hosts that own their model. A grid
/// whose columns are configured at runtime (the case AG Grid's SSRM exists for) has no compile-time
/// class to declare. So this is the general path, and a host that <em>does</em> have entity classes
/// simply does not call it: every entry point in this package is generic in the row type and composes
/// onto whatever <see cref="IQueryable{T}"/> it is handed, which is what makes EF's global query
/// filters — AR-4's whole reason for existing — reachable from a real <c>DbSet</c>.</para>
///
/// <para><b>Keyless, deliberately (<c>EntityTypeBuilder.HasNoKey</c>).</b> A grid query is a
/// read. Declaring a key would buy change tracking nobody wants and one active hazard: EF's identity
/// resolution returns the <em>same instance</em> for two rows with equal key values, so a schema whose
/// declared <see cref="GridSchema.StableKey"/> turned out not to be unique would silently collapse
/// rows rather than return them. Keyless entity types are read-only by construction, which is the
/// honest declaration of what this model is for.</para>
/// </summary>
public static class GridSchemaPropertyBag
{
    /// <summary>
    /// The one CLR type every schema is mapped onto. Public because a host has to name it to query it:
    /// <c>context.Set&lt;Dictionary&lt;string, object?&gt;&gt;(source)</c>, which
    /// <see cref="Query"/> wraps.
    ///
    /// <para>Nullable in its value argument because a cell can be null and this type IS the row.
    /// Nullable reference annotations are erased at runtime, so this is the same
    /// <c>Dictionary&lt;string, object&gt;</c> EF's property-bag support requires.</para>
    /// </summary>
    public static Type RowType { get; } = typeof(Dictionary<string, object?>);

    /// <summary>
    /// Registers <paramref name="schema"/> as a keyless property-bag entity type named
    /// <see cref="GridSchema.Source"/>, with one indexer property per declared field.
    ///
    /// <para>Each property is named after the field's <b>physical SQL column</b>
    /// (<c>physical.sql.column</c>, falling back to the colId), which is the same string
    /// <see cref="EfCoreQueryBuilder.Property(System.Linq.Expressions.ParameterExpression, GridSchema, string)"/>
    /// asks EF for and the same one <c>SqlQueryBuilder</c> quotes — so the two paths read the same
    /// column of the same table without either knowing about the other. Its CLR type comes from
    /// <see cref="EfCoreQueryBuilder.ClrTypeFor"/>, for the same reason: the type this model declares
    /// and the type the query asks for must be one decision.</para>
    ///
    /// <para>Call it from <c>OnModelCreating</c>, once per schema a <c>DbContext</c> serves. Fields
    /// declaring <c>physical.sql.qualifiedColumn</c> or <c>physical.sql.expression</c> are refused here
    /// rather than at query time: a model that silently omitted them would fail later with EF's own
    /// "property not found", pointing at a query instead of at the schema that caused it.</para>
    ///
    /// <para><b>A context type that can be built from more than one schema MUST also register
    /// <see cref="GridSchemaModelCacheKeyFactory"/></b> and implement <see cref="IGridSchemaModel"/>.
    /// Without it EF calls <c>OnModelCreating</c> once per context TYPE and every later schema silently
    /// reuses the first one's model — see that class for the two misleading errors that produces.</para>
    /// </summary>
    /// <exception cref="EfCoreCompilationException">A field declares a <c>physical.sql</c> form this
    /// adapter cannot map (see <c>EfCoreQueryBuilder.Property</c> for why neither has a defined
    /// translation).</exception>
    public static void Configure(ModelBuilder modelBuilder, GridSchema schema)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(schema);

        var entity = modelBuilder.SharedTypeEntity<Dictionary<string, object?>>(schema.Source);
        entity.ToTable(schema.Source);

        // GRP-3's declaration order, not Fields.Keys: the projection reads FieldOrder, and declaring
        // the properties in the same order keeps a model dump comparable to a schema document. It has
        // no effect on any emitted query, which names its columns.
        foreach (var field in schema.FieldOrder)
        {
            var config = schema.Fields[field];
            var mapping = config.Physical?.Sql;

            if (mapping?.QualifiedColumn is not null || mapping?.Expression is not null)
            {
                throw new EfCoreCompilationException(
                    $"Field '{field}' declares a physical.sql form this adapter does not map " +
                    "(qualifiedColumn / expression). A property-bag entity maps plain columns of one table; a " +
                    "qualified column is another table's, reached in EF through a navigation, and an expression is " +
                    "raw SQL with no expression-tree equivalent. Map such a field in a hand-written EF model " +
                    "instead — every entry point here is generic in the row type.");
            }

            entity.IndexerProperty(EfCoreQueryBuilder.ClrTypeFor(config.Type), mapping?.Column ?? field);
        }

        entity.HasNoKey();
    }

    /// <summary>
    /// The queryable over the entity type <see cref="Configure"/> registered for
    /// <paramref name="schema"/> — the <c>FROM</c> target every entry point in this package takes.
    ///
    /// <para>A one-line wrapper, and worth having: <c>Set&lt;T&gt;(string)</c>'s name argument must be
    /// the entity-type name <see cref="Configure"/> chose, and having both read
    /// <see cref="GridSchema.Source"/> is what makes that impossible to get wrong. EF throws a clear
    /// "cannot be used with the entity type" if the schema was never configured.</para>
    /// </summary>
    public static IQueryable<Dictionary<string, object?>> Query(DbContext context, GridSchema schema)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(schema);

        return context.Set<Dictionary<string, object?>>(schema.Source);
    }
}
