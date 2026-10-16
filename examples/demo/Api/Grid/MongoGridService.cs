using Craticula.Core.Export;
using Craticula.Core.Grid;
using Craticula.Mongo;
using Craticula.MongoDB.Driver;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Demo.Api.Grid;

/// <summary>MongoDB through <c>Craticula.MongoDB.Driver</c>.</summary>
public sealed class MongoGridService(IMongoDatabase database, GridRequestPreparer preparer) : IGridService
{
    public string Name => "mongo";

    public async Task<GridPageResult> FetchRowsAsync(PreparedRows request, CancellationToken cancellationToken)
    {
        var page = await MongoGridQuery.FetchPageAsync(
            database, ToMongoRequest(request), includeRowCount: true, cancellationToken);
        return page.ToGridPageResult();
    }

    /// <summary>
    /// The Mongo adapter has no entry point for BM-3's distinct values, so this compiles with
    /// <c>MongoPipelineBuilder.BuildSetValues</c> and runs ONLY that pipeline (and collation), translated to BSON the
    /// way the adapter does it: each stage's JSON text parsed as Extended JSON. Each result document is <c>{ _id: value }</c>.
    /// </summary>
    public async Task<IReadOnlyList<object?>> FetchSetValuesAsync(PreparedSetValues request, CancellationToken cancellationToken)
    {
        var query = MongoPipelineBuilder.BuildSetValues(
            new MongoSetValuesRequest
            {
                Schema = preparer.Schema,
                ColId = request.ColId,
                Filter = request.Filter,
                RowLimit = request.RowLimit
            });

        var stages = query.Pipeline.Select(stage => BsonDocument.Parse(stage.ToJsonString())).ToArray();
        var options = query.Collation is null
            ? null
            : new AggregateOptions { Collation = Collation.FromBsonDocument(BsonDocument.Parse(query.Collation.ToJsonString())) };

        var values = new List<object?>();
        var collection = database.GetCollection<BsonDocument>(query.Collection);
        using var cursor = await collection.AggregateAsync<BsonDocument>(stages, options, cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            values.AddRange(cursor.Current.Select(document => ToClr(document["_id"])));
        }

        return values;
    }

    public IAsyncEnumerable<ExportRow> StreamExportAsync(PreparedRows request, CancellationToken cancellationToken) =>
        MongoExportStream.StreamAsync(database, ToMongoRequest(request), cancellationToken);

    public CompiledQueryView Compile(PreparedRows request)
    {
        var mongoRequest = ToMongoRequest(request);
        if (mongoRequest.Group is null && mongoRequest.Projection is null)
        {
            mongoRequest = mongoRequest with { Projection = preparer.Schema.FieldOrder };
        }

        var query = MongoPipelineBuilder.Build(mongoRequest);
        var text = query.ToArtifact().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        // MongoDB has no bind parameters: values travel inside the pipeline as data, never as concatenated text.
        return new CompiledQueryView("mongo", "mongodb", text, []);
    }

    private MongoQueryRequest ToMongoRequest(PreparedRows request) => new()
    {
        Schema = preparer.Schema,
        Filter = request.Filter,
        Sort = request.Parsed.Sort,
        Page = request.Parsed.Page,
        Group = request.Parsed.Group,
        Aggregations = request.Parsed.Aggregations,
        Projection = request.Parsed.Export?.ProjectionColumns
    };

    private static object? ToClr(BsonValue value) => value switch
    {
        { IsBsonNull: true } => null,
        BsonDecimal128 d => Decimal128.ToDecimal(d.Value),
        BsonDateTime t => t.ToUniversalTime(),
        _ => BsonTypeMapper.MapToDotNetValue(value)
    };
}
