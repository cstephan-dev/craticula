namespace Fixture.Sql;

public sealed record FilterRequest(string Value);

public static class RawInterpolation
{
    // The SEC-2 defect itself: the client's value is written into the statement instead of bound.
    public static string Where(FilterRequest request) =>
        $"SELECT * FROM \"orders\" WHERE \"status\" = '{request.Value}'";
}
