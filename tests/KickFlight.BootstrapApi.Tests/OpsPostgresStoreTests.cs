using KickFlight.BootstrapApi.Ops;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The PostgreSQL ops store (Ops/OpsStores.cs) only runs in the deployment, so its schema and every read/write is
// pinned here against a real throwaway database. Gated like PostgresSocialMigrationTests: without
// KF_TEST_OPS_POSTGRES_CONNECTION_STRING the facts are skipped, so CI and local runs stay green.
public sealed class OpsPostgresStoreTests
{
    [OpsPostgresIntegrationFact]
    public async Task Schema_migrates_and_every_row_type_round_trips()
    {
        var connectionString = Environment.GetEnvironmentVariable("KF_TEST_OPS_POSTGRES_CONNECTION_STRING")!;
        await ResetSchemaAsync(connectionString);

        var store = new PostgresOpsStore(connectionString);
        await store.InitializeAsync(CancellationToken.None);
        // Re-initialising must be a no-op (the version row makes every migration conditional).
        await store.InitializeAsync(CancellationToken.None);

        var t0 = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var run = new ServerRunRow
        {
            Id = "run-20261010120000-abc123456", StartedAt = t0, ReleaseSha = "deadbeef",
            MasterVersion = "v1", MinClientVersion = "2.11.0"
        };
        var battle = new BattleRow
        {
            BattleId = "battle-1", RunId = run.Id, CreatedAt = t0, UpdatedAt = t0.AddMinutes(1),
            RuleId = 7, MatchType = 2, FieldId = 801, Humans = 2, Bots = 2, Custom = false,
            FirstStartAt = t0.AddSeconds(30), BattleTimeSeconds = 180, Outcome = OpsStatusRules.Clean
        };
        var participant = new ParticipantRow
        {
            BattleId = "battle-1", UserId = "1000001", BattleEntryId = "entry-1", CreatedAt = t0,
            UpdatedAt = t0.AddMinutes(1), KickerId = 3010001, CostumeId = 64, Team = 0, IsDiag = true,
            AppVersion = "2.11.2", DeviceName = "Pixel", OsVersion = "14", Ip = "1.2.3.4",
            StartAt = t0.AddSeconds(30), EndAt = t0.AddMinutes(4), ResultAt = t0.AddMinutes(4),
            ReconnectBegins = 1, ReconnectFailedCause = null, DisconnectCause = null, ExceptionCount = 2,
            Status = OpsStatusRules.ReconnectedCompleted
        };
        var exception = new ClientEventRow
        {
            At = t0.AddMinutes(2), RunId = run.Id, UserId = "1000001", BattleId = "battle-1",
            Kind = OpsFingerprint.ExceptionKind, AppVersion = "2.11.2", Device = "Pixel", Os = "14",
            Fingerprint = "0123456789abcdef",
            Payload = new System.Text.Json.Nodes.JsonObject
            {
                ["key"] = "PhotonGetResionPingFailed",
                ["value"] = new System.Text.Json.Nodes.JsonObject { ["cause"] = "[Analysis] boom" },
                ["signature"] = "NullReferenceException\nat Foo ()"
            }
        };
        var plainEvent = new ClientEventRow
        {
            At = t0.AddMinutes(3), RunId = run.Id, UserId = "1000001", BattleId = "battle-1",
            Kind = "GameReconnectBegin", AppVersion = "2.11.2", Device = "Pixel", Os = "14",
            Payload = new System.Text.Json.Nodes.JsonObject { ["key"] = "GameReconnectBegin", ["value"] = 1 }
        };

        var batch = new OpsWriteBatch();
        batch.Runs.Add(run);
        batch.Battles.Add(battle);
        batch.Participants.Add(participant);
        batch.Events.Add(exception);
        batch.Events.Add(plainEvent);
        await store.WriteAsync(batch, CancellationToken.None);

        // Upserts must take the latest snapshot, not fail on the primary key. Events are append-only, so the
        // second write only carries the changed rows.
        var update = new OpsWriteBatch();
        update.Battles.Add(battle with { UpdatedAt = t0.AddMinutes(2), Outcome = OpsStatusRules.Partial });
        update.Participants.Add(participant with { UpdatedAt = t0.AddMinutes(2), ExceptionCount = 3 });
        await store.WriteAsync(update, CancellationToken.None);

        var runs = await store.LoadRunsAsync(t0.AddHours(-1), CancellationToken.None);
        Assert.Single(runs);
        Assert.Equal("deadbeef", runs[0].ReleaseSha);

        var battles = await store.LoadBattlesAsync(t0.AddHours(-1), CancellationToken.None);
        Assert.Single(battles);
        Assert.Equal(OpsStatusRules.Partial, battles[0].Outcome);
        Assert.Equal(2, battles[0].Humans);

        var participants = await store.LoadParticipantsAsync(t0.AddHours(-1), CancellationToken.None);
        Assert.Single(participants);
        Assert.Equal(OpsStatusRules.ReconnectedCompleted, participants[0].Status);
        Assert.Equal(1, participants[0].ReconnectBegins);
        Assert.Equal(3, participants[0].ExceptionCount);

        var loadedBattle = await store.LoadBattleAsync("battle-1", CancellationToken.None);
        Assert.NotNull(loadedBattle);
        var loadedParticipants = await store.LoadBattleParticipantsAsync("battle-1", CancellationToken.None);
        Assert.Single(loadedParticipants);

        var events = await store.LoadEventsAsync(new OpsEventQuery(t0.AddHours(-1)), CancellationToken.None);
        Assert.Equal(2, events.Count);
        Assert.Equal(exception.Fingerprint, events[0].Fingerprint);
        Assert.Equal("PhotonGetResionPingFailed", events[0].Payload["key"]!.GetValue<string>());

        // The kind filter is the `kind = any($n)` branch.
        var exceptionOnly = await store.LoadEventsAsync(
            new OpsEventQuery(t0.AddHours(-1), Kinds: [OpsFingerprint.ExceptionKind]), CancellationToken.None);
        Assert.Single(exceptionOnly);
        Assert.Equal(OpsFingerprint.ExceptionKind, exceptionOnly[0].Kind);

        // The battle filter and the limit/order combination.
        var byBattle = await store.LoadEventsAsync(
            new OpsEventQuery(t0.AddHours(-1), BattleId: "battle-1", Limit: 1), CancellationToken.None);
        Assert.Single(byBattle);
        Assert.Equal("GameReconnectBegin", byBattle[0].Kind); // the newest of the two

        var kinds = await store.CountEventsByKindAsync(t0.AddHours(-1), CancellationToken.None);
        Assert.Equal(1, kinds[OpsFingerprint.ExceptionKind]);
        Assert.Equal(1, kinds["GameReconnectBegin"]);

        // A window that starts after every row yields nothing.
        Assert.Empty(await store.LoadBattlesAsync(t0.AddDays(2), CancellationToken.None));

        // Retention: pruning before a cutoff removes every table's old rows.
        await store.PruneAsync(t0.AddDays(1), CancellationToken.None);
        Assert.Empty(await store.LoadRunsAsync(DateTime.MinValue, CancellationToken.None));
        Assert.Empty(await store.LoadBattlesAsync(DateTime.MinValue, CancellationToken.None));
        Assert.Empty(await store.LoadParticipantsAsync(DateTime.MinValue, CancellationToken.None));
        Assert.Empty(await store.LoadEventsAsync(new OpsEventQuery(DateTime.MinValue), CancellationToken.None));
        Assert.Empty(await store.CountEventsByKindAsync(DateTime.MinValue, CancellationToken.None));
    }

    [OpsPostgresIntegrationFact]
    public async Task Unknown_database_fails_initialize_without_killing_the_process()
    {
        // The API must survive a database that is down: PostgresOpsStore must only throw from InitializeAsync
        // (the OpsTelemetryService retries in the background), never from its constructor or a Record* call.
        var store = new PostgresOpsStore("Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=1");
        await Assert.ThrowsAnyAsync<Exception>(() => store.InitializeAsync(CancellationToken.None));
    }

    private static async Task ResetSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "drop table if exists client_events; drop table if exists battle_participants; " +
            "drop table if exists battles; drop table if exists server_runs; drop table if exists ops_schema_meta;",
            connection);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class OpsPostgresIntegrationFactAttribute : FactAttribute
{
    public OpsPostgresIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KF_TEST_OPS_POSTGRES_CONNECTION_STRING")))
            Skip = "Set KF_TEST_OPS_POSTGRES_CONNECTION_STRING to a throwaway database; its ops tables are dropped.";
    }
}
