using System.Text.Json.Nodes;
using KickFlight.BootstrapApi.Ops;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The NDJSON fallback is what a local run (no PlayerStore connection string) writes and what the /admin/ops
// endpoints then read. These tests pin its round-trip, filters and pruning so both stores keep the same contract.
public sealed class OpsStoresTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kf-ops-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Ndjson_store_round_trips_all_row_types_with_filters_and_prune()
    {
        // The store names each file after the UTC day it is written (not the row time), so the rows must be "today".
        var t0 = DateTime.UtcNow.Date;
        var store = new NdjsonOpsStore(_directory);
        await store.InitializeAsync(CancellationToken.None);

        var batch = new OpsWriteBatch();
        batch.Runs.Add(new ServerRunRow { Id = "run-1", StartedAt = t0, ReleaseSha = "sha", MasterVersion = "v", MinClientVersion = "2.11" });
        batch.Battles.Add(new BattleRow { BattleId = "b1", RunId = "run-1", CreatedAt = t0, UpdatedAt = t0, RuleId = 7, FieldId = 801, Humans = 1, BattleTimeSeconds = 180, Outcome = "partial" });
        batch.Participants.Add(new ParticipantRow { BattleId = "b1", UserId = "u1", BattleEntryId = "e1", CreatedAt = t0, UpdatedAt = t0, Status = "dropped_silent" });
        batch.Events.Add(new ClientEventRow { At = t0.AddMinutes(1), RunId = "run-1", BattleId = "b1", UserId = "u1", Kind = "start", Payload = new JsonObject { ["x"] = 1 } });
        batch.Events.Add(new ClientEventRow { At = t0.AddMinutes(2), RunId = "run-1", BattleId = "b1", UserId = "u1", Kind = OpsFingerprint.ExceptionKind, Fingerprint = "abc", Payload = new JsonObject() });
        await store.WriteAsync(batch, CancellationToken.None);

        Assert.Equal("sha", Assert.Single(await store.LoadRunsAsync(t0.AddMinutes(-1), CancellationToken.None)).ReleaseSha);
        Assert.Equal("partial", Assert.Single(await store.LoadBattlesAsync(t0.AddMinutes(-1), CancellationToken.None)).Outcome);
        Assert.Equal("dropped_silent", Assert.Single(await store.LoadParticipantsAsync(t0.AddMinutes(-1), CancellationToken.None)).Status);
        Assert.NotNull(await store.LoadBattleAsync("b1", CancellationToken.None));
        Assert.Single(await store.LoadBattleParticipantsAsync("b1", CancellationToken.None));

        var events = await store.LoadEventsAsync(new OpsEventQuery(t0.AddMinutes(-1)), CancellationToken.None);
        Assert.Equal(2, events.Count);
        var exceptions = await store.LoadEventsAsync(
            new OpsEventQuery(t0.AddMinutes(-1), Kinds: [OpsFingerprint.ExceptionKind]), CancellationToken.None);
        Assert.Single(exceptions);
        Assert.Equal("abc", exceptions[0].Fingerprint);
        var newestOnly = await store.LoadEventsAsync(new OpsEventQuery(t0.AddMinutes(-1), Limit: 1), CancellationToken.None);
        Assert.Equal(OpsFingerprint.ExceptionKind, Assert.Single(newestOnly).Kind);
        var kinds = await store.CountEventsByKindAsync(t0.AddMinutes(-1), CancellationToken.None);
        Assert.Equal(1, kinds["start"]);
        Assert.Equal(1, kinds[OpsFingerprint.ExceptionKind]);

        // A future window sees nothing; a window before every row sees nothing either (files are read by day).
        Assert.Empty(await store.LoadBattlesAsync(t0.AddDays(2), CancellationToken.None));

        // Pruning deletes the whole day file, so the rows disappear from every read.
        await store.PruneAsync(t0.AddDays(1), CancellationToken.None);
        Assert.Empty(await store.LoadEventsAsync(new OpsEventQuery(DateTime.MinValue), CancellationToken.None));
        Assert.Empty(await store.LoadRunsAsync(DateTime.MinValue, CancellationToken.None));
    }

    [Fact]
    public void Factory_defaults_the_ndjson_directory_under_the_content_root()
    {
        var configuration = new ConfigurationBuilder().Build();
        var store = Assert.IsType<NdjsonOpsStore>(OpsStoreFactory.Create(configuration, _directory));
        Assert.Equal(Path.GetFullPath(Path.Combine(_directory, "data", "ops")), store.Directory);
    }
}
