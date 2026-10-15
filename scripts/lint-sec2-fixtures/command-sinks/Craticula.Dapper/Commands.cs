using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Fixture.Dapper;

public sealed record Request(string Value);

public static class Commands
{
    // Raw values handed straight to the database: no string is built around them, the command is the sink.
    public static CommandDefinition Definition(Request request) => new CommandDefinition(request.Value);

    public static void Text(IDbCommand command, Request request) => command.CommandText = request.Value;

    public static SqlCommand Constructed(Request request, SqlConnection connection) =>
        new SqlCommand(request.Value, connection);

    public static SqlCommand Initialized(Request request) => new SqlCommand { CommandText = request.Value };

    public static int Raw(DbContext context, Request request) => context.Database.ExecuteSqlRaw(request.Value);

    public static IQueryable<T> FromRaw<T>(DbSet<T> set, Request request) where T : class =>
        set.FromSqlRaw(request.Value);

    public static Task<int> Dapper(IDbConnection connection, Request request) => connection.ExecuteAsync(request.Value);

    // The compiled statement, through a local, a parameter and a `with` copy, and a command object: not raw text.
    public static async Task<int> Compiled(IDbConnection connection, object request, object dialect)
    {
        var query = SqlQueryBuilder.Build(request, dialect);
        var command = new CommandDefinition(query.Sql);
        await connection.ExecuteAsync(command);
        var count = query with { Sql = $"SELECT COUNT(*) FROM ({query.Sql}) c" };
        return await connection.ExecuteScalarAsync<int>(Prepare(count));
    }

    private static CommandDefinition Prepare(SqlQuery query) => new CommandDefinition(query.Sql);
}
