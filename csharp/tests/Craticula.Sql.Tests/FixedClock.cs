using System.Globalization;

namespace Craticula.Sql.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> pinned to a single instant, so a fixture carrying a
/// <c>requestContext.now</c> resolves its AG-2 date presets reproducibly. Mirrors
/// <c>Craticula.AgGrid.Tests.FixedClock</c> (internal to that assembly); hand-written rather than
/// taken from <c>Microsoft.Extensions.TimeProvider.Testing</c>, since AR-3 keeps this solution at
/// zero third-party packages.
/// </summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    // Note: `System.Globalization.X` cannot be written unqualified inside a TimeProvider subclass —
    // `System` binds to the inherited TimeProvider.System property first. Hence the using above.
    public static FixedClock At(string iso8601Instant) =>
        new(DateTimeOffset.Parse(iso8601Instant, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    public override DateTimeOffset GetUtcNow() => now;
}
