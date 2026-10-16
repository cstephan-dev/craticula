using System.Net.Sockets;
using Demo.Api.Seed;
using Npgsql;

namespace Demo.Api.Tests;

/// <summary>A [Fact] that is skipped, with the reason, when the demo databases do not answer on their ports.
/// It probes with a TCP connect, so a closed port is a skip, never a failure.</summary>
public sealed class RequiresDatabasesFactAttribute : FactAttribute
{
    public RequiresDatabasesFactAttribute()
    {
        var pg = new NpgsqlConnectionStringBuilder(DemoSettings.Postgres);
        var mongo = new MongoDB.Driver.MongoUrl(DemoSettings.Mongo).Server;

        if (!IsOpen(pg.Host ?? "127.0.0.1", pg.Port))
        {
            Skip = $"PostgreSQL is not reachable on {pg.Host}:{pg.Port}. Start it with `docker compose up -d` in examples/demo.";
        }
        else if (!IsOpen(mongo.Host, mongo.Port))
        {
            Skip = $"MongoDB is not reachable on {mongo.Host}:{mongo.Port}. Start it with `docker compose up -d` in examples/demo.";
        }
    }

    private static bool IsOpen(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(1)) && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            return false;
        }
    }
}
