namespace KickFlight.BootstrapApi.PlayerStore;

using Npgsql;

// Picks the store. A connection string means PostgreSQL; no connection string means the JSON files.
//
// The fallback is not a second production path: it exists so `dotnet test` and a local `start-server.bat`
// need no database, which keeps the suite hermetic. The deployment always sets the connection string, so
// player state there is always in PostgreSQL. Anything added to IPlayerStore that only the Postgres
// implementation can honour must therefore be verified against the deployment, not just the suite.
public static class PlayerStoreFactory
{
    public const string ConnectionStringKey = "PlayerStore:ConnectionString";
    public const string DatabaseUrlKey = "PlayerStore:DatabaseUrl";

    /// <summary>
    /// The PostgreSQL connection string the deployment configured, or null/empty for the JSON fallback. The ops
    /// telemetry store (Ops/OpsStores.cs) uses the same database, so both stores always agree on the mode.
    /// </summary>
    public static string? ResolveConnectionString(IConfiguration configuration)
    {
        var databaseUrl = configuration[DatabaseUrlKey];
        return !string.IsNullOrWhiteSpace(databaseUrl)
            ? ConvertDatabaseUrl(databaseUrl)
            : configuration[ConnectionStringKey] ?? configuration.GetConnectionString("KickFlight");
    }

    public static IPlayerStore Create(IServiceProvider services, IConfiguration configuration)
    {
        var connectionString = ResolveConnectionString(configuration);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            services.GetRequiredService<ILogger<JsonPlayerStore>>()
                .LogWarning("No {Key} configured: player state is in per-user JSON files, not PostgreSQL",
                    ConnectionStringKey);
            return new JsonPlayerStore(
                services.GetRequiredService<ILogger<JsonPlayerStore>>(),
                services.GetRequiredService<IWebHostEnvironment>());
        }

        return new PostgresPlayerStore(
            services.GetRequiredService<ILogger<PostgresPlayerStore>>(),
            connectionString);
    }

    private static string ConvertDatabaseUrl(string databaseUrl)
    {
        if (!Uri.TryCreate(databaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
        {
            throw new InvalidOperationException(
                $"{DatabaseUrlKey} must be a postgres:// or postgresql:// URL.");
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        if (userInfo.Length != 2 || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException(
                $"{DatabaseUrlKey} must include a host, username, and password.");
        }

        var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (string.IsNullOrWhiteSpace(database))
        {
            throw new InvalidOperationException($"{DatabaseUrlKey} must include a database name.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = database,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = Uri.UnescapeDataString(userInfo[1]),
            SslMode = SslMode.Require
        };

        return builder.ConnectionString;
    }
}
