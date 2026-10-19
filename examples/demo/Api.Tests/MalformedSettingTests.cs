using Demo.Api.Grid;
using Demo.Api.Seed;

namespace Demo.Api.Tests;

/// <summary>
/// A malformed <c>DEMO_SQLSERVER</c> / <c>DEMO_MYSQL</c> is an operator's setting error: the API stops at startup with a message that
/// names the variable (never every route answering 500 at request time), and <c>seed</c> reports that provider as failed and
/// goes on. No engine is contacted, so none of these needs a database.
/// </summary>
public sealed class MalformedSettingTests
{
    private const string Secret = "S3cr3t-not-real";

    public static TheoryData<string, string, string> Settings => new()
    {
        { "sqlserver", DemoSettings.SqlServerEnvVar, "Sever=x;Password=" + Secret },
        { "sqlserver", DemoSettings.SqlServerEnvVar, "Data Source=127.0.0.1;Password=" + Secret + ";Encrypt=maybe" },
        { "sqlserver", DemoSettings.SqlServerEnvVar, "no equals sign " + Secret },
        { "mysql", DemoSettings.MySqlEnvVar, "Sever=x;Password=" + Secret },
        { "mysql", DemoSettings.MySqlEnvVar, "Server=127.0.0.1;Password=" + Secret + ";SslMode=maybe" },
        { "mysql", DemoSettings.MySqlEnvVar, "no equals sign " + Secret }
    };

    [Theory]
    [MemberData(nameof(Settings))]
    public void Startup_WithAMalformedSetting_FailsFastNamingTheVariable_AndNeverTheSetting(string provider, string envVar, string setting)
    {
        _ = provider;
        var failure = Assert.Throws<InvalidOperationException>(() => DemoApp.Build([], builder =>
        {
            builder.Configuration["Urls"] = "http://127.0.0.1:0";
            builder.Configuration[envVar] = setting;
            // Keep the other optional engine out of the way whatever the machine's environment holds.
            var other = envVar == DemoSettings.SqlServerEnvVar ? DemoSettings.MySqlEnvVar : DemoSettings.SqlServerEnvVar;
            builder.Configuration[other] = "";
        }));

        Assert.StartsWith(envVar + " is not a valid ", failure.Message);
        Assert.DoesNotContain(Secret, failure.Message);
        Assert.DoesNotContain(setting, failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task Seed_WithAMalformedSetting_IsAReportedFailureNotACrash(string provider, string envVar, string setting)
    {
        var target = new SeedTarget(
            provider,
            rows => provider == "sqlserver"
                ? SqlServerSeeder.SeedAsync(setting, rows)
                : MySqlSeeder.SeedAsync(setting, rows),
            count => $"{count}",
            "hint for " + provider,
            Default: false);
        var other = new SeedTarget("other", _ => Task.FromResult(20000L), count => $"other ok {count}", "hint other", Default: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await SeedCommand.RunAsync([provider, "other"], [target, other], output, error);

        Assert.Equal(2, exit);
        Assert.Contains(envVar + " is not a valid ", error.ToString());
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Contains("hint for " + provider, error.ToString());
        Assert.Contains("other ok", output.ToString());
    }
}
