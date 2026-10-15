using System.Text;

namespace Fixture.Mongo;

public static class MongoRegexEscaping
{
    // The reviewed loop escapes the value character by character; the line above it appends the value whole.
    public static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length);
        escaped.Append(value);
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                escaped.Append(character);
                continue;
            }

            escaped.Append('\\').Append(character);
        }

        return escaped.ToString();
    }
}
