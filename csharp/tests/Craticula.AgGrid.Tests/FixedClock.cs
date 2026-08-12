using System.Globalization;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> pinned to a single instant — the whole reason
/// <see cref="AgGridParseOptions.Clock"/> exists, and the mechanism the conformance harness uses to
/// honour a <c>date-presets/</c> fixture's <c>requestContext.now</c>. Hand-written rather than
/// pulled from <c>Microsoft.Extensions.TimeProvider.Testing</c>: AR-3 keeps this solution at zero
/// third-party packages, and nothing here needs that package's timer/advance machinery.
/// </summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    // Note: `System.Globalization.X` cannot be written unqualified inside a TimeProvider subclass —
    // `System` binds to the inherited TimeProvider.System property first. Hence the using above.
    public static FixedClock At(string iso8601Instant) =>
        new(DateTimeOffset.Parse(iso8601Instant, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    public override DateTimeOffset GetUtcNow() => now;
}
