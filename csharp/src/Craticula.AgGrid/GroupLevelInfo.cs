using System.Text.Json.Serialization;
using Craticula.Core.Grid;

namespace Craticula.AgGrid;

/// <summary>
/// The one fact this project already knows about a grouped level's own <see cref="GridPageResult.RowCount"/>
/// that AG Grid's own <c>LoadSuccessParams.groupLevelInfo</c> has no opinion on — AG Grid types that
/// member <c>any</c> and assigns it no interpreted meaning of its own; it is attached to the group row
/// whose children a response is delivering purely so an application's own custom cell renderer can read
/// it back later. <see cref="LoadSuccessParamsMapper"/> populates this only when
/// <see cref="GridPageResult.RowCountIsApproximate"/> is <see langword="true"/> — every other response
/// leaves <see cref="LoadSuccessParams.GroupLevelInfo"/> <see langword="null"/>, matching AG Grid's own
/// expectation that the member is usually absent. <see cref="RowCountExact"/> then reflects whether the
/// WIRE <c>lastRow</c> that was actually emitted ended up exact regardless — OpenSearch's grouped
/// <c>cardinality</c> path can also carry an exact total derivable from <see cref="GridPageResult.IsLastPage"/>,
/// which wins over the approximate <c>RowCount</c> that triggered this type's own presence; see
/// <see cref="LoadSuccessParamsMapper"/>'s precedence table for the full derivation.
///
/// <para><b>Deliberately not an attempt at cross-level "expansion state".</b> A prior story's own doc
/// comment guessed <c>groupLevelInfo</c> might carry per-level row counts or expansion state for
/// sibling/ancestor levels — there is no IR support for that at all
/// (<see cref="Craticula.Core.Ir.GroupSpec"/> is strictly one drill-down level per request) and inventing
/// it now would be exactly the kind of ungrounded design this project's own conventions warn against.
/// This type stays narrow: one already-true fact about the level the response is for, nothing more.</para>
/// </summary>
public sealed record GroupLevelInfo
{
    /// <summary>
    /// Whether the WIRE <see cref="LoadSuccessParams.LastRow"/> value this <see cref="GroupLevelInfo"/>
    /// is attached to is exact, not just whether the raw <see cref="GridPageResult.RowCount"/> happened
    /// to be approximate. Usually <see langword="false"/> (the approximate <c>cardinality</c> count was
    /// the value used), but <see langword="true"/> when <see cref="GridPageResult.IsLastPage"/> supplied
    /// an exact derived total that won over the approximate <c>RowCount</c> instead — see
    /// <see cref="LoadSuccessParamsMapper"/>'s precedence table. A host consuming this can e.g. show a
    /// "~" before the group count in the UI only when this is <see langword="false"/>.
    /// </summary>
    [JsonPropertyName("rowCountExact")]
    public required bool RowCountExact { get; init; }
}
