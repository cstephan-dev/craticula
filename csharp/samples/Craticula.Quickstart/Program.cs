using Craticula.AgGrid;
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.Sql;

// 1. The schema is the allowlist: a column that is not listed here can never reach a query, and every identifier in the
//    statement comes from `physical.sql.column`, never from the request.
const string schemaJson = """
    {
      "mode": "strict",
      "source": "orders",
      "stableKey": "id",
      "fields": {
        "id":       { "type": "int64",    "sortable": true, "physical": { "sql": { "column": "id" } } },
        "customer": { "type": "string",   "filterable": true, "sortable": true, "physical": { "sql": { "column": "customer" } } },
        "amount":   { "type": "decimal",  "filterable": true, "sortable": true, "physical": { "sql": { "column": "amount" } } },
        "status":   { "type": "string",   "filterable": true, "sortable": true, "physical": { "sql": { "column": "status" } } }
      }
    }
    """;

// 2. A request exactly as AG Grid's server-side row model sends it: customers containing "acme", amount above 100,
//    biggest amounts first, the first 50 rows.
const string requestJson = """
    {
      "startRow": 0,
      "endRow": 50,
      "rowGroupCols": [],
      "valueCols": [],
      "pivotCols": [],
      "pivotMode": false,
      "groupKeys": [],
      "filterModel": {
        "customer": { "filterType": "text", "type": "contains", "filter": "acme" },
        "amount": { "filterType": "number", "type": "greaterThan", "filter": 100 }
      },
      "sortModel": [ { "colId": "amount", "sort": "desc" } ]
    }
    """;

GridSchema schema = GridSchema.Parse(schemaJson);

// 3. Parse the request into the canonical IR, then validate it against the schema and the resource limits.
AgGridParsedRequest parsed = AgGridRequestParser.Parse(requestJson, new AgGridParseOptions { Schema = schema });
FilterNode? filter = GridFilterPipeline.Process(
    parsed.NativeFilter, parsed.CustomFilters, parsed.Page, schema, new ResourceLimits(), parsed.Group, parsed.Aggregations);

// 4. Compile: the same IR gives each database its own statement. Values travel as parameters, never inside the text.
foreach ((string name, ISqlDialect dialect) in new (string, ISqlDialect)[]
         {
             ("PostgreSQL", PostgresDialect.Instance),
             ("SQL Server", MssqlDialect.Instance),
         })
{
    SqlQuery query = SqlQueryBuilder.Build(
        new SqlQueryRequest { Schema = schema, Filter = filter, Sort = parsed.Sort, Page = parsed.Page }, dialect);

    Console.WriteLine($"-- {name}");
    Console.WriteLine(query.Sql);
    Console.WriteLine($"-- parameters: {string.Join(", ", query.Parameters.Select(value => value is string text ? $"'{text}'" : value?.ToString() ?? "NULL"))}");
    Console.WriteLine();
}

// 5. A column the schema does not list is refused before any query exists. Here the grid asks to filter on `password`.
const string hostileJson = """
    { "startRow": 0, "endRow": 50, "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
      "filterModel": { "password": { "filterType": "text", "type": "equals", "filter": "x" } }, "sortModel": [] }
    """;

try
{
    AgGridParsedRequest bad = AgGridRequestParser.Parse(hostileJson, new AgGridParseOptions { Schema = schema });
    GridFilterPipeline.Process(bad.NativeFilter, bad.CustomFilters, bad.Page, schema, new ResourceLimits(), bad.Group, bad.Aggregations);
    Console.WriteLine("-- the unknown column was NOT refused");
}
catch (UnknownFieldException error)
{
    Console.WriteLine($"-- refused: {error.GetType().Name}: {error.Message}");
}
