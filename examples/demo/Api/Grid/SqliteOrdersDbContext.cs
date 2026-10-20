using System.Globalization;
using Craticula.Core.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Demo.Api.Grid;

/// <summary>
/// The <c>orders</c> model on the SQLite file (route <c>ef-sqlite</c>); the adapter ships the SQLite semantics preset.
/// <para>SQLite compares TEXT character by character and the seed stores datetimes as <c>yyyy-MM-ddTHH:mm:ss.fffZ</c> (SEM-4, the host obligation of
/// the SQLite arm). EF's own SQLite mapping writes a <see cref="DateTime"/> parameter as <c>yyyy-MM-dd HH:mm:ss</c> (a space, no <c>Z</c>), and on the
/// bound's own date <c>T</c> sorts after a space, so every row of that date counts as later than a bound at noon: range filters with a non-midnight
/// bound and the keyset walk of a CSV export sorted by a datetime then returned wrong rows (measured). This context therefore maps every
/// datetime to and from the stored text form.</para>
/// </summary>
public sealed class SqliteOrdersDbContext(DbContextOptions<SqliteOrdersDbContext> options, GridSchema schema)
    : SchemaDbContext<SqliteOrdersDbContext>(options, schema)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTime>().HaveConversion<StoredInstantConverter>();

    private sealed class StoredInstantConverter() : ValueConverter<DateTime, string>(
        value => ToStored(value),
        text => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal))
    {
        private static string ToStored(DateTime value) =>
            (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}
