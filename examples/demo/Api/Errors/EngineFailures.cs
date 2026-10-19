using System.Collections.Immutable;
using System.Net.Sockets;
using Demo.Api.Seed;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
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
    /// <summary>
    /// The <c>SqlException.Number</c> values that mean "the engine cannot be reached": 2 and 53 (the server or instance was not
    /// found or not accessible), -1 (a connection-level failure: refused, reset), 258 (the wait operation timed out),
    /// -2 (the driver's own timeout, connect or command), 4060 (the database cannot be opened: not created yet) and
    /// 18456 (the login failed), plus 11001 (the host name does not resolve). A refused connection is not a number at all on Linux:
    /// SqlClient 7.1.0 reports Number 0 with an inner <see cref="SocketException"/> (measured), so that case is recognised by its
    /// inner exception; see <see cref="IsSqlServerUnavailable"/>.
    /// </summary>
    public static readonly ImmutableHashSet<int> SqlServerConnectionFailureNumbers = [2, 53, -1, -2, 258, 4060, 11001, 18456];

    // Order does not matter: the first predicate that matches wins and every match means the same thing.
    private static ImmutableArray<Func<Exception, bool>> declared =
    [
        exception => exception is NpgsqlException,
        exception => exception is MongoException,
        // SQLite: the file cannot be opened (missing, no permission) or another connection holds the lock (primary result
        // codes 14 SQLITE_CANTOPEN, 5 SQLITE_BUSY, 6 SQLITE_LOCKED). Any other SqliteException is a bug and stays a 500.
        exception => exception is SqliteException { SqliteErrorCode: 14 or 5 or 6 },
        exception => exception is SqliteVersionException,
        // SQL Server: the server cannot be reached or refuses the login, see SqlServerConnectionFailureNumbers. Any other
        // SqlException (a bad statement, a missing table) is a bug and stays a 500.
        exception => exception is SqlException sql && IsSqlServerUnavailable(sql),
        exception => exception is TimeoutException,
        exception => exception is SocketException
    ];

    /// <summary>A <see cref="SqlException"/> that is a connection failure: a declared error number, or a refused/unreachable socket.
    /// Anything else (a bad statement, a missing table, an untrusted certificate: a configuration error) is not.</summary>
    public static bool IsSqlServerUnavailable(SqlException exception) =>
        SqlServerConnectionFailureNumbers.Contains(exception.Number) || exception.InnerException is SocketException;

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
