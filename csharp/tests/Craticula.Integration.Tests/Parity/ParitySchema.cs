using Craticula.Core.Schema;

namespace Craticula.Integration.Tests.Parity;

/// <summary>
/// The one GridSchema every parity case runs against (T-3, <c>spec/semantics.md</c> PAR-1..PAR-4). Written as JSON text,
/// like a fixture's <c>schema.json</c>, so a runner can hand the same text to every harness and a reader can diff it
/// against <c>spec/fixtures/operators/*/schema.json</c>.
///
/// <para>What the schema deliberately does <b>not</b> declare is the generator's profile (PAR-3's register):
/// no <c>caseSensitivity: "sensitive"</c> string (SEM-1, divergence #5), no <c>searchMode: "analyzed"</c> (SEM-7), no
/// <c>groupable</c> field (GRP-2, divergences #3/#4). <c>id</c> is the stableKey: <c>int64</c>, sortable, not filterable,
/// exactly as in the operator fixtures. Every other field is filterable and sortable and holds NULLs (the dataset
/// writes them), one field per supported type.</para>
/// </summary>
internal static class ParitySchema
{
    /// <summary>The logical field names, in declaration order (the logical column list of XEQ-3).</summary>
    public static readonly IReadOnlyList<string> FieldNames = ["id", "label", "qty", "score", "price", "active", "born", "seen"];

    public const string Json = """
        {
          "mode": "strict",
          "source": "parity_items",
          "stableKey": "id",
          "fields": {
            "id": {
              "type": "int64",
              "filterable": false,
              "sortable": true,
              "physical": { "sql": { "column": "id" }, "mongo": { "path": "id" }, "search": { "field": "id" } }
            },
            "label": {
              "type": "string",
              "filterable": true,
              "sortable": true,
              "physical": {
                "sql": { "column": "label" },
                "mongo": { "path": "label" },
                "search": { "field": "label", "keywordSubField": "label.keyword" }
              },
              "text": { "caseSensitivity": "insensitive", "searchMode": "substring" }
            },
            "qty": {
              "type": "int64",
              "filterable": true,
              "sortable": true,
              "physical": { "sql": { "column": "qty" }, "mongo": { "path": "qty" }, "search": { "field": "qty" } }
            },
            "score": {
              "type": "double",
              "filterable": true,
              "sortable": true,
              "physical": { "sql": { "column": "score" }, "mongo": { "path": "score" }, "search": { "field": "score" } }
            },
            "price": {
              "type": "decimal",
              "filterable": true,
              "sortable": true,
              "physical": { "sql": { "column": "price" }, "mongo": { "path": "price" }, "search": { "field": "price" } }
            },
            "active": {
              "type": "boolean",
              "filterable": true,
              "sortable": true,
              "physical": { "sql": { "column": "is_active" }, "mongo": { "path": "active" }, "search": { "field": "active" } }
            },
            "born": {
              "type": "date",
              "filterable": true,
              "sortable": true,
              "physical": { "sql": { "column": "born_on" }, "mongo": { "path": "born" }, "search": { "field": "born" } }
            },
            "seen": {
              "type": "datetime",
              "filterable": true,
              "sortable": true,
              "physical": { "sql": { "column": "seen_at" }, "mongo": { "path": "seen" }, "search": { "field": "seen" } }
            }
          }
        }
        """;

    public static GridSchema Create() => GridSchema.Parse(Json);
}
