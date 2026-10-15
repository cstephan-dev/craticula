using System.Globalization;

namespace Fixture.Dapper;

public sealed class ReaderException(string message) : Exception(message);

public static class ValueReader
{
    // Echoes a database value, bounded, into exception messages: never query text.
    internal static string Truncate(object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return text.Length <= 32 ? text : text[..32] + "...";
    }

    public static long ToInt64(object raw) =>
        raw is long value ? value : throw new ReaderException($"Column value '{Truncate(raw)}' is not an integer.");

    public static string Lookup(object userValue) => "SELECT * FROM t WHERE c = '" + Truncate(userValue) + "'";
}
