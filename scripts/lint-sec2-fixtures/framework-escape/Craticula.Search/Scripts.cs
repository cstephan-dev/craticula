namespace Fixture.Search;

public static class Scripts
{
    // A framework Escape* method is not a pattern escaper: its result comes from code the lint cannot see.
    public static string PriceAbove(string userValue) => "doc['price'].value > " + Uri.EscapeDataString(userValue);

    // A listed escaper, by type and name: a pattern value on a document backend.
    public static string Prefix(string userValue) => $"{SearchRegexEscaping.Escape(userValue)}.*";
}
