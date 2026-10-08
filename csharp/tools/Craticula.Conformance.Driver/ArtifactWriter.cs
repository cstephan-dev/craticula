using System.Text.Json;
using Craticula.Mongo;
using Craticula.Search;
using Craticula.Sql;

namespace Craticula.Conformance.Driver;

/// <summary>
/// Writes a compiled query as the artifact its backend's <c>expected/&lt;backend&gt;.artifact.json</c>
/// holds (<c>spec/conformance-runner.md</c> §2.2), into the caller's single-line writer.
///
/// <para>Prints what the compiler produced and nothing more: no normalising, sorting or
/// pretty-printing to look like the expected file (§3, last paragraph). Whether a difference matters
/// is the runner's call (§4), not the driver's.</para>
/// </summary>
internal static class ArtifactWriter
{
    /// <summary>Dispatches on what <see cref="RequestCompiler.Compile"/> returned.</summary>
    public static void Write(Utf8JsonWriter writer, object compiled)
    {
        switch (compiled)
        {
            case SqlQuery sql:
                WriteSql(writer, sql);
                break;
            case MongoQuery mongo:
                mongo.ToArtifact().WriteTo(writer);
                break;
            case SearchQuery search:
                search.ToArtifact().WriteTo(writer);
                break;
            default:
                throw new ArgumentException(
                    $"No artifact shape for a compiled {compiled.GetType().Name}.", nameof(compiled));
        }
    }

    /// <summary>
    /// The <c>{ dialect, sql, params }</c> wrapper every SQL artifact pins. <c>params</c> goes through
    /// <see cref="JsonSerializer.Serialize{TValue}(TValue, JsonSerializerOptions?)"/> with the default
    /// options — the writer <c>FixtureCompiler.CanonicalJson</c> uses in the C# conformance tests — and
    /// is spliced in raw, so each number keeps the literal text the serializer gives it and a
    /// <c>decimal</c> stays the numeric string AR-7 binds it as (<c>"19.99"</c>, never <c>19.99</c>).
    /// </summary>
    private static void WriteSql(Utf8JsonWriter writer, SqlQuery query)
    {
        writer.WriteStartObject();
        writer.WriteString("dialect", query.Dialect);
        writer.WriteString("sql", query.Sql);
        writer.WritePropertyName("params");
        writer.WriteRawValue(JsonSerializer.Serialize(query.Parameters));
        writer.WriteEndObject();
    }
}
