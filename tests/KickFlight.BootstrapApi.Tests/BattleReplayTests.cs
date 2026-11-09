using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

public sealed class BattleReplayTests
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";

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
        const string dir = "/storage/emulated/0/Android/data/world.global.fenix/files/Replay/Work/";
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

    // ---------------------------------------------------------------- storage + human count

    [Fact]
    public void Store_upload_counts_human_players_and_keeps_the_first_upload_per_battle()
    {
        using var directory = new TempDirectory();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(directory.Path, clock);
        var blob = Gzip(BuildArchive(
            ("header", Encoding.UTF8.GetBytes(HeaderJson())),
            ("battle", Encoding.UTF8.GetBytes(BattleJson("battle-77",
                ("1000001", false, 1, 0), ("1000002", false, 2, 0),
                ("bot-1", true, 3, 1), ("bot-2", true, 4, 1)))),
            ("result", Encoding.UTF8.GetBytes(ResultJson([3, 1], "1000001")))));

        Assert.True(service.StoreUpload("battle-77", blob, clock.GetUtcNow().UtcDateTime));
        var record = Assert.Single(service.Records);
        Assert.Equal("battle-77", record.ReplayId);
        Assert.Equal(2, record.HumanCount);
        Assert.Equal(new[] { 1, 2, 3, 4 }, record.KickerIds);
        Assert.Equal(2, record.Teams.Count);
        Assert.Equal(3, record.Teams.Single(t => t.TeamType == 0).Score);
        Assert.Equal(1, record.Teams.Single(t => t.TeamType == 1).Score);
        Assert.True(record.Teams.Single(t => t.TeamType == 0).Players.Single(p => p.UserId == "1000001").MvpFlag);
        Assert.True(record.Teams.Single(t => t.TeamType == 1).Players.All(p => !p.MvpFlag));

        // The master client is the only uploader, so a second upload of the same battle is ignored.
        Assert.False(service.StoreUpload("battle-77", blob, clock.GetUtcNow().UtcDateTime));
        Assert.Single(service.Records);
    }

    [Fact]
    public void Store_upload_falls_back_to_the_battle_end_snapshot_when_the_archive_roster_is_missing()
    {
        using var directory = new TempDirectory();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(directory.Path, clock);
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
        Assert.True(service.StoreUpload("battle-9", blob, clock.GetUtcNow().UtcDateTime));

        var record = Assert.Single(service.Records);
        Assert.Equal(3, record.HumanCount);
        Assert.Equal(2, record.BattleRuleId);
        Assert.Equal(301, record.FieldId);
        var team = Assert.Single(record.Teams);
        Assert.Equal(7, team.Score);
        Assert.Equal(64, Assert.Single(team.Players).KickerCostumeId);
    }

    // ---------------------------------------------------------------- kicker channel

    [Fact]
    public void Kicker_channel_returns_only_replays_where_that_kicker_played_newest_first()
    {
        using var directory = new TempDirectory();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(directory.Path, clock);

        Store(service, clock, "battle-1", ticksBase: 1, humans: 1, kickers: [1, 2]);
        Store(service, clock, "battle-2", ticksBase: 2, humans: 1, kickers: [2, 3]);
        Store(service, clock, "battle-3", ticksBase: 3, humans: 1, kickers: [1, 4]);

        var tsubame = service.GetLatestForKicker(1, clock.GetUtcNow().UtcDateTime);
        Assert.Equal(new[] { "battle-3", "battle-1" }, tsubame.Select(r => r.ReplayId).ToArray());
        Assert.Empty(service.GetLatestForKicker(14, clock.GetUtcNow().UtcDateTime));
    }

    // ---------------------------------------------------------------- featured rotation

    [Fact]
    public void Featured_picks_the_highest_human_counts_and_freezes_for_the_rotation()
    {
        using var directory = new TempDirectory();
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
        var service = NewService(directory.Path, clock, featuredCount: 2, rotationHours: 3);

        Store(service, clock, "battle-a", ticksBase: 1, humans: 1, kickers: [1]);
        Store(service, clock, "battle-b", ticksBase: 2, humans: 4, kickers: [2]);
        Store(service, clock, "battle-c", ticksBase: 3, humans: 3, kickers: [3]);

        var first = service.GetFeatured(clock.GetUtcNow().UtcDateTime);
        Assert.Equal(new[] { "battle-b", "battle-c" }, first.Select(r => r.ReplayId).ToArray());

        // A new, better upload within the same rotation must NOT change the frozen set...
        clock.Advance(TimeSpan.FromHours(1));
        Store(service, clock, "battle-d", ticksBase: 4, humans: 8, kickers: [4]);
        var frozen = service.GetFeatured(clock.GetUtcNow().UtcDateTime);
        Assert.Equal(new[] { "battle-b", "battle-c" }, frozen.Select(r => r.ReplayId).ToArray());

        // ...but once the rotation rolls it is recomputed and includes it.
        clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));
        var rotated = service.GetFeatured(clock.GetUtcNow().UtcDateTime);
        Assert.Equal(new[] { "battle-d", "battle-b" }, rotated.Select(r => r.ReplayId).ToArray());

        // A fresh service instance on the same directory serves the persisted rotation (restart-safe).
        var restarted = NewService(directory.Path, clock, featuredCount: 2, rotationHours: 3);
        Assert.Equal(rotated.Select(r => r.ReplayId).ToArray(),
            restarted.GetFeatured(clock.GetUtcNow().UtcDateTime).Select(r => r.ReplayId).ToArray());
    }

    [Fact]
    public void Featured_computed_while_short_is_topped_up_inside_the_rotation()
    {
        using var directory = new TempDirectory();
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
        var service = NewService(directory.Path, clock, featuredCount: 2, rotationHours: 3);

        // The first index request of a rotation can come before any upload: an empty set must not stay frozen.
        Assert.Empty(service.GetFeatured(clock.GetUtcNow().UtcDateTime));

        clock.Advance(TimeSpan.FromMinutes(10));
        Store(service, clock, "battle-a", ticksBase: 1, humans: 1, kickers: [1]);
        Assert.Equal(new[] { "battle-a" }, service.GetFeatured(clock.GetUtcNow().UtcDateTime).Select(r => r.ReplayId).ToArray());

        Store(service, clock, "battle-b", ticksBase: 2, humans: 5, kickers: [2]);
        Store(service, clock, "battle-c", ticksBase: 3, humans: 6, kickers: [3]);
        // Appended up to the count, never swapped: battle-a stays although battle-c has more humans.
        Assert.Equal(new[] { "battle-a", "battle-c" }, service.GetFeatured(clock.GetUtcNow().UtcDateTime).Select(r => r.ReplayId).ToArray());

        // Outside the window, old replays still fill a rotation that has nothing newer.
        clock.Advance(TimeSpan.FromHours(30));
        Assert.Equal(new[] { "battle-c", "battle-b" }, service.GetFeatured(clock.GetUtcNow().UtcDateTime).Select(r => r.ReplayId).ToArray());
    }

    // ---------------------------------------------------------------- playback

    [Fact]
    public void Play_key_encodes_the_stored_blob_back_to_the_uploaded_bytes()
    {
        using var directory = new TempDirectory();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = NewService(directory.Path, clock);
        var blob = Gzip(BuildArchive(("battle", Encoding.UTF8.GetBytes(BattleJson("battle-5", ("u1", false, 1, 0))))));
        Assert.True(service.StoreUpload("battle-5", blob, clock.GetUtcNow().UtcDateTime));

        var (keyId, key) = service.CreatePlayKey("battle-5", clock.GetUtcNow().UtcDateTime);
        Assert.NotEmpty(keyId);
        Assert.Equal(32, Encoding.UTF8.GetByteCount(key));

        Assert.True(service.TryEncodeBlob(keyId, clock.GetUtcNow().UtcDateTime, out var encoded));
        var decrypted = D2CCodec.Decode(encoded, Encoding.ASCII.GetBytes(key));
        Assert.Equal(blob, decrypted);

        // Unknown replay -> no key/url; expired key -> no blob.
        Assert.Equal(("", ""), service.CreatePlayKey("does-not-exist", clock.GetUtcNow().UtcDateTime));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False(service.TryEncodeBlob(keyId, clock.GetUtcNow().UtcDateTime, out _));
    }

    // ---------------------------------------------------------------- endpoint behaviour

    [Fact]
    public async Task Battle_end_returns_replay_upload_flag_true_and_index_exposes_the_served_dtos()
    {
        using var directory = new TempDirectory();
        using var factory = NewFactory(directory.Path);
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
        await PostAsync(session, "/battle/upload", new
        {
            battleId = "battle-100",
            uploadFile = Convert.ToBase64String(blob)
        });

        var index = await PostAsync(session, "/battleReplay/index", null);
        var channels = index.GetProperty("battleReplayChannelList");
        Assert.Equal(15, channels.GetArrayLength());
        Assert.Equal(0, index.GetProperty("appMovieList").GetArrayLength());

        // ids match the served BattleReplayChannel master: Featured 1 + 1000+kickerId.
        var channelIds = channels.EnumerateArray().Select(c => c.GetProperty("battleReplayChannelId").GetInt32()).ToArray();
        Assert.Equal(1, channelIds[0]);
        Assert.Equal(Enumerable.Range(1001, 14).ToArray(), channelIds.Skip(1).ToArray());

        var featured = channels[0].GetProperty("battleReplayList");
        Assert.Equal(1, featured.GetArrayLength());
        var replay = featured[0];
        Assert.Equal("battle-100", replay.GetProperty("battleReplayId").GetString());
        Assert.Equal(1, replay.GetProperty("battleRuleId").GetInt32());
        Assert.Equal("2.11.0", replay.GetProperty("applicationVersion").GetString());
        Assert.False(string.IsNullOrEmpty(replay.GetProperty("displayStartDatetime").GetString()));
        Assert.False(string.IsNullOrEmpty(replay.GetProperty("displayEndDatetime").GetString()));

        var teams = replay.GetProperty("battleReplayTeamList");
        Assert.Equal(2, teams.GetArrayLength());
        var blue = teams[0];
        Assert.Equal(0, blue.GetProperty("teamType").GetInt32());
        Assert.Equal(5, blue.GetProperty("score").GetInt32());
        var player = blue.GetProperty("battleReplayPlayerList")[0];
        Assert.Equal(session.UserId, player.GetProperty("userId").GetString());
        Assert.True(player.GetProperty("mvpFlag").GetBoolean());
        Assert.True(player.GetProperty("kickerCostumeId").GetInt32() > 0);

        // The kicker channel for kickerId 1 carries the same replay.
        var tsubameChannel = channels.EnumerateArray().Single(c => c.GetProperty("battleReplayChannelId").GetInt32() == 1001);
        Assert.Equal("battle-100", tsubameChannel.GetProperty("battleReplayList")[0].GetProperty("battleReplayId").GetString());

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
    }

    [Fact]
    public async Task Custom_battle_end_is_recorded_and_served_fields_are_fieldless()
    {
        using var directory = new TempDirectory();
        using var factory = NewFactory(directory.Path);
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
        await PostAsync(session, "/battle/upload", new
        {
            battleId = "custom-123-1",
            uploadFile = Convert.ToBase64String(blob)
        });

        var index = await PostAsync(session, "/battleReplay/index", null);
        var featured = index.GetProperty("battleReplayChannelList")[0].GetProperty("battleReplayList");
        var replay = Assert.Single(featured.EnumerateArray());
        Assert.Equal("custom-123-1", replay.GetProperty("battleReplayId").GetString());
        // The upload's own battleRuleInfo filled the rule/field the custom end body never carried.
        Assert.Equal(1, replay.GetProperty("battleRuleId").GetInt32());
        Assert.Equal(101, replay.GetProperty("fieldId").GetInt32());
    }

    // ---------------------------------------------------------------- helpers

    private static BattleReplayService NewService(string directory, TimeProvider clock,
        int featuredCount = 20, double rotationHours = 3)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BattleReplayService.DirectoryKey] = directory,
            [BattleReplayService.FeaturedCountKey] = featuredCount.ToString(),
            [BattleReplayService.RotationHoursKey] = rotationHours.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [BattleReplayService.FeaturedWindowHoursKey] = (rotationHours * 2).ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();
        return new BattleReplayService(configuration, new TestEnvironment(), clock, NullLogger<BattleReplayService>.Instance);
    }

    private static void Store(BattleReplayService service, TimeProvider clock, string battleId, long ticksBase,
        int humans, int[] kickers)
    {
        var players = new List<(string, bool, int, int)>();
        for (var i = 0; i < humans; i++) players.Add(($"human-{battleId}-{i}", false, kickers[i % kickers.Length], i % 2));
        for (var i = humans; i < 4; i++) players.Add(($"bot-{battleId}-{i}", true, kickers[i % kickers.Length], i % 2));
        var blob = Gzip(BuildArchive(
            ("header", Encoding.UTF8.GetBytes(HeaderJson(ticks: DateTime.UnixEpoch.Ticks + ticksBase))),
            ("battle", Encoding.UTF8.GetBytes(BattleJson(battleId, players.ToArray()))),
            ("result", Encoding.UTF8.GetBytes(ResultJson([humans, 4 - humans], players[0].Item1)))));
        Assert.True(service.StoreUpload(battleId, blob, clock.GetUtcNow().UtcDateTime));
    }

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

    private static WebApplicationFactory<Program> NewFactory(string replayDirectory) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.UseSetting(BattleReplayService.DirectoryKey, replayDirectory);
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
        var json = body is null ? "{}" : JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(json), session.Key, new byte[16]))
        };
        request.Headers.Host = Host;
        using var response = await session.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
        using var document = JsonDocument.Parse(D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), session.Key));
        return document.RootElement.Clone();
    }

    private sealed record DemoSession(HttpClient Client, byte[] Key, string UserId);

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kf-replay-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => System.IO.Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
