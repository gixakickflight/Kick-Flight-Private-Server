using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The replay screens are a translation over otomo's patch recordings (IOtomoRecordings): nothing is stored here, so
// these tests pin the translation - the tags an upload carries, the channel rows read back by tag, the Featured row
// read from the published curated list, the sealed play URL - against FakeOtomoRecordings, which honours patch's own
// contract. Where the old tests exercised local storage, a rotation and a retention clock, those subjects moved to
// patch and their tests went with them.
public sealed class BattleReplayTests
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
    private const string Secret = "replay-test-secret";

    // ---------------------------------------------------------------- archive parser

    [Fact]
    public void Archive_parser_reads_type_tagged_length_name_and_body()
    {
        // Exactly what Colorful.ReplayValue.SerializeObjects writes (see docs/REPLAYS.md):
        //   byte TYPE_UINT(2) | uint32-le length | byte TYPE_STRING(6) | int32-le nameLen | utf8 name | raw bytes
        var header = Encoding.UTF8.GetBytes("{\"header\":true}");
        var battle = Encoding.UTF8.GetBytes("{\"battle\":true}");
        var result = Encoding.UTF8.GetBytes("{\"result\":true}");
        var frames = new byte[] { 1, 2, 3, 4, 5 };
        var archive = BuildArchive(("header", header), ("battle", battle), ("frames", frames), ("result", result));

        var parsed = BattleReplayService.ParseArchive(archive);

        Assert.Equal(header, parsed.Header);
        Assert.Equal(battle, parsed.Battle);
        Assert.Equal(result, parsed.Result);

        // Unknown future members are skipped without breaking the walk.
        var withUnknown = BuildArchive(("header", header), ("thumbnails", new byte[] { 9, 9 }), ("battle", battle));
        var parsedUnknown = BattleReplayService.ParseArchive(withUnknown);
        Assert.Equal(header, parsedUnknown.Header);
        Assert.Equal(battle, parsedUnknown.Battle);
        Assert.Null(parsedUnknown.Result);
    }

    [Fact]
    public void Archive_parser_matches_device_path_member_names_and_strips_the_bom()
    {
        // Captured from a 2.11.1 upload: members are named by their full path and JSON members carry a UTF-8 BOM.
        const string dir = "/storage/emulated/0/Android/data/jp.grenge.kickflight/files/Replay/Work/";
        var json = Encoding.UTF8.GetBytes("{\"_appVersion\":\"2.11.1\"}");
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(json).ToArray();
        var archive = BuildArchive((dir + "header", withBom), (dir + "frames", new byte[] { 1 }), (dir + "result", withBom));

        var parsed = BattleReplayService.ParseArchive(archive);

        Assert.Equal(json, parsed.Header);
        Assert.Equal(json, parsed.Result);
        Assert.Null(parsed.Battle);
    }

    [Fact]
    public void Archive_parser_rejects_a_stream_with_the_wrong_value_type_tag()
    {
        var archive = BuildArchive(("header", new byte[] { 1 }));
        archive[0] = 1; // TYPE_INT instead of TYPE_UINT
        Assert.Throws<InvalidDataException>(() => BattleReplayService.ParseArchive(archive));
    }

    // ---------------------------------------------------------------- upload

    [Fact]
    public async Task Upload_files_the_recording_under_one_tag_per_kicker_and_the_facts_the_row_shows()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(patch, clock);
        var blob = Gzip(BuildArchive(
            ("header", Encoding.UTF8.GetBytes(HeaderJson())),
            ("battle", Encoding.UTF8.GetBytes(BattleJson("battle-77",
                ("1000001", false, 1, 0), ("1000002", false, 2, 0),
                ("bot-1", true, 3, 1), ("bot-2", true, 4, 1)))),
            ("result", Encoding.UTF8.GetBytes(ResultJson([3, 1], "1000001")))));

        var created = await service.StoreUploadAsync("battle-77", blob, "acct-uploader", ResolveHumans,
            clock.GetUtcNow().UtcDateTime, default);

        Assert.True(created);
        var stored = patch.Get("battle-77") ?? throw new Xunit.Sdk.XunitException("patch holds no recording");
        Assert.Equal(blob, stored.Bytes);
        Assert.Equal(BattleReplayService.RecordingKind, stored.Kind);
        // One tag per kicker that played (bots included, as the old channel index did), then the facts the row shows.
        Assert.Equal(new[] { "humans:2", "kicker:1", "kicker:2", "kicker:3", "kicker:4", "rule:1", "ver:2.11.0" },
            stored.Tags.Order(StringComparer.Ordinal));
        // The players patch lets fetch the bytes: the uploader and the humans this server can link; never a bot.
        Assert.Equal(new[] { "acct-1000001", "acct-1000002", "acct-uploader" }, stored.Players.Order(StringComparer.Ordinal));

        // The index row rides in meta, so any adapter instance can draw it.
        var meta = stored.Meta;
        Assert.Equal(2, meta["HumanCount"]!.GetValue<int>());
        Assert.Equal("2.11.0", meta["ApplicationVersion"]!.GetValue<string>());
        Assert.Equal(2, meta["Teams"]!.AsArray().Count);
    }

    [Fact]
    public async Task Upload_keeps_the_first_upload_per_battle_and_a_retry_does_not_duplicate()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(patch, clock);
        var first = Gzip(BuildArchive(("battle", Encoding.UTF8.GetBytes(BattleJson("battle-5", ("u1", false, 1, 0))))));
        var other = Gzip(BuildArchive(("battle", Encoding.UTF8.GetBytes(BattleJson("battle-5", ("u1", false, 2, 0))))));

        Assert.True(await service.StoreUploadAsync("battle-5", first, "acct-a", ResolveHumans, clock.GetUtcNow().UtcDateTime, default));
        // The same bytes again (a retry after a lost answer): patch keeps one recording, so this is not "created".
        Assert.False(await service.StoreUploadAsync("battle-5", first, "acct-a", ResolveHumans, clock.GetUtcNow().UtcDateTime, default));
        // Different bytes under the same battle (a second uploader): patch refuses with 409, the first stays.
        Assert.False(await service.StoreUploadAsync("battle-5", other, "acct-b", ResolveHumans, clock.GetUtcNow().UtcDateTime, default));

        Assert.Equal(1, patch.Count);
        Assert.Equal(first, patch.Get("battle-5")!.Bytes);
        Assert.DoesNotContain("acct-b", patch.Get("battle-5")!.Players);
    }

    [Fact]
    public async Task Upload_falls_back_to_the_battle_end_snapshot_when_the_archive_roster_is_missing()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(patch, clock);
        service.RecordBattleEnd(new BattleReplayService.PendingMatch
        {
            BattleId = "battle-9",
            BattleRuleId = 2,
            FieldId = 301,
            HumanCount = 3,
            Teams =
            [
                new BattleReplayService.PendingTeam { TeamType = 0, Score = 7, RemainingDistance = 1, LastRemainingDistance = 2 }
            ],
            Players =
            [
                new BattleReplayService.PendingPlayer { UserId = "u1", Name = "One", KickerId = 5, KickerCostumeId = 64, TeamType = 0 }
            ]
        }, clock.GetUtcNow().UtcDateTime);

        // A battle member with no _playerBattleInfos: the pending snapshot supplies the roster.
        var blob = Gzip(BuildArchive(("battle", Encoding.UTF8.GetBytes(
            """{"_battleInfo":{"_battleId":"battle-9"},"_battleRuleInfo":{"_battleRuleId":2,"_fieldId":301}}"""))));
        Assert.True(await service.StoreUploadAsync("battle-9", blob, "acct-a", _ => null, clock.GetUtcNow().UtcDateTime, default));

        var stored = patch.Get("battle-9")!;
        Assert.Contains("kicker:5", stored.Tags);
        Assert.Contains("humans:3", stored.Tags);
        Assert.Contains("rule:2", stored.Tags);
        Assert.Equal(301, stored.Meta["FieldId"]!.GetValue<int>());
        var team = Assert.Single(stored.Meta["Teams"]!.AsArray());
        Assert.Equal(7, team!["Score"]!.GetValue<int>());
        Assert.Equal(64, Assert.Single(team["Players"]!.AsArray())!["KickerCostumeId"]!.GetValue<int>());
    }

    [Fact]
    public void Tags_stay_inside_patchs_budget_with_the_kickers_first()
    {
        var many = new BattleReplayService.ReplayRecord
        {
            KickerIds = Enumerable.Range(1, 20).ToArray(), HumanCount = 4, BattleRuleId = 1, ApplicationVersion = "2.11.0"
        };
        var tags = BattleReplayService.RecordingTags(many, null);
        Assert.Equal(16, tags.Count); // services/patch/internal/recording/recording.go:41
        Assert.All(tags, tag => Assert.StartsWith(BattleReplayService.KickerTagPrefix, tag));

        var few = new BattleReplayService.ReplayRecord { KickerIds = [3, 9], HumanCount = 1, BattleRuleId = 4, ApplicationVersion = "weird version!" };
        var custom = BattleReplayService.RecordingTags(few, new BattleReplayService.PendingMatch { Custom = true });
        // A version patch's tag alphabet cannot hold is left off rather than sent to be refused.
        Assert.Equal(new[] { "kicker:3", "kicker:9", "humans:1", "rule:4", "custom" }, custom);
    }

    [Fact]
    public async Task Upload_with_patch_unreachable_or_without_a_key_throws_instead_of_pretending()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var blob = Gzip(BuildArchive(("battle", Encoding.UTF8.GetBytes(BattleJson("battle-3", ("u1", false, 1, 0))))));

        patch.SetUnavailable(true);
        var down = await Assert.ThrowsAsync<OtomoRecordingsException>(() =>
            NewService(patch, clock).StoreUploadAsync("battle-3", blob, "acct-a", ResolveHumans, clock.GetUtcNow().UtcDateTime, default));
        Assert.Equal(OtomoRecordingsFailure.Unavailable, down.Failure);
        patch.SetUnavailable(false);

        var keyless = await Assert.ThrowsAsync<OtomoRecordingsException>(() =>
            NewService(patch, clock, o => o.ServiceKey = "").StoreUploadAsync("battle-3", blob, "acct-a", ResolveHumans, clock.GetUtcNow().UtcDateTime, default));
        Assert.Equal(OtomoRecordingsFailure.Disabled, keyless.Failure);

        var wrongKey = await Assert.ThrowsAsync<OtomoRecordingsException>(() =>
            NewService(patch, clock, o => o.ServiceKey = "not-the-key").StoreUploadAsync("battle-3", blob, "acct-a", ResolveHumans, clock.GetUtcNow().UtcDateTime, default));
        Assert.Equal(OtomoRecordingsFailure.Refused, wrongKey.Failure);
        Assert.Equal(401, wrongKey.StatusCode);
        Assert.Equal(0, patch.Count);
    }

    // ---------------------------------------------------------------- index: kicker channels

    [Fact]
    public async Task Kicker_channel_lists_only_replays_where_that_kicker_played_newest_first()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(patch, clock);

        await StoreAsync(service, clock, "battle-1", humans: 1, kickers: [1, 2]);
        await StoreAsync(service, clock, "battle-2", humans: 1, kickers: [2, 3]);
        await StoreAsync(service, clock, "battle-3", humans: 1, kickers: [1, 4]);

        var index = await IndexAsync(service, [1, 2, 14]);

        Assert.Equal(new[] { "battle-3", "battle-1" }, ReplayIds(index, 1001));
        Assert.Equal(new[] { "battle-2", "battle-1" }, ReplayIds(index, 1002));
        Assert.Empty(ReplayIds(index, 1014));
        // Each channel is the by-tag read of its kicker, newest N, with the player's own token.
        Assert.Contains(patch.Requests, r => r.Query.StartsWith("?tag=kicker%3A1&limit=20", StringComparison.Ordinal)
            && r.Authorization == "Bearer " + Token);
    }

    [Fact]
    public async Task Kicker_channel_shows_only_the_newest_configured_number()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(patch, clock, o => o.KickerChannelSize = 2);

        foreach (var n in new[] { 1, 2, 3 }) await StoreAsync(service, clock, $"battle-{n}", humans: 1, kickers: [1]);

        Assert.Equal(new[] { "battle-3", "battle-2" }, ReplayIds(await IndexAsync(service, [1]), 1001));
    }

    [Fact]
    public async Task Index_draws_a_row_from_the_meta_the_upload_carried()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(patch, clock);
        await StoreAsync(service, clock, "battle-100", humans: 2, kickers: [1, 2], appVersion: "2.11.0");

        var row = AllRows(await IndexAsync(service, [1])).First(r => r.Channel == 1001).Row;

        Assert.Equal("battle-100", row.GetProperty("battleReplayId").GetString());
        Assert.Equal(1, row.GetProperty("battleRuleId").GetInt32());
        Assert.Equal(101, row.GetProperty("fieldId").GetInt32());
        Assert.Equal("2.11.0", row.GetProperty("applicationVersion").GetString());
        Assert.Equal(2, row.GetProperty("battleReplayTeamList").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(row.GetProperty("displayEndDatetime").GetString()));
    }

    [Fact]
    public async Task Index_leaves_out_a_recording_whose_meta_is_not_a_replay_row()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(patch, clock);
        await StoreAsync(service, clock, "battle-1", humans: 1, kickers: [1]);
        // Somebody else's producer filed a recording under the same tag.
        patch.Seed("foreign-1", ["kicker:1"], ["acct-viewer"], """{"x":1}""");

        Assert.Equal(new[] { "battle-1" }, ReplayIds(await IndexAsync(service, [1]), 1001));
    }

    // ---------------------------------------------------------------- index: Featured

    [Fact]
    public async Task Featured_is_empty_when_nothing_is_published_whatever_has_been_uploaded()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(patch, clock);
        await StoreAsync(service, clock, "battle-a", humans: 4, kickers: [1]);
        await StoreAsync(service, clock, "battle-b", humans: 1, kickers: [2]);

        // patch.curated has no list yet (a 404), and the adapter invents no rotation to fill it.
        var unpublished = await IndexAsync(service, [1]);
        Assert.Empty(ReplayIds(unpublished, 1));
        Assert.Contains(patch.Requests, r => r.Query == "?list=featured");

        // Published but empty (what the seed document carries) is the same row.
        patch.PublishCurated("featured");
        Assert.Empty(ReplayIds(await IndexAsync(service, [1]), 1));
    }

    [Fact]
    public async Task Featured_is_the_published_curated_list_in_its_order_without_the_recordings_that_are_gone()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(patch, clock);
        await StoreAsync(service, clock, "battle-a", humans: 1, kickers: [1]);
        await StoreAsync(service, clock, "battle-b", humans: 4, kickers: [2]);
        await StoreAsync(service, clock, "battle-c", humans: 3, kickers: [3]);

        // The operator's order, not newest-first and not most-humans-first.
        patch.PublishCurated("featured", "battle-a", "battle-c", "battle-b");
        Assert.Equal(new[] { "battle-a", "battle-c", "battle-b" }, ReplayIds(await IndexAsync(service, [1]), 1));

        // An entry whose recording patch no longer holds is omitted, never a hole.
        patch.Delete("battle-c");
        Assert.Equal(new[] { "battle-a", "battle-b" }, ReplayIds(await IndexAsync(service, [1]), 1));
    }

    [Fact]
    public async Task Featured_reads_the_configured_list_name()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(patch, clock, o => o.FeaturedList = "staff.picks");
        await StoreAsync(service, clock, "battle-a", humans: 1, kickers: [1]);
        patch.PublishCurated("featured");
        patch.PublishCurated("staff.picks", "battle-a");

        Assert.Equal(new[] { "battle-a" }, ReplayIds(await IndexAsync(service, [1]), 1));
    }

    [Fact]
    public async Task Index_fails_closed_when_patch_is_down_instead_of_serving_empty_channels()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var service = NewService(patch, new AdjustableTimeProvider(DateTimeOffset.UnixEpoch));
        patch.SetUnavailable(true);

        var failure = await Assert.ThrowsAsync<OtomoRecordingsException>(() => IndexAsync(service, [1]));
        Assert.Equal(OtomoRecordingsFailure.Unavailable, failure.Failure);
    }

    // ---------------------------------------------------------------- playback

    [Fact]
    public async Task Play_key_round_trips_to_the_uploaded_bytes_in_the_clients_encryption()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(patch, clock);
        var blob = await StoreAsync(service, clock, "battle-5", humans: 1, kickers: [1]);
        var now = clock.GetUtcNow().UtcDateTime;

        var (keyId, key) = await service.CreatePlayKeyAsync("battle-5", "device-1", now, default);
        Assert.NotEmpty(keyId);
        Assert.Equal(32, Encoding.UTF8.GetByteCount(key));

        var encoded = await service.TryEncodeBlobAsync(keyId, now, default);

        Assert.NotNull(encoded);
        Assert.Equal(blob, D2CCodec.Decode(encoded!, Encoding.ASCII.GetBytes(key)));
        // The bytes came from patch's internal route with the game-server key; no player token, no player route.
        Assert.Contains(patch.Requests, r => r.Path == "/internal/patch/recording/battle-5/blob"
            && r.Authorization == "Bearer " + FakeOtomoRecordings.ServiceKey);
        Assert.DoesNotContain(patch.Requests, r => r.Path.StartsWith("/patch/v1/recordings/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Play_key_works_for_a_featured_row_and_a_kicker_row_the_viewer_was_not_in()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(patch, clock);
        var now = clock.GetUtcNow().UtcDateTime;
        // Two battles uploaded by other players: acct-viewer is in neither, so patch's player blob route would 404.
        var featured = Gzip(BuildArchive(("battle", Encoding.UTF8.GetBytes(BattleJson("battle-f", ("u1", false, 3, 0))))));
        var channel = Gzip(BuildArchive(("battle", Encoding.UTF8.GetBytes(BattleJson("battle-k", ("u2", false, 7, 0))))));
        Assert.True(await service.StoreUploadAsync("battle-f", featured, "acct-other-1", _ => null, now, default));
        Assert.True(await service.StoreUploadAsync("battle-k", channel, "acct-other-2", _ => null, now, default));
        patch.PublishCurated("featured", "battle-f");

        var index = await IndexAsync(service, [7]);
        Assert.Equal(new[] { "battle-f" }, ReplayIds(index, 1));
        Assert.Equal(new[] { "battle-k" }, ReplayIds(index, 1007));

        foreach (var (id, bytes) in new[] { ("battle-f", featured), ("battle-k", channel) })
        {
            var (keyId, key) = await service.CreatePlayKeyAsync(id, "device-viewer", now, default);
            Assert.NotEmpty(keyId);
            var encoded = await service.TryEncodeBlobAsync(keyId, now, default);
            Assert.Equal(bytes, D2CCodec.Decode(encoded!, Encoding.ASCII.GetBytes(key)));
        }
    }

    [Fact]
    public async Task Play_key_is_refused_for_an_unknown_or_malformed_replay_and_a_forged_or_expired_id()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(patch, clock);
        await StoreAsync(service, clock, "battle-5", humans: 1, kickers: [1]);
        var now = clock.GetUtcNow().UtcDateTime;

        Assert.Equal(("", ""), await service.CreatePlayKeyAsync("does-not-exist", "d", now, default));
        Assert.Equal(("", ""), await service.CreatePlayKeyAsync("../etc/passwd", "d", now, default));

        var (keyId, _) = await service.CreatePlayKeyAsync("battle-5", "d", now, default);
        var forged = keyId[..^2] + (keyId.EndsWith("AA", StringComparison.Ordinal) ? "BB" : "AA");
        Assert.Null(await service.TryEncodeBlobAsync(forged, now, default));
        Assert.Null(await service.TryEncodeBlobAsync("garbage", now, default));
        Assert.Null(await service.TryEncodeBlobAsync("", now, default));

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(await service.TryEncodeBlobAsync(keyId, clock.GetUtcNow().UtcDateTime, default));
    }

    [Fact]
    public async Task Play_url_for_a_recording_patch_has_since_deleted_is_not_found_and_patch_down_is_an_error()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(patch, clock);
        await StoreAsync(service, clock, "battle-5", humans: 1, kickers: [1]);
        var now = clock.GetUtcNow().UtcDateTime;
        var (keyId, _) = await service.CreatePlayKeyAsync("battle-5", "d", now, default);

        patch.SetUnavailable(true);
        var down = await Assert.ThrowsAsync<OtomoRecordingsException>(() => service.TryEncodeBlobAsync(keyId, now, default));
        Assert.Equal(OtomoRecordingsFailure.Unavailable, down.Failure);
        await Assert.ThrowsAsync<OtomoRecordingsException>(() => service.CreatePlayKeyAsync("battle-5", "d", now, default));
        patch.SetUnavailable(false);

        patch.Delete("battle-5");
        Assert.Null(await service.TryEncodeBlobAsync(keyId, now, default));
        Assert.Equal(("", ""), await service.CreatePlayKeyAsync("battle-5", "d", now, default));
    }

    [Fact]
    public async Task Play_url_needs_no_server_state_so_another_instance_with_the_same_secret_serves_it()
    {
        await using var patch = FakeOtomoRecordings.Start();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(patch, clock);
        var blob = await StoreAsync(service, clock, "battle-5", humans: 1, kickers: [1]);
        var now = clock.GetUtcNow().UtcDateTime;
        var (keyId, key) = await service.CreatePlayKeyAsync("battle-5", "d", now, default);

        // A restarted adapter (or a second replica) is a new object that has seen nothing.
        var restarted = NewService(patch, clock);
        var encoded = await restarted.TryEncodeBlobAsync(keyId, now, default);
        Assert.Equal(blob, D2CCodec.Decode(encoded!, Encoding.ASCII.GetBytes(key)));

        // An adapter with another secret cannot open it, and could not have derived its key.
        var stranger = NewService(patch, clock, secret: "another-secret");
        Assert.Null(await stranger.TryEncodeBlobAsync(keyId, now, default));
    }

    // ---------------------------------------------------------------- endpoint behaviour

    [Fact]
    public async Task Battle_end_upload_index_and_play_are_translated_from_patch()
    {
        await using var patch = FakeOtomoRecordings.Start();
        using var factory = NewFactory(patch);
        var session = await CreateSessionAsync(factory);

        var endResponse = await PostAsync(session, "/battle/end", new
        {
            battleId = "battle-100",
            teamScoreList = new[]
            {
                new { teamType = 0, score = 5, remainingDistance = 2, lastRemainingDistance = 1 },
                new { teamType = 1, score = 3, remainingDistance = 4, lastRemainingDistance = 2 }
            },
            userScoreList = new[]
            {
                new { userId = session.UserId, score = 5, mvpFlag = true, breakawayFlag = false, badConnectionFlag = false, aiFlag = false },
                new { userId = "bot-77", score = 3, mvpFlag = false, breakawayFlag = false, badConnectionFlag = false, aiFlag = true }
            }
        });
        Assert.True(endResponse.GetProperty("replayUploadFlag").GetBoolean());
        Assert.Equal(200, endResponse.GetProperty("kickPoint").GetInt32());

        var blob = Gzip(BuildArchive(
            ("header", Encoding.UTF8.GetBytes(HeaderJson(appVersion: "2.11.0"))),
            ("battle", Encoding.UTF8.GetBytes(BattleJson("battle-100",
                (session.UserId, false, 1, 0), ("bot-77", true, 2, 1)))),
            ("result", Encoding.UTF8.GetBytes(ResultJson([5, 3], session.UserId)))));
        await PostAsync(session, "/battle/upload", new { battleId = "battle-100", uploadFile = Convert.ToBase64String(blob) });

        // The upload reached patch, with the game-server key, once, and the uploader is a player of it.
        var stored = patch.Get("battle-100") ?? throw new Xunit.Sdk.XunitException("patch holds no recording");
        Assert.Equal(blob, stored.Bytes);
        Assert.Single(patch.Requests, r => r.Method == "PUT" && r.Authorization == "Bearer " + FakeOtomoRecordings.ServiceKey);
        Assert.Contains("kicker:1", stored.Tags);
        Assert.Contains("kicker:2", stored.Tags);
        Assert.Contains("humans:1", stored.Tags);
        Assert.Single(stored.Players);

        var index = await PostAsync(session, "/battleReplay/index", null);
        var channels = index.GetProperty("battleReplayChannelList");
        Assert.Equal(15, channels.GetArrayLength());
        Assert.Equal(0, index.GetProperty("appMovieList").GetArrayLength());

        // ids match the served BattleReplayChannel master: Featured 1 + 1000+kickerId.
        var channelIds = channels.EnumerateArray().Select(c => c.GetProperty("battleReplayChannelId").GetInt32()).ToArray();
        Assert.Equal(1, channelIds[0]);
        Assert.Equal(Enumerable.Range(1001, 14).ToArray(), channelIds.Skip(1).ToArray());

        // Nothing is published as Featured, so that row is empty - no rotation is made up.
        Assert.Equal(0, channels[0].GetProperty("battleReplayList").GetArrayLength());

        var tsubame = channels.EnumerateArray().Single(c => c.GetProperty("battleReplayChannelId").GetInt32() == 1001);
        var replay = Assert.Single(tsubame.GetProperty("battleReplayList").EnumerateArray());
        Assert.Equal("battle-100", replay.GetProperty("battleReplayId").GetString());
        Assert.Equal("2.11.0", replay.GetProperty("applicationVersion").GetString());
        Assert.False(string.IsNullOrEmpty(replay.GetProperty("displayStartDatetime").GetString()));
        var teams = replay.GetProperty("battleReplayTeamList");
        Assert.Equal(2, teams.GetArrayLength());
        Assert.Equal(5, teams[0].GetProperty("score").GetInt32());
        var player = teams[0].GetProperty("battleReplayPlayerList")[0];
        Assert.Equal(session.UserId, player.GetProperty("userId").GetString());
        Assert.True(player.GetProperty("mvpFlag").GetBoolean());
        Assert.True(player.GetProperty("kickerCostumeId").GetInt32() > 0);

        // Once the curated list names it, the Featured row carries it.
        patch.PublishCurated("featured", "battle-100");
        var featured = (await PostAsync(session, "/battleReplay/index", null))
            .GetProperty("battleReplayChannelList")[0].GetProperty("battleReplayList");
        Assert.Equal("battle-100", Assert.Single(featured.EnumerateArray()).GetProperty("battleReplayId").GetString());

        // /battleReplay/play hands out a 32-char key and a blob URL that decrypts back to the upload.
        var play = await PostAsync(session, "/battleReplay/play", new { battleReplayId = "battle-100" });
        var url = play.GetProperty("battleReplayUrl").GetString();
        var encryptionKey = play.GetProperty("encryptionKey").GetString();
        Assert.False(string.IsNullOrEmpty(url));
        Assert.Equal(32, encryptionKey!.Length);

        using var download = await session.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var downloaded = D2CCodec.Decode(await download.Content.ReadAsByteArrayAsync(), Encoding.ASCII.GetBytes(encryptionKey));
        Assert.Equal(blob, downloaded);

        var missing = await PostAsync(session, "/battleReplay/play", new { battleReplayId = "battle-unknown" });
        Assert.Equal("", missing.GetProperty("battleReplayUrl").GetString());
        Assert.Equal("", missing.GetProperty("encryptionKey").GetString());

        // A replay the viewer was not in, listed in Featured and in its kicker's channel, plays all the same.
        var foreign = Gzip(BuildArchive(("battle", Encoding.UTF8.GetBytes(BattleJson("battle-foreign", ("someone-else", false, 5, 0))))));
        patch.Seed("battle-foreign", ["kicker:5"], ["acct-someone-else"], """{"BattleId":"battle-foreign","Teams":[]}""", foreign);
        patch.PublishCurated("featured", "battle-100", "battle-foreign");
        var foreignPlay = await PostAsync(session, "/battleReplay/play", new { battleReplayId = "battle-foreign" });
        var foreignUrl = foreignPlay.GetProperty("battleReplayUrl").GetString();
        Assert.False(string.IsNullOrEmpty(foreignUrl));
        using var foreignDownload = await session.Client.GetAsync(foreignUrl);
        Assert.Equal(HttpStatusCode.OK, foreignDownload.StatusCode);
        Assert.Equal(foreign, D2CCodec.Decode(await foreignDownload.Content.ReadAsByteArrayAsync(),
            Encoding.ASCII.GetBytes(foreignPlay.GetProperty("encryptionKey").GetString()!)));

        using var forged = await session.Client.GetAsync(url![..^3] + "AAA");
        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
    }

    [Fact]
    public async Task Custom_battle_end_is_recorded_and_served_fields_are_fieldless()
    {
        await using var patch = FakeOtomoRecordings.Start();
        using var factory = NewFactory(patch);
        var session = await CreateSessionAsync(factory);

        var response = await PostAsync(session, "/customBattle/end", new
        {
            customBattleId = "custom-123-1",
            teamScoreList = Array.Empty<object>(),
            userScoreList = new[]
            {
                new { userId = session.UserId, score = 1, mvpFlag = true, breakawayFlag = false, badConnectionFlag = false, aiFlag = false }
            }
        });
        Assert.Equal(JsonValueKind.Object, response.ValueKind);
        Assert.Empty(response.EnumerateObject());

        var blob = Gzip(BuildArchive(
            ("battle", Encoding.UTF8.GetBytes(BattleJson("custom-123-1", (session.UserId, false, 8, 0)))),
            ("result", Encoding.UTF8.GetBytes(ResultJson([1, 0], session.UserId)))));
        await PostAsync(session, "/battle/upload", new { battleId = "custom-123-1", uploadFile = Convert.ToBase64String(blob) });

        Assert.Contains("custom", patch.Get("custom-123-1")!.Tags);
        var index = await PostAsync(session, "/battleReplay/index", null);
        var kicker8 = index.GetProperty("battleReplayChannelList").EnumerateArray()
            .Single(c => c.GetProperty("battleReplayChannelId").GetInt32() == 1008);
        var replay = Assert.Single(kicker8.GetProperty("battleReplayList").EnumerateArray());
        Assert.Equal("custom-123-1", replay.GetProperty("battleReplayId").GetString());
        // The upload's own battleRuleInfo filled the rule/field the custom end body never carried.
        Assert.Equal(1, replay.GetProperty("battleRuleId").GetInt32());
        Assert.Equal(101, replay.GetProperty("fieldId").GetInt32());
    }

    [Fact]
    public async Task Replay_screens_fail_closed_in_the_game_error_contract_when_patch_is_down_or_switched_off()
    {
        await using var patch = FakeOtomoRecordings.Start();
        using var down = NewFactory(patch);
        var session = await CreateSessionAsync(down);
        patch.SetUnavailable(true);

        var blob = Gzip(BuildArchive(("battle", Encoding.UTF8.GetBytes(BattleJson("battle-1", (session.UserId, false, 1, 0))))));
        foreach (var (path, body) in new (string, object?)[]
        {
            ("/battleReplay/index", null),
            ("/battleReplay/play", new { battleReplayId = "battle-1" }),
            ("/battle/upload", new { battleId = "battle-1", uploadFile = Convert.ToBase64String(blob) })
        })
        {
            var (status, headers, json) = await PostRawAsync(session, path, body);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
            Assert.Equal("1", headers);
            Assert.Equal("replay-unavailable", json.GetProperty("error").GetString());
            Assert.Equal("Unavailable", json.GetProperty("reason").GetString());
        }

        // Switched off (the default): the same refusal, with its own reason, and no empty list served as fact.
        using var off = NewFactory(new Dictionary<string, string?> { ["Otomo:Recordings:Enabled"] = "false" });
        var offSession = await CreateSessionAsync(off);
        var (offStatus, _, offJson) = await PostRawAsync(offSession, "/battleReplay/index", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, offStatus);
        Assert.Equal("Disabled", offJson.GetProperty("reason").GetString());
    }

    // ---------------------------------------------------------------- helpers

    private static readonly string Token = FakeOtomoRecordings.TokenFor("acct-viewer");

    // A battle's numeric user ids to otomo accounts: the humans of this suite are linked, bots never are.
    private static string? ResolveHumans(string userId) =>
        userId.StartsWith("bot-", StringComparison.Ordinal) ? null : "acct-" + userId;

    private static BattleReplayService NewService(FakeOtomoRecordings patch, TimeProvider clock,
        Action<OtomoRecordingsOptions>? tweak = null, string secret = Secret)
    {
        var options = new OtomoRecordingsOptions
        {
            Enabled = true,
            BaseUrl = patch.BaseUrl,
            InternalBaseUrl = patch.BaseUrl,
            ServiceKey = FakeOtomoRecordings.ServiceKey,
            RetryDelay = TimeSpan.Zero,
            Timeout = TimeSpan.FromSeconds(10),
            PlaySecret = secret
        };
        tweak?.Invoke(options);
        var wrapped = Options.Create(options);
        var client = OtomoRecordings.Create(wrapped, Options.Create(new OtomoSocialOptions()), NullLogger<OtomoRecordings>.Instance);
        return new BattleReplayService(client, wrapped, new ConfigurationBuilder().Build(), clock, NullLogger<BattleReplayService>.Instance);
    }

    // Uploads one four-player battle (the first `humans` are people, the rest bots) and returns the gzip bytes.
    private static async Task<byte[]> StoreAsync(BattleReplayService service, TimeProvider clock, string battleId,
        int humans, int[] kickers, string appVersion = "2.11.0")
    {
        var players = new List<(string, bool, int, int)>();
        for (var i = 0; i < humans; i++) players.Add(($"human-{battleId}-{i}", false, kickers[i % kickers.Length], i % 2));
        for (var i = humans; i < 4; i++) players.Add(($"bot-{battleId}-{i}", true, kickers[i % kickers.Length], i % 2));
        var blob = Gzip(BuildArchive(
            ("header", Encoding.UTF8.GetBytes(HeaderJson(appVersion))),
            ("battle", Encoding.UTF8.GetBytes(BattleJson(battleId, players.ToArray()))),
            ("result", Encoding.UTF8.GetBytes(ResultJson([humans, 4 - humans], players[0].Item1)))));
        // The viewer is a player of every battle here, so the play path can fetch its bytes.
        Assert.True(await service.StoreUploadAsync(battleId, blob, "acct-viewer", ResolveHumans, clock.GetUtcNow().UtcDateTime, default));
        return blob;
    }

    private static async Task<JsonElement> IndexAsync(BattleReplayService service, int[] kickers)
    {
        using var document = JsonDocument.Parse(await service.BuildIndexJsonAsync(Token, kickers, _ => 1, default));
        return document.RootElement.Clone();
    }

    private static IEnumerable<(int Channel, JsonElement Row)> AllRows(JsonElement index) =>
        index.GetProperty("battleReplayChannelList").EnumerateArray().SelectMany(channel =>
            channel.GetProperty("battleReplayList").EnumerateArray()
                .Select(row => (channel.GetProperty("battleReplayChannelId").GetInt32(), row.Clone())));

    private static string[] ReplayIds(JsonElement index, int channelId) =>
        index.GetProperty("battleReplayChannelList").EnumerateArray()
            .Single(c => c.GetProperty("battleReplayChannelId").GetInt32() == channelId)
            .GetProperty("battleReplayList").EnumerateArray()
            .Select(r => r.GetProperty("battleReplayId").GetString()!).ToArray();

    private static byte[] BuildArchive(params (string Name, byte[] Bytes)[] members)
    {
        using var stream = new MemoryStream();
        Span<byte> scratch = stackalloc byte[4];
        foreach (var (name, bytes) in members)
        {
            stream.WriteByte(2); // ReplayValue.TYPE_UINT
            BinaryPrimitives.WriteUInt32LittleEndian(scratch, (uint)bytes.Length);
            stream.Write(scratch);

            stream.WriteByte(6); // ReplayValue.TYPE_STRING
            var nameBytes = Encoding.UTF8.GetBytes(name);
            BinaryPrimitives.WriteInt32LittleEndian(scratch, nameBytes.Length);
            stream.Write(scratch);
            stream.Write(nameBytes);
            stream.Write(bytes);
        }
        return stream.ToArray();
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(bytes, 0, bytes.Length);
        return output.ToArray();
    }

    private static string HeaderJson(string appVersion = "2.11.0", long? ticks = null) =>
        JsonSerializer.Serialize(new
        {
            _appVersion = appVersion,
            _fileVersion = "1.0",
            _userId = "1000001",
            _dateTimeTicks = ticks ?? DateTime.UnixEpoch.Ticks
        });

    private static string BattleJson(string battleId, params (string UserId, bool Ai, int KickerId, int TeamType)[] players)
    {
        var infos = players.Select(p => new
        {
            _userId = p.UserId,
            _name = p.UserId,
            _rank = 10,
            _kickerId = p.KickerId,
            _kickerAiParameterId = p.Ai ? p.KickerId : 0,
            _teamType = p.TeamType,
            _teamIndex = 0,
            _isAi = p.Ai,
            _frameId = 1,
            _languageCode = "es"
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            _battleInfo = new { _battleId = battleId, _playerBattleInfos = infos },
            _battleRuleInfo = new { _battleRuleId = 1, _fieldId = 101 },
            _playerInfos = Array.Empty<object>()
        });
    }

    private static string ResultJson(int[] score, string mvpUserId) =>
        JsonSerializer.Serialize(new
        {
            _userId = "1000001",
            _teamType = 0,
            _score = score,
            _maxFlagProgressRate = new[] { 0, 0 },
            _lastFlagProgressRate = new[] { 0, 0 },
            _minRemainingDistance = new[] { 0, 0 },
            _battleInfo = Array.Empty<object>(),
            _validResult = true,
            _mvpUserId = mvpUserId
        });

    // A host whose patch is this test's own fake. In-memory configuration is added last, so it wins over the suite-wide
    // environment variables that point every other host at the shared one.
    private static WebApplicationFactory<Program> NewFactory(FakeOtomoRecordings patch) =>
        NewFactory(new Dictionary<string, string?>
        {
            ["Otomo:Recordings:Enabled"] = "true",
            ["Otomo:Recordings:BaseUrl"] = patch.BaseUrl,
            ["Otomo:Recordings:InternalBaseUrl"] = patch.BaseUrl,
            ["Otomo:Recordings:ServiceKey"] = FakeOtomoRecordings.ServiceKey,
            ["Otomo:Recordings:RetryDelay"] = "00:00:00",
            ["Otomo:Recordings:Timeout"] = "00:00:10"
        });

    private static WebApplicationFactory<Program> NewFactory(Dictionary<string, string?> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        });

    private static async Task<DemoSession> CreateSessionAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var key = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
        var payload = JsonSerializer.Serialize(new { hash = Encoding.ASCII.GetString(key), uuid = Guid.NewGuid().ToString("N") });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(payload), Encoding.ASCII.GetBytes(CommonCode), new byte[16]))
        };
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Add("x-app-access-token", response.Headers.GetValues("x-app-access-token").Single());
        return new DemoSession(client, key, response.Headers.GetValues("x-app-user-id").Single());
    }

    private static async Task<JsonElement> PostAsync(DemoSession session, string path, object? body)
    {
        var (status, headers, json) = await PostRawAsync(session, path, body);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("0", headers);
        return json;
    }

    // The status, the x-app-status-code header and the body (decoded when the game's contract encrypted it).
    private static async Task<(HttpStatusCode Status, string AppStatus, JsonElement Json)> PostRawAsync(
        DemoSession session, string path, object? body)
    {
        var json = body is null ? "{}" : JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(json), session.Key, new byte[16]))
        };
        request.Headers.Host = Host;
        using var response = await session.Client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var appStatus = response.Headers.GetValues("x-app-status-code").Single();
        // A 503 is plain JSON (SocialUnavailable's shape); a 200 is the D2C-encrypted body.
        using var document = JsonDocument.Parse(response.StatusCode == HttpStatusCode.OK ? D2CCodec.Decode(bytes, session.Key) : bytes);
        return (response.StatusCode, appStatus, document.RootElement.Clone());
    }

    private sealed record DemoSession(HttpClient Client, byte[] Key, string UserId);

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
