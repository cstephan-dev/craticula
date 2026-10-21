namespace Craticula.OpenSearch.Client;

/// <summary>
/// Which engine the client talks to (OSE-15). The compiled request is the same for both (OSE-14); only the
/// point-in-time lifecycle differs: the create URL, the key holding the id in the answer, the close URL
/// and the close body. Searching with the point-in-time is identical.
/// </summary>
public enum SearchEngineFlavour
{
    /// <summary>OpenSearch 2.x: <c>POST /{index}/_search/point_in_time</c>, answer key <c>pit_id</c>, close <c>DELETE /_search/point_in_time</c>.</summary>
    OpenSearch = 0,

    /// <summary>Elasticsearch 8.x: <c>POST /{index}/_pit</c>, answer key <c>id</c>, close <c>DELETE /_pit</c> with <c>{"id": …}</c>.</summary>
    Elasticsearch = 1,
}
