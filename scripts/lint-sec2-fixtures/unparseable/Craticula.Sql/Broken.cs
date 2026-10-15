namespace Fixture.Sql;

public static class Broken
{
    // The closing brace of the class is missing: the lint cannot vouch for a file it cannot read.
    public static string Where() => "SELECT 1";
