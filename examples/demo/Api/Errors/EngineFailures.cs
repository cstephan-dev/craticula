using System.Collections.Immutable;
using System.Net.Sockets;
using MongoDB.Driver;
using Npgsql;

namespace Demo.Api.Errors;

/// <summary>
/// The one place that says which exceptions mean "this engine cannot be reached": the API answers them with 503
/// <c>backend-unavailable</c> and the <c>seed</c> command reports them per provider. A new engine declares its
/// connection exceptions here (one more entry in <see cref="declared"/>); nothing else needs to change.
/// </summary>
public static class EngineFailures
{
    // Order does not matter: the first predicate that matches wins and every match means the same thing.
    private static ImmutableArray<Func<Exception, bool>> declared =
    [
        exception => exception is NpgsqlException,
        exception => exception is MongoException,
        exception => exception is TimeoutException,
        exception => exception is SocketException
    ];

    /// <summary>True when the exception is a connection failure of an engine.</summary>
    public static bool IsUnavailable(Exception exception)
    {
        foreach (var predicate in declared)
        {
            if (predicate(exception))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Declares an exception type (and its subtypes) an engine connection failure. Tests use it for a fake engine.</summary>
    public static void Declare<TException>() where TException : Exception =>
        ImmutableInterlocked.Update(ref declared, current => current.Add(exception => exception is TException));
}
