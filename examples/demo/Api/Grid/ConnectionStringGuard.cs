namespace Demo.Api.Grid;

/// <summary>
/// An optional engine's connection string comes from the operator. A malformed one (<c>Sever=x</c>, no <c>=</c>, <c>Encrypt=maybe</c>) makes
/// the driver's builder throw; that must surface as one clear message naming the variable, at startup (the API) or as a reported
/// failure for that provider (<c>seed</c>), never at request time. The message never repeats the setting or the driver's text,
/// because either may carry the password.
/// </summary>
public static class ConnectionStringGuard
{
    public static string Message(string envVar, string engine) =>
        $"{envVar} is not a valid {engine} connection string: a keyword or value is malformed (the setting is not echoed).";

    /// <summary>Runs <paramref name="build"/>; a malformed string becomes the exception <paramref name="invalid"/> makes (no inner exception).</summary>
    public static T Build<T>(string connectionString, Func<string, T> build, Func<Exception> invalid)
    {
        try
        {
            return build(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw invalid();
        }
    }
}
