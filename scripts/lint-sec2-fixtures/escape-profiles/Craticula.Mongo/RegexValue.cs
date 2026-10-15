namespace Fixture.Mongo;

public static class RegexValue
{
    // On a document backend the escaped pattern is a JSON VALUE ($regex), never query text (SEC-4).
    public static string StartsWith(string value) => $"^{MongoRegexEscaping.Escape(value)}";
}
