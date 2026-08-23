using System.Text.Json.Serialization;
using Craticula.Core.Grid;

namespace Craticula.AgGrid;

/// <summary>
/// A HOST's own HTTP JSON response body shape, <c>{ rows, lastRow?, groupLevelInfo? }</c> — #1622's
/// definition of done, verbatim, and the TRD's own AG-3 spec text ("compatible with
/// <c>LoadSuccessParams</c>"). <see cref="LoadSuccessParamsMapper.Map"/> is the one place a
/// <see cref="GridPageResult"/> becomes this.
///
/// <para><b>Not literally AG Grid's own <c>LoadSuccessParams</c> callback property names.</b> AG Grid's
/// real client-side datasource callback is <c>params.success({ rowData, rowCount, groupLevelInfo })</c>
/// (<c>server-side-sorting/main.ts</c>'s own vendored example: <c>params.success({ rowData:
/// response.rows, rowCount: response.lastRow })</c>) — <c>rowData</c> and <c>rowCount</c>, NOT
/// <c>rows</c> and <c>lastRow</c>. This type's <c>rows</c>/<c>lastRow</c> naming matches only the
/// HOST's own response body (what a backend returns to its own frontend); a host's frontend datasource
/// code is the one that renames <c>rows</c> → <c>rowData</c> and <c>lastRow</c> → <c>rowCount</c> before
/// actually calling <c>params.success(...)</c>. <see cref="GroupLevelInfo"/> is the one member that
/// passes through unrenamed — it is a literal, unrenamed AG Grid member on both sides of that call.</para>
///
/// <para><b><see cref="JsonPropertyNameAttribute"/> on every member, explicitly — this is the first type
/// in the repo that a HOST's own <see cref="System.Text.Json.JsonSerializerOptions"/> serializes rather
/// than one this project fully controls.</b> Every prior wire artifact this repo builds (<c>SqlQuery</c>,
/// <c>MongoQuery</c>, <c>SearchQuery</c>) constructs its own <see cref="System.Text.Json.Nodes.JsonObject"/>
/// and therefore controls its own casing directly, with no serializer settings to depend on. This type
/// is instead handed to a host that already has its own ASP.NET Core / <c>System.Text.Json</c>
/// configuration — a global <c>PropertyNamingPolicy</c> (PascalCase, snake_case, whatever the host
/// defaults to) must never be allowed to silently rewrite this contract's exact camelCase field names
/// (<c>rows</c>, <c>lastRow</c>, <c>groupLevelInfo</c>) into something the host's own frontend
/// datasource code — which expects exactly these names before it renames two of them into AG Grid's
/// own <c>rowData</c>/<c>rowCount</c>, per this type's own remarks above — no longer recognizes. See
/// <c>LoadSuccessParamsMapperTests</c>'s own serialization round-trip, which proves this directly
/// against a non-default naming policy rather than trusting the attribute is present.</para>
/// </summary>
public sealed record LoadSuccessParams
{
    /// <summary>
    /// AG Grid's real wire shape: an array of plain objects keyed by colId (<c>{"id": 1, "name":
    /// "Bob"}</c>) — NOT the positional arrays every adapter's own page type (and
    /// <see cref="GridPageResult.Rows"/>, which mirrors them) uses internally. That positional shape was
    /// chosen to match <c>Craticula.Export.Csv.CsvWriter</c>'s row-based consumption model
    /// (<see cref="Craticula.Core.Export.ExportRow"/>'s own precedent); it was never meant to be the wire
    /// shape a grid actually reads, which is exactly the transposition <see cref="LoadSuccessParamsMapper"/>
    /// exists to do once instead of four times.
    /// </summary>
    [JsonPropertyName("rows")]
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }

    /// <summary>
    /// The total row/bucket count, when known — its PRESENCE is what tells AG Grid it has reached the
    /// end of the dataset and can stop requesting further blocks; its absence (<see langword="null"/>)
    /// tells it to keep asking. Derived from <see cref="GridPageResult.RowCount"/> and
    /// <see cref="GridPageResult.IsLastPage"/> plus the request's own <c>startRow</c> — precedence is
    /// EXACTNESS first, not "RowCount always wins": an exact <c>RowCount</c> wins whenever present, but
    /// when <c>RowCount</c> is present and only <see cref="GridPageResult.RowCountIsApproximate"/>, the
    /// exact value derivable from <c>IsLastPage == true</c> (<c>startRow + rows.Count</c>) wins instead;
    /// an approximate <c>RowCount</c> is still used when <c>IsLastPage</c> is false/unknown, since no
    /// better signal exists. See <see cref="LoadSuccessParamsMapper"/> for the full precedence table.
    /// </summary>
    [JsonPropertyName("lastRow")]
    public long? LastRow { get; init; }

    /// <summary>
    /// <see langword="null"/> in the overwhelming majority of responses — see
    /// <see cref="GroupLevelInfo"/>'s own remarks for the one case this is populated.
    /// </summary>
    [JsonPropertyName("groupLevelInfo")]
    public GroupLevelInfo? GroupLevelInfo { get; init; }
}
