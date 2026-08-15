using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

public class GridSchemaDeserializationTests
{
    private const string Json = """
    {
      "mode": "strict",
      "source": "items",
      "stableKey": "id",
      "staticFilter": {
        "type": "comparison",
        "op": "eq",
        "field": "tenantId",
        "value": { "type": "int64", "value": 42 }
      },
      "fields": {
        "id": {
          "type": "int64",
          "filterable": true,
          "sortable": true,
          "groupable": false,
          "physical": {
            "sql": { "column": "id" },
            "mongo": { "path": "_id" },
            "search": { "field": "id" }
          }
        },
        "name": {
          "type": "string",
          "groupable": true,
          "text": { "caseSensitivity": "insensitive", "searchMode": "substring" },
          "physical": {
            "sql": { "qualifiedColumn": "t.name" },
            "search": { "field": "name", "keywordSubField": "name.keyword", "normalizedSubField": "name.normalized" }
          },
          "label": "Name"
        },
        "fullName": {
          "type": "string",
          "physical": {
            "sql": { "expression": "concat(first_name, ' ', last_name)" }
          }
        },
        "quantity": {
          "type": "int64",
          "groupable": true,
          "aggregatable": ["sum", "avg", "min", "max", "count"],
          "exportable": false
        }
      }
    }
    """;

    [Fact]
    public void RoundTrips_TopLevel_GridSchema_Shape()
    {
        var schema = GridSchema.Parse(Json);

        Assert.Equal(GridSchemaMode.Strict, schema.Mode);
        Assert.Equal("items", schema.Source);
        Assert.Equal("id", schema.StableKey);
        var staticFilter = Assert.IsType<ComparisonNode>(schema.StaticFilter);
        Assert.Equal("tenantId", staticFilter.Field);
        Assert.Equal(ComparisonOperator.Eq, staticFilter.Op);
        Assert.Equal(42L, Assert.IsType<Int64Value>(staticFilter.Value).Value);
        Assert.Equal(4, schema.Fields.Count);
    }

    [Fact]
    public void RoundTrips_FieldConfig_With_Sql_Column_And_Mongo_And_Search_Mapping()
    {
        var schema = GridSchema.Parse(Json);

        var id = schema.Fields["id"];
        Assert.Equal(FieldType.Int64, id.Type);
        Assert.True(id.Filterable);
        Assert.True(id.Sortable);
        Assert.False(id.Groupable);
        Assert.Equal("id", id.Physical!.Sql!.Column);
        Assert.Null(id.Physical.Sql.QualifiedColumn);
        Assert.Null(id.Physical.Sql.Expression);
        Assert.Equal("_id", id.Physical.Mongo!.Path);
        Assert.Equal("id", id.Physical.Search!.Field);
    }

    [Fact]
    public void RoundTrips_FieldConfig_With_QualifiedColumn_TextOptions_And_SearchSubFields()
    {
        var schema = GridSchema.Parse(Json);

        var name = schema.Fields["name"];
        Assert.Equal(FieldType.String, name.Type);
        Assert.True(name.Groupable);
        Assert.Equal("t.name", name.Physical!.Sql!.QualifiedColumn);
        Assert.Equal("name.keyword", name.Physical.Search!.KeywordSubField);
        Assert.Equal("name.normalized", name.Physical.Search.NormalizedSubField);
        Assert.Equal(CaseSensitivity.Insensitive, name.Text!.CaseSensitivity);
        Assert.Equal(SearchMode.Substring, name.Text.SearchMode);
        Assert.Equal("Name", name.Label);
    }

    [Fact]
    public void RoundTrips_FieldConfig_With_Sql_Expression_Escape_Hatch()
    {
        var schema = GridSchema.Parse(Json);

        var fullName = schema.Fields["fullName"];
        Assert.Equal("concat(first_name, ' ', last_name)", fullName.Physical!.Sql!.Expression);
        Assert.Null(fullName.Physical.Sql.Column);
        Assert.Null(fullName.Physical.Sql.QualifiedColumn);
    }

    [Fact]
    public void RoundTrips_FieldConfig_Aggregatable_List_And_Exportable_Override()
    {
        var schema = GridSchema.Parse(Json);

        var quantity = schema.Fields["quantity"];
        Assert.Equal(
            [
                AggregationFunc.Sum,
                AggregationFunc.Avg,
                AggregationFunc.Min,
                AggregationFunc.Max,
                AggregationFunc.Count
            ],
            quantity.Aggregatable);
        Assert.False(quantity.Exportable);
    }

    [Fact]
    public void Applies_Documented_Defaults_When_Optional_Properties_Are_Absent()
    {
        const string minimalJson = """
        {
          "source": "items",
          "stableKey": "id",
          "fields": {
            "id": { "type": "int64" }
          }
        }
        """;

        var schema = GridSchema.Parse(minimalJson);

        Assert.Equal(GridSchemaMode.Strict, schema.Mode);
        Assert.Null(schema.StaticFilter);

        var id = schema.Fields["id"];
        Assert.True(id.Filterable);
        Assert.True(id.Sortable);
        Assert.False(id.Groupable);
        Assert.Empty(id.Aggregatable);
        Assert.Null(id.Physical);
        Assert.Null(id.Text);
        Assert.True(id.Exportable);
        Assert.Null(id.Label);
    }

    /// <summary>
    /// GRP-3: <see cref="GridSchema.FieldOrder"/> is the schema's DECLARATION order, read from the
    /// JSON document itself. The field names below are deliberately reverse-alphabetical and not the
    /// order a sorted or hash-ordered container would produce, so this fails if the order is ever
    /// re-derived from <see cref="GridSchema.Fields"/> — an <c>IReadOnlyDictionary</c> whose
    /// enumeration order .NET does not specify, and which the drill-down leaf's projection
    /// (compared byte-for-byte by <c>group-aggregation/005</c> and <c>006</c>) depends on.
    /// </summary>
    [Fact]
    public void FieldOrder_Is_The_Declaration_Order_Of_The_Json_Not_The_Dictionarys_Enumeration()
    {
        var schema = GridSchema.Parse("""
        {
          "source": "items",
          "stableKey": "zulu",
          "fields": {
            "zulu":    { "type": "int64" },
            "alpha":   { "type": "string" },
            "mike":    { "type": "string" },
            "bravo":   { "type": "int64" }
          }
        }
        """);

        Assert.Equal(["zulu", "alpha", "mike", "bravo"], schema.FieldOrder);
    }

    /// <summary>A schema built in code has no JSON to read an order from, so it falls back to
    /// <see cref="GridSchema.Fields"/> — the only source there is, and the one that was being relied
    /// on implicitly everywhere before GRP-3 made the rule explicit.</summary>
    [Fact]
    public void A_Hand_Built_Schema_Falls_Back_To_Its_Dictionarys_Own_Keys()
    {
        var schema = new GridSchema
        {
            Source = "items",
            StableKey = "id",
            Fields = new Dictionary<string, FieldConfig>
            {
                ["id"] = new() { Type = FieldType.Int64 },
                ["name"] = new() { Type = FieldType.String }
            }
        };

        Assert.Equal(schema.Fields.Keys, schema.FieldOrder);
    }
}
