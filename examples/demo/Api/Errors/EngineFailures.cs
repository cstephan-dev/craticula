using System.Collections.Immutable;
using System.Net.Sockets;
using Demo.Api.Seed;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Craticula.OpenSearch.Client;
using MongoDB.Driver;
using MySqlConnector;
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
    /// -2 (the driver's own timeout, connect or command) and 4060 (the database cannot be opened: not created yet), plus 11001
    /// (the host name does not resolve). 18456 (the login failed) is not listed: a refused login is a setting error and answers 500,
    /// like MySQL 1045 and PostgreSQL 28P01. A refused connection is not a number at all on Linux:
    /// SqlClient 7.1.0 reports Number 0 with an inner <see cref="SocketException"/> (measured), so that case is recognised by its
    /// inner exception; see <see cref="IsSqlServerUnavailable"/>.
    /// </summary>
    public static readonly ImmutableHashSet<int> SqlServerConnectionFailureNumbers = [2, 53, -1, -2, 258, 4060, 11001];

    /// <summary>
    /// MySqlConnector reports every failure to reach the server as <c>MySqlException.Number</c> 1042
    /// (<see cref="MySqlErrorCode.UnableToConnectToHost"/>), measured against MySQL 8.4.11 with 2.6.2: a refused port, a stopped
    /// container, a host name that does not resolve, an unroutable address and a server that accepts the connection and never
    /// answers (message "Connect Timeout expired.", inner MySqlException). None has an inner <see cref="SocketException"/>.
    /// A refused login (1045 access denied, 1044 no access to the database) is a setting error, and any statement error is a bug:
    /// both stay a 500.
    /// </summary>
    public static readonly ImmutableHashSet<int> MySqlConnectionFailureNumbers = [(int)MySqlErrorCode.UnableToConnectToHost];

    // Order does not matter: the first predicate that matches wins and every match means the same thing.
    private static ImmutableArray<Func<Exception, bool>> declared =
    [
        // PostgreSQL: a driver failure to reach the server, or a server error that says the server is going away or refusing
        // connections, see IsPostgresUnavailable. Any other PostgresException (a bad statement, a missing table, a refused login) is a bug.
        exception => exception is NpgsqlException and not PostgresException,
        exception => exception is PostgresException postgres && IsPostgresUnavailable(postgres),
        exception => exception is MongoException,
        // SQLite: the file cannot be opened (missing, no permission) or another connection holds the lock (primary result
        // codes 14 SQLITE_CANTOPEN, 5 SQLITE_BUSY, 6 SQLITE_LOCKED). Any other SqliteException is a bug and stays a 500.
        exception => exception is SqliteException { SqliteErrorCode: 14 or 5 or 6 },
        exception => exception is SqliteVersionException,
        // SQL Server: the server cannot be reached, see SqlServerConnectionFailureNumbers. Any other
        // SqlException (a bad statement, a missing table) is a bug and stays a 500.
        exception => exception is SqlException sql && IsSqlServerUnavailable(sql),
        // MySQL: the server cannot be reached, see MySqlConnectionFailureNumbers. Any other MySqlException (a refused login, a bad
        // statement, a missing table) stays a 500.
        exception => exception is MySqlException mysql && MySqlConnectionFailureNumbers.Contains(mysql.Number),
        // OpenSearch: the low-level client does not throw, the adapter wraps a failed call; see IsOpenSearchUnavailable.
        exception => exception is OpenSearchClientCompilationException opensearch && IsOpenSearchUnavailable(opensearch),
        exception => exception is TimeoutException,
        exception => exception is SocketException
    ];

    /// <summary>
    /// A <see cref="PostgresException"/> that means "the server cannot be reached now": the class 57 operator-intervention states that
    /// end or refuse connections (57P01 admin shutdown, 57P02 crash shutdown, 57P03 cannot connect now, 57P05 idle-session timeout;
    /// measured: the first request on a pooled connection after <c>docker compose stop</c> fails with 57P01 "terminating connection due
    /// to administrator command"), 53300 (too many connections) and the connection exceptions Npgsql itself calls transient (08000,
    /// 08001, 08003, 08004, 08006, 08007; not 08P01, a protocol violation, which is a bug). 57014 (a cancelled statement), a refused
    /// login (28P01, 28000), a missing database (3D000) and every statement error are not listed: they are settings or bugs.
    /// </summary>
    public static bool IsPostgresUnavailable(PostgresException exception) =>
        exception.SqlState is "57P01" or "57P02" or "57P03" or "57P05" or "53300"
            or "08000" or "08001" or "08003" or "08004" or "08006" or "08007";

    /// <summary>A <see cref="SqlException"/> that is a connection failure: a declared error number, or a refused/unreachable socket.
    /// Anything else (a bad statement, a missing table, an untrusted certificate: a configuration error) is not.</summary>
    public static bool IsSqlServerUnavailable(SqlException exception) =>
        SqlServerConnectionFailureNumbers.Contains(exception.Number) || exception.InnerException is SocketException;

    /// <summary>
    /// An OpenSearch call that failed because the cluster cannot be reached or says it is overloaded (HTTP 503 or 429), or because the
    /// transport failed: no HTTP status, and the transport exception inside. Measured against OpenSearch.Client 2.1.0 (every path
    /// through the adapter, the distinct-values call and the export stream, which passes the HTTP status on since bug #2523): a refused port and a host name that does not resolve carry an
    /// <see cref="HttpRequestException"/> (inner <see cref="SocketException"/>); a server that accepts and never answers carries a
    /// <see cref="TaskCanceledException"/> (inner <see cref="TimeoutException"/>) after the 10 s request timeout. A 4xx or other 5xx
    /// answer from a running cluster (a bad mapping, a rejected body) has a status and is a bug: it is not listed.
    /// </summary>
    public static bool IsOpenSearchUnavailable(OpenSearchClientCompilationException exception) =>
        exception.HttpStatusCode is 503 or 429
        || (exception.HttpStatusCode is null
            && exception.InnerException is HttpRequestException or SocketException or TimeoutException or TaskCanceledException);

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
