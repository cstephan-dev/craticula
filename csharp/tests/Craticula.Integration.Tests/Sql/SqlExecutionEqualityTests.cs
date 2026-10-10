using Craticula.Integration.Tests.Docker;

namespace Craticula.Integration.Tests.Containers.Sql;

// One collection (and so one container) per engine for the whole run; xUnit runs a collection's tests one at a time,
// which is what lets every fixture reuse the one table name. The four collections run in parallel with each other.

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresEngine>
{
    public const string Name = "xeq-postgres";
}

[CollectionDefinition(Name)]
public sealed class MssqlCollection : ICollectionFixture<MssqlEngine>
{
    public const string Name = "xeq-mssql";
}

[CollectionDefinition(Name)]
public sealed class MySqlCollection : ICollectionFixture<MySqlEngine>
{
    public const string Name = "xeq-mysql";
}

[CollectionDefinition(Name)]
public sealed class SqliteCollection : ICollectionFixture<SqliteEngine>
{
    public const string Name = "xeq-sqlite";
}

/// <summary>T-1b on PostgreSQL 16 (<c>postgres:16</c>): every fixture with an <c>expected/postgres.artifact.json</c>.</summary>
[Collection(PostgresCollection.Name)]
public class PostgresExecutionEqualityTests(PostgresEngine engine)
{
    public static IEnumerable<object[]> Fixtures() => SqlExecutionRunner.FixturesFor("postgres");

    [DockerTheory]
    [MemberData(nameof(Fixtures))]
    public Task Fixture_Returns_The_Expected_Rows_On_Postgres(string name) => SqlExecutionRunner.AssertMatchesOracleAsync(engine, name);
}

/// <summary>T-1b on SQL Server 2022: every fixture with an <c>expected/mssql.artifact.json</c>.</summary>
[Collection(MssqlCollection.Name)]
public class MssqlExecutionEqualityTests(MssqlEngine engine)
{
    public static IEnumerable<object[]> Fixtures() => SqlExecutionRunner.FixturesFor("mssql");

    [DockerTheory]
    [MemberData(nameof(Fixtures))]
    public Task Fixture_Returns_The_Expected_Rows_On_Mssql(string name) => SqlExecutionRunner.AssertMatchesOracleAsync(engine, name);
}

/// <summary>T-1b on MySQL 8.4 (<c>mysql:8.4</c>): every fixture with an <c>expected/mysql.artifact.json</c>.</summary>
[Collection(MySqlCollection.Name)]
public class MySqlExecutionEqualityTests(MySqlEngine engine)
{
    public static IEnumerable<object[]> Fixtures() => SqlExecutionRunner.FixturesFor("mysql");

    [DockerTheory]
    [MemberData(nameof(Fixtures))]
    public Task Fixture_Returns_The_Expected_Rows_On_MySql(string name) => SqlExecutionRunner.AssertMatchesOracleAsync(engine, name);
}

/// <summary>T-1b on SQLite, in-process (no Docker): every fixture with an <c>expected/sqlite.artifact.json</c>.</summary>
[Collection(SqliteCollection.Name)]
public class SqliteExecutionEqualityTests(SqliteEngine engine)
{
    public static IEnumerable<object[]> Fixtures() => SqlExecutionRunner.FixturesFor("sqlite");

    [Theory]
    [MemberData(nameof(Fixtures))]
    public Task Fixture_Returns_The_Expected_Rows_On_Sqlite(string name) => SqlExecutionRunner.AssertMatchesOracleAsync(engine, name);
}
