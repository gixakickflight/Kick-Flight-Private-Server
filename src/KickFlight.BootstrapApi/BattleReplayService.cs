using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi;

/// <summary>
/// The adapter's translation between the unmodified client's replay screens and otomo's patch recordings
/// (<see cref="IOtomoRecordings"/>). It stores nothing: no blob, no index, no rotation, no retention.
///
/// <list type="bullet">
/// <item><b>Upload</b> (Colorful /battle/upload): the gzip archive goes to patch as one recording keyed by the battle id,
/// its index row as the recording's <c>meta</c>, the kickers that played as <c>kicker:&lt;id&gt;</c> tags (plus the facts
/// the row shows: human count, rule, version), and the players who were in it as the recording's players.</item>
/// <item><b>Index</b> (/battleReplay/index): one type-2 channel per kicker from patch's by-tag listing (newest first), and
/// the type-3 "Featured" row from the published <c>patch.curated</c> list, in the order it was published.</item>
/// <item><b>Play</b> (/battleReplay/play and GET /battleReplay/blob/{keyId}): the client's own encryption
/// (<see cref="D2CCodec"/>) around bytes fetched from patch with the player's token. The URL is sealed, so no key table
/// exists to lose.</item>
/// </list>
///
/// The only state in this process is <see cref="RecordBattleEnd"/>'s map of the battle-end facts waiting for the
/// master client's upload (kicker costumes are not in the archive). It is request correlation, bounded and in memory,
/// like the matchmaking room it is copied from; losing it costs a default costume on a row.
///
/// The archive the client uploads is NOT a zip: it is <c>gzip( [TYPE_UINT][uint32-le length][TYPE_STRING]
/// [int32-le nameLen][utf8 name][raw bytes] ... )</c>. See docs/REPLAYS.md for the RVAs that pin the encoding.
/// </summary>
public sealed class BattleReplayService
{
    // ReplayValue value-type tags (Colorful.ReplayValue, dump.cs:531629-531646). Only the two the archive uses.
    private const byte TypeUInt = 2;
    private const byte TypeString = 6;

    // 2030 is the sentinel most other served windows use; the Featured/Kicker windows never expire.
    private const string FarFutureDatetime = "2099-12-31 23:59:59";

    // The recording kind patch stores these under (kindPattern: lowercase, dotted or dashed).
    public const string RecordingKind = "kickflight.replay";

    // Patch's tag budget (services/patch/internal/recording/recording.go:41): 16 per recording.
    private const int MaxTags = 16;

    public const string KickerTagPrefix = "kicker:";

    private readonly IOtomoRecordings _recordings;
    private readonly OtomoRecordingsOptions _options;
    private readonly byte[] _sealKey;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BattleReplayService> _logger;

    private readonly ConcurrentDictionary<string, PendingMatch> _pending = new(StringComparer.Ordinal);

    public BattleReplayService(IOtomoRecordings recordings, IOptions<OtomoRecordingsOptions> options,
        IConfiguration configuration, TimeProvider timeProvider, ILogger<BattleReplayService> logger)
    {
        _recordings = recordings;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;

        var secret = !string.IsNullOrWhiteSpace(_options.PlaySecret)
            ? _options.PlaySecret
            : configuration[$"{OtomoOptions.SectionName}:TokenSecret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            _sealKey = RandomNumberGenerator.GetBytes(32);
            _logger.LogWarning("Otomo:Recordings:PlaySecret (and Otomo:TokenSecret) is not set: replay play URLs are sealed with " +
                "a key generated at startup and stop working at a restart");
        }
        else
        {
            _sealKey = SHA256.HashData(Encoding.UTF8.GetBytes("kf-replay-play:" + secret));
        }
    }

    public long MaxUploadBytes => _options.MaxUploadBytes;

    public bool Enabled => _recordings.Enabled;

    // ------------------------------------------------------------------ pending matches (/battle/end, custom)

    public sealed class PendingPlayer
    {
        public string UserId { get; set; } = "";
        public string Name { get; set; } = "";
        public int Rank { get; set; }
        public int KickerId { get; set; }
        public int KickerCostumeId { get; set; }
        public int FrameId { get; set; }
        public string LanguageCode { get; set; } = "";
        public int TeamType { get; set; }
        public bool Ai { get; set; }
        public bool Mvp { get; set; }
    }

    public sealed class PendingTeam
    {
        public int TeamType { get; set; }
        public int Score { get; set; }
        public int RemainingDistance { get; set; }
        public int LastRemainingDistance { get; set; }
    }

    /// <summary>The match facts /battle/end carries before (and independently of) the upload.</summary>
    public sealed class PendingMatch
    {
        public string BattleId { get; set; } = "";
        public bool Custom { get; set; }
        public int BattleRuleId { get; set; }
        public int FieldId { get; set; }
        public int HumanCount { get; set; }
        public List<PendingTeam> Teams { get; set; } = [];
        public List<PendingPlayer> Players { get; set; } = [];
        public DateTime EndUtc { get; set; }
    }

    /// <summary>Remembers the battle-end facts until the master client's /battle/upload arrives for the same id.</summary>
    public void RecordBattleEnd(PendingMatch match, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(match.BattleId)) return;
        match.EndUtc = nowUtc;
        _pending[SafeId(match.BattleId)] = match;
        // The upload for a normal match usually follows within seconds; keep the map from growing without bound.
        if (_pending.Count > 2048)
        {
            foreach (var stale in _pending.Where(kv => nowUtc - kv.Value.EndUtc > TimeSpan.FromHours(6)).Select(kv => kv.Key).ToList())
                _pending.TryRemove(stale, out _);
        }
        _logger.LogInformation(
            "Recorded battle-end {BattleId} (custom={Custom}, rule={RuleId}, field={FieldId}, humans={Humans})",
            match.BattleId, match.Custom, match.BattleRuleId, match.FieldId, match.HumanCount);
    }

    // ------------------------------------------------------------------ the index row

    public sealed class ReplayPlayerRecord
    {
        public string UserId { get; set; } = "";
        public string Name { get; set; } = "";
        public int FrameId { get; set; }
        public string LanguageCode { get; set; } = "";
        public int Rank { get; set; }
        public int KickerId { get; set; }
        public int KickerCostumeId { get; set; }
        public bool MvpFlag { get; set; }
    }

    public sealed class ReplayTeamRecord
    {
        public int TeamType { get; set; }
        public int Score { get; set; }
        public int RemainingDistance { get; set; }
        public int LastRemainingDistance { get; set; }
        public List<ReplayPlayerRecord> Players { get; set; } = [];
    }

    /// <summary>
    /// What the index row shows, stored as the recording's <c>meta</c> object in patch (an opaque JSON object, at most
    /// 64 KiB, that patch keeps verbatim and never reads). The bytes, their size and digest, who was in it and when
    /// patch received it are patch's own fields on the recording.
    /// </summary>
    public sealed class ReplayRecord
    {
        // Filled from the recording's id when a row is read back; it is not part of the stored meta.
        public string ReplayId { get; set; } = "";
        public string BattleId { get; set; } = "";
        public string ApplicationVersion { get; set; } = "";
        public string EndDatetime { get; set; } = "";
        public int BattleRuleId { get; set; }
        public int FieldId { get; set; }
        public int HumanCount { get; set; }
        public int[] KickerIds { get; set; } = [];
        public List<ReplayTeamRecord> Teams { get; set; } = [];
        public DateTime EndUtc { get; set; }
    }

    // ------------------------------------------------------------------ upload

    /// <summary>
    /// Sends one uploaded replay to patch. Returns true when patch created the recording and false when it already held
    /// the battle (the master client is the only uploader, so the first upload wins: patch answers 200 for an identical
    /// retry and 409 recording_conflict for anything else under that id, and both are "already stored"), an oversized
    /// body, or an archive that does not parse. A patch that cannot be reached or refuses for another reason throws
    /// <see cref="OtomoRecordingsException"/>: the caller's client is told, because nothing here can retry it later.
    /// </summary>
    /// <param name="uploaderOtomoPlayerId">The master client's otomo account; always one of the recording's players.</param>
    /// <param name="resolveOtomoPlayer">A battle's numeric user id to the otomo account behind it, or null (a bot, or a
    /// player this server has no link for).</param>
    public async Task<bool> StoreUploadAsync(string requestedBattleId, byte[] gzip, string uploaderOtomoPlayerId,
        Func<string, string?> resolveOtomoPlayer, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (gzip.Length == 0 || gzip.Length > _options.MaxUploadBytes)
        {
            _logger.LogWarning("Rejected replay upload for {BattleId}: {Bytes} bytes (cap {Cap})",
                requestedBattleId, gzip.Length, _options.MaxUploadBytes);
            return false;
        }

        ParsedReplay parsed;
        try
        {
            parsed = ParseGzip(gzip);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
        {
            _logger.LogWarning("Rejected replay upload for {BattleId}: archive did not parse: {Error}",
                requestedBattleId, ex.Message);
            return false;
        }

        var battleId = string.IsNullOrWhiteSpace(parsed.BattleId) ? requestedBattleId : parsed.BattleId!;
        if (string.IsNullOrWhiteSpace(battleId)) return false;
        var replayId = SafeId(battleId);

        _pending.TryGetValue(replayId, out var pending);
        pending ??= _pending.Values.FirstOrDefault(p => p.Custom && p.BattleId == requestedBattleId);

        var record = BuildRecord(replayId, battleId, parsed, pending, nowUtc);
        var players = RecordingPlayers(uploaderOtomoPlayerId, parsed, pending, resolveOtomoPlayer);
        var upload = new RecordingUpload(RecordingKind, gzip, players, RecordingTags(record, pending),
            JsonSerializer.Serialize(record));

        bool created;
        try
        {
            created = await _recordings.PutAsync(replayId, upload, cancellationToken).ConfigureAwait(false);
        }
        catch (OtomoRecordingsException ex) when (ex is { Failure: OtomoRecordingsFailure.Refused, Code: "recording_conflict" })
        {
            _logger.LogInformation("Ignoring duplicate replay upload for {BattleId} (first one is kept)", battleId);
            if (pending is not null) _pending.TryRemove(replayId, out _);
            return false;
        }

        if (pending is not null) _pending.TryRemove(SafeId(pending.BattleId), out _);
        _logger.LogInformation(
            "Sent replay {ReplayId} for {BattleId} to patch ({Outcome}): version={AppVersion} rule={RuleId} field={FieldId} humans={Humans} size={Bytes} players={Players} tags={Tags}",
            replayId, battleId, created ? "created" : "already held", parsed.AppVersion, record.BattleRuleId, record.FieldId,
            record.HumanCount, gzip.Length, players.Count, string.Join(',', upload.Tags));
        return created;
    }

    // Who was in the recording, in patch's terms (otomo account ids): the uploader, plus every human the battle named
    // that this server can link to an account. Bots and unlinked ids are not players patch could ever match a token to.
    private static List<string> RecordingPlayers(string uploader, ParsedReplay parsed, PendingMatch? pending,
        Func<string, string?> resolve)
    {
        var players = new List<string>();
        void Add(string? id)
        {
            if (!string.IsNullOrEmpty(id) && PatchIdPattern.IsMatch(id) && !players.Contains(id, StringComparer.Ordinal))
                players.Add(id);
        }

        Add(uploader);
        var userIds = parsed.Players.Where(p => !p.IsAi).Select(p => p.UserId)
            .Concat(pending?.Players.Where(p => !p.Ai).Select(p => p.UserId) ?? []);
        foreach (var userId in userIds)
        {
            if (string.IsNullOrEmpty(userId) || userId.StartsWith("bot-", StringComparison.OrdinalIgnoreCase)) continue;
            Add(resolve(userId));
            if (players.Count == 64) break; // services/patch/internal/recording/recording.go:34
        }
        return players;
    }

    // services/patch/internal/recording/recording.go:44-50 (the id alphabet a recording and a player share).
    private static readonly System.Text.RegularExpressions.Regex PatchIdPattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // services/patch/internal/recording/recording.go:57-66 (the tag alphabet).
    private static readonly System.Text.RegularExpressions.Regex PatchTagPattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The tags a recording is filed under. One <c>kicker:&lt;id&gt;</c> per kicker that played, which is what the channel
    /// rows are read by; then the facts the index row shows (<c>humans:&lt;n&gt;</c>, <c>rule:&lt;id&gt;</c>,
    /// <c>ver:&lt;appVersion&gt;</c>, and <c>custom</c> for a custom battle). Patch allows 16 per recording, so the kickers
    /// are written first and the facts take what is left.
    /// </summary>
    public static IReadOnlyList<string> RecordingTags(ReplayRecord record, PendingMatch? pending)
    {
        var tags = new List<string>();
        void Add(string tag)
        {
            if (tags.Count < MaxTags && PatchTagPattern.IsMatch(tag) && !tags.Contains(tag, StringComparer.Ordinal))
                tags.Add(tag);
        }

        foreach (var kickerId in record.KickerIds) Add(KickerTagPrefix + kickerId);
        Add("humans:" + record.HumanCount);
        Add("rule:" + record.BattleRuleId);
        if (!string.IsNullOrEmpty(record.ApplicationVersion)) Add("ver:" + record.ApplicationVersion);
        if (pending?.Custom == true) Add("custom");
        return tags;
    }

    private ReplayRecord BuildRecord(string replayId, string battleId, ParsedReplay parsed, PendingMatch? pending,
        DateTime nowUtc)
    {
        // Pending roster supplies the kicker row costume ids: PlayerBattleInfo in the archive has no costume field.
        var costumeByUser = new Dictionary<string, int>(StringComparer.Ordinal);
        if (pending is not null)
        {
            foreach (var p in pending.Players)
                if (!string.IsNullOrEmpty(p.UserId) && p.KickerCostumeId > 0) costumeByUser[p.UserId] = p.KickerCostumeId;
        }

        var teams = new Dictionary<int, ReplayTeamRecord>();
        foreach (var player in parsed.Players)
        {
            if (!teams.TryGetValue(player.TeamType, out var team))
            {
                team = new ReplayTeamRecord { TeamType = player.TeamType };
                teams[player.TeamType] = team;
            }
            team.Players.Add(new ReplayPlayerRecord
            {
                UserId = player.UserId,
                Name = player.Name,
                FrameId = player.FrameId,
                LanguageCode = player.LanguageCode,
                Rank = player.Rank,
                KickerId = player.KickerId,
                KickerCostumeId = costumeByUser.GetValueOrDefault(player.UserId, 0),
                MvpFlag = !string.IsNullOrEmpty(parsed.MvpUserId) && player.UserId == parsed.MvpUserId
            });
        }

        int ScoreAt(int teamType) => teamType >= 0 && teamType < parsed.Scores.Length ? parsed.Scores[teamType] : 0;
        int MinDistanceAt(int teamType) => teamType >= 0 && teamType < parsed.MinRemaining.Length ? parsed.MinRemaining[teamType] : 0;
        int LastFlagAt(int teamType) => teamType >= 0 && teamType < parsed.LastFlag.Length ? parsed.LastFlag[teamType] : 0;

        if (teams.Count > 0)
        {
            foreach (var team in teams.Values)
            {
                team.Score = ScoreAt(team.TeamType);
                team.RemainingDistance = MinDistanceAt(team.TeamType);
                team.LastRemainingDistance = LastFlagAt(team.TeamType);
            }
        }
        else if (pending is not null)
        {
            // Nothing parsed from the archive roster: fall back to the battle-end snapshot, costumes included.
            foreach (var pendingTeam in pending.Teams)
            {
                var team = new ReplayTeamRecord
                {
                    TeamType = pendingTeam.TeamType,
                    Score = pendingTeam.Score,
                    RemainingDistance = pendingTeam.RemainingDistance,
                    LastRemainingDistance = pendingTeam.LastRemainingDistance
                };
                foreach (var p in pending.Players.Where(p => p.TeamType == pendingTeam.TeamType))
                {
                    team.Players.Add(new ReplayPlayerRecord
                    {
                        UserId = p.UserId,
                        Name = p.Name,
                        FrameId = p.FrameId,
                        LanguageCode = p.LanguageCode,
                        Rank = p.Rank,
                        KickerId = p.KickerId,
                        KickerCostumeId = p.KickerCostumeId,
                        MvpFlag = p.Mvp
                    });
                }
                teams[team.TeamType] = team;
            }
        }

        var parsedHumans = parsed.HasAiFlag
            ? parsed.Players.Count(p => !p.IsAi && !p.UserId.StartsWith("bot-", StringComparison.OrdinalIgnoreCase))
            : 0;
        var humanCount = Math.Max(parsedHumans, pending?.HumanCount ?? 0);

        var kickerIds = parsed.Players.Select(p => p.KickerId).Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
        if (kickerIds.Length == 0 && pending is not null)
            kickerIds = pending.Players.Select(p => p.KickerId).Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();

        // The header's _dateTimeTicks is device wall-clock. It is only trusted when it is near the receipt time:
        // a wrong sign, a different ticks epoch or a clock-skewed phone must not put a 1970 date on the index row
        // (the index row shows this value).
        var endUtc = parsed.EndUtc is { } parsedEnd && Math.Abs((parsedEnd - nowUtc).TotalDays) <= 1 ? parsedEnd : nowUtc;
        return new ReplayRecord
        {
            ReplayId = replayId,
            BattleId = battleId,
            ApplicationVersion = parsed.AppVersion,
            EndDatetime = endUtc.ToString("yyyy-MM-dd HH:mm:ss"),
            BattleRuleId = parsed.BattleRuleId > 0 ? parsed.BattleRuleId : pending?.BattleRuleId ?? 1,
            FieldId = parsed.FieldId > 0 ? parsed.FieldId : pending?.FieldId ?? 0,
            HumanCount = humanCount,
            KickerIds = kickerIds,
            Teams = teams.Values.OrderBy(t => t.TeamType).ToList(),
            EndUtc = endUtc
        };
    }


    // ------------------------------------------------------------------ index

    /// <summary>
    /// Builds the /battleReplay/index body: Featured (type 3, id 1) from the published curated list, plus one type-2
    /// channel per kicker from patch's by-tag listing. Order is patch's in both: the curated order as published, and
    /// newest first for a kicker. A curated list patch has never been given is an empty Featured row, not an invented one.
    /// </summary>
    public async Task<string> BuildIndexJsonAsync(string accessToken, IReadOnlyList<int> kickerIds,
        Func<int, int> defaultCostumeId, CancellationToken cancellationToken)
    {
        var displayStart = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss");

        var featuredTask = _recordings.ListCuratedAsync(accessToken, _options.FeaturedList, cancellationToken);
        var kickerTasks = kickerIds.Select(kickerId => _recordings.ListByTagAsync(accessToken,
            KickerTagPrefix + kickerId, _options.KickerChannelSize, cancellationToken)).ToArray();
        await Task.WhenAll(kickerTasks.Cast<Task>().Append(featuredTask)).ConfigureAwait(false);

        var channels = new List<object>
        {
            new
            {
                battleReplayChannelId = 1,
                battleReplayList = BuildReplayJson(ToRecords(await featuredTask.ConfigureAwait(false) ?? []), defaultCostumeId, displayStart)
            }
        };
        for (var i = 0; i < kickerIds.Count; i++)
        {
            channels.Add(new
            {
                battleReplayChannelId = 1000 + kickerIds[i],
                battleReplayList = BuildReplayJson(ToRecords(await kickerTasks[i].ConfigureAwait(false)), defaultCostumeId, displayStart)
            });
        }

        return JsonSerializer.Serialize(new
        {
            battleReplayChannelList = channels.ToArray(),
            appMovieList = Array.Empty<object>()
        });
    }

    // A recording's meta back into the row it was uploaded as. A recording whose meta is not ours (somebody else's
    // producer, an older shape) cannot be drawn as a row and is left out, not guessed at.
    private IEnumerable<ReplayRecord> ToRecords(IEnumerable<PatchRecording> recordings)
    {
        foreach (var recording in recordings)
        {
            ReplayRecord? record = null;
            try
            {
                record = recording.Meta.ValueKind == JsonValueKind.Object
                    ? recording.Meta.Deserialize<ReplayRecord>()
                    : null;
            }
            catch (JsonException)
            {
                // fall through: logged below
            }
            if (record is null || record.Teams.Count == 0 && string.IsNullOrEmpty(record.BattleId))
            {
                _logger.LogWarning("Leaving recording {RecordingId} out of the replay index: its meta is not a replay row", recording.Id);
                continue;
            }
            record.ReplayId = recording.Id;
            yield return record;
        }
    }

    private static object[] BuildReplayJson(IEnumerable<ReplayRecord> records, Func<int, int> defaultCostumeId,
        string displayStart)
    {
        return records.Select(record => (object)new
        {
            battleReplayId = record.ReplayId,
            battleRuleId = record.BattleRuleId,
            fieldId = record.FieldId,
            endDatetime = record.EndDatetime,
            displayStartDatetime = displayStart,
            displayEndDatetime = FarFutureDatetime,
            applicationVersion = record.ApplicationVersion,
            battleReplayTeamList = record.Teams.Select(team => new
            {
                teamType = team.TeamType,
                score = team.Score,
                remainingDistance = team.RemainingDistance,
                lastRemainingDistance = team.LastRemainingDistance,
                battleReplayPlayerList = team.Players.Select(player => new
                {
                    userId = player.UserId,
                    name = player.Name,
                    frameId = player.FrameId,
                    languageCode = player.LanguageCode,
                    rank = player.Rank,
                    kickerCostumeId = player.KickerCostumeId > 0 ? player.KickerCostumeId : defaultCostumeId(player.KickerId),
                    mvpFlag = player.MvpFlag
                }).ToArray()
            }).ToArray()
        }).ToArray();
    }

    // ------------------------------------------------------------------ playback

    /// <summary>
    /// Mints the URL id and encryption key /battleReplay/play answers with, or ("","") when the player cannot fetch that
    /// recording (patch says 404 for one that does not exist and for one the player was not in).
    ///
    /// Nothing is stored. The id is the replay id, the player's otomo device and an expiry sealed with AES-GCM under the
    /// server's secret, and the key is an HMAC of that id, so the blob request (which carries no session) can open the id,
    /// fetch with the right player's token and encrypt with the key the client was given. A replayed URL works until it
    /// expires, as the one-shot map it replaces did (the entry was only dropped by expiry).
    /// </summary>
    public async Task<(string KeyId, string EncryptionKey)> CreatePlayKeyAsync(string replayId, string otomoDeviceId,
        string accessToken, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(replayId) || !PatchIdPattern.IsMatch(replayId)) return ("", "");
        if (!await _recordings.CanFetchAsync(accessToken, replayId, cancellationToken).ConfigureAwait(false)) return ("", "");

        var keyId = Seal(new PlayClaim(replayId, otomoDeviceId, new DateTimeOffset(nowUtc, TimeSpan.Zero).Add(_options.PlayLifetime).ToUnixTimeSeconds()));
        return (keyId, DeriveKey(keyId));
    }

    /// <summary>
    /// Encrypts the recording exactly as the client expects (D2CCodec = IV || AES-256-CBC/PKCS7) from patch's bytes.
    /// <paramref name="accessTokenForDevice"/> turns the sealed device into that player's current access token.
    /// Returns null for an id that is forged, expired, or whose recording patch no longer serves.
    /// </summary>
    public async Task<byte[]?> TryEncodeBlobAsync(string keyId, DateTime nowUtc,
        Func<string, CancellationToken, Task<string>> accessTokenForDevice, CancellationToken cancellationToken)
    {
        var claim = Open(keyId);
        if (claim is null || claim.ExpiresUnix < new DateTimeOffset(nowUtc, TimeSpan.Zero).ToUnixTimeSeconds()) return null;

        var accessToken = await accessTokenForDevice(claim.Device, cancellationToken).ConfigureAwait(false);
        var blob = await _recordings.GetBlobAsync(accessToken, claim.Replay, cancellationToken).ConfigureAwait(false);
        return blob is null
            ? null
            : D2CCodec.Encode(blob, Encoding.ASCII.GetBytes(DeriveKey(keyId)),
                RandomNumberGenerator.GetBytes(D2CCodec.VectorSizeBytes));
    }

    private sealed record PlayClaim(string Replay, string Device, long ExpiresUnix);

    private string DeriveKey(string keyId) =>
        Convert.ToHexString(HMACSHA256.HashData(_sealKey, Encoding.UTF8.GetBytes("key:" + keyId))[..16]); // 32 ASCII chars = AES-256 key material

    private string Seal(PlayClaim claim)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(claim);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(_sealKey, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Base64Url(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    private PlayClaim? Open(string keyId)
    {
        try
        {
            var raw = FromBase64Url(keyId);
            if (raw.Length <= 28) return null;
            var plain = new byte[raw.Length - 28];
            using var aes = new AesGcm(_sealKey, 16);
            aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
            return JsonSerializer.Deserialize<PlayClaim>(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or ArgumentException)
        {
            return null; // forged, truncated, or sealed by another key
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    // ------------------------------------------------------------------ archive parsing

    private sealed class ParsedPlayer
    {
        public string UserId { get; set; } = "";
        public string Name { get; set; } = "";
        public int Rank { get; set; }
        public int KickerId { get; set; }
        public int TeamType { get; set; }
        public int FrameId { get; set; }
        public string LanguageCode { get; set; } = "";
        public bool IsAi { get; set; }
    }

    private sealed class ParsedReplay
    {
        public string? BattleId { get; set; }
        public string AppVersion { get; set; } = "";
        public int BattleRuleId { get; set; }
        public int FieldId { get; set; }
        public DateTime? EndUtc { get; set; }
        public bool HasAiFlag { get; set; }
        public string MvpUserId { get; set; } = "";
        public int[] Scores { get; set; } = [];
        public int[] MinRemaining { get; set; } = [];
        public int[] LastFlag { get; set; } = [];
        public List<ParsedPlayer> Players { get; } = [];
    }

    private ParsedReplay ParseGzip(byte[] gzip)
    {
        using var input = new MemoryStream(gzip);
        using var gunzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = gunzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > _options.MaxDecompressedBytes)
                throw new InvalidDataException($"decompressed archive exceeds {_options.MaxDecompressedBytes} bytes");
            output.Write(buffer, 0, read);
        }

        var archive = ParseArchive(output.GetBuffer().AsSpan(0, (int)output.Length));
        var parsed = new ParsedReplay();
        if (archive.Header is not null)
        {
            using var header = JsonDocument.Parse(archive.Header);
            parsed.AppVersion = GetString(header.RootElement, "appVersion");
            var ticks = GetLong(header.RootElement, "dateTimeTicks");
            if (ticks > DateTime.UnixEpoch.Ticks && ticks < DateTime.MaxValue.Ticks)
            {
                try { parsed.EndUtc = new DateTime(ticks, DateTimeKind.Utc); }
                catch (ArgumentOutOfRangeException) { /* leave null */ }
            }
        }
        if (archive.Battle is not null)
        {
            using var battle = JsonDocument.Parse(archive.Battle);
            var root = battle.RootElement;
            if (TryGetProperty(root, "battleRuleInfo", out var rule))
            {
                parsed.BattleRuleId = GetInt(rule, "battleRuleId");
                parsed.FieldId = GetInt(rule, "fieldId");
            }
            if (TryGetProperty(root, "battleInfo", out var battleInfo))
            {
                parsed.BattleId = GetString(battleInfo, "battleId");
                ReadRoster(battleInfo, parsed);
            }
        }
        if (archive.Result is not null)
        {
            using var result = JsonDocument.Parse(archive.Result);
            var root = result.RootElement;
            parsed.Scores = ReadIntArray(root, "score");
            parsed.MinRemaining = ReadIntArray(root, "minRemainingDistance");
            parsed.LastFlag = ReadIntArray(root, "lastFlagProgressRate");
            parsed.MvpUserId = GetString(root, "mvpUserId");
            if (parsed.Players.Count == 0 && TryGetProperty(root, "battleInfo", out var resultRoster))
                ReadRoster(resultRoster, parsed);
            if (parsed.BattleId is null && TryGetProperty(root, "battleInfo", out _))
            {
                // The result member does not carry the battle id; nothing else to do here.
            }
        }
        return parsed;
    }

    private static void ReadRoster(JsonElement rosterElement, ParsedReplay parsed)
    {
        if (!TryGetProperty(rosterElement, "playerBattleInfos", out var players) || players.ValueKind != JsonValueKind.Array)
            return;
        var hasAiFlag = false;
        foreach (var player in players.EnumerateArray())
        {
            if (TryGetProperty(player, "isAi", out var aiProp) && aiProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                hasAiFlag = true;
            var aiId = GetInt(player, "kickerAiParameterId");
            var userId = GetString(player, "userId");
            parsed.Players.Add(new ParsedPlayer
            {
                UserId = userId,
                Name = GetString(player, "name"),
                Rank = GetInt(player, "rank"),
                KickerId = GetInt(player, "kickerId"),
                TeamType = GetInt(player, "teamType"),
                FrameId = GetInt(player, "frameId"),
                LanguageCode = GetString(player, "languageCode"),
                IsAi = (aiProp.ValueKind == JsonValueKind.True) || aiId > 0 ||
                       userId.StartsWith("bot-", StringComparison.OrdinalIgnoreCase)
            });
        }
        if (hasAiFlag) parsed.HasAiFlag = true;
    }

    public sealed class ReplayArchive
    {
        public byte[]? Header { get; set; }
        public byte[]? Battle { get; set; }
        public byte[]? Result { get; set; }
    }

    /// <summary>
    /// Reads the [TYPE_UINT][uint32-le length][TYPE_STRING][int32-le nameLen][utf8 name][raw bytes] archive.
    /// Unknown members (frames, future files) are skipped but still validated so a corrupt stream fails.
    /// </summary>
    public static ReplayArchive ParseArchive(ReadOnlySpan<byte> archive)
    {
        var result = new ReplayArchive();
        var position = 0;
        while (position < archive.Length)
        {
            if (position + 1 > archive.Length || archive[position++] != TypeUInt)
                throw new InvalidDataException("archive member length tag is not TYPE_UINT");
            if (position + 4 > archive.Length)
                throw new InvalidDataException("archive member length is truncated");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(archive[position..]);
            position += 4;
            if (length > int.MaxValue)
                throw new InvalidDataException("archive member length is too large");

            if (position + 1 > archive.Length || archive[position++] != TypeString)
                throw new InvalidDataException("archive member name tag is not TYPE_STRING");
            if (position + 4 > archive.Length)
                throw new InvalidDataException("archive member name length is truncated");
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(archive[position..]);
            position += 4;
            if (nameLength < 0 || position + nameLength > archive.Length)
                throw new InvalidDataException("archive member name is truncated");
            var name = Encoding.UTF8.GetString(archive.Slice(position, nameLength));
            position += nameLength;

            if (position + (int)length > archive.Length)
                throw new InvalidDataException("archive member body is truncated");
            var body = archive.Slice(position, (int)length).ToArray();
            position += (int)length;

            // The client writes each member under its full device path (".../Replay/Work/header") and the JSON
            // members start with a UTF-8 BOM, which JsonDocument.Parse(byte[]) rejects.
            name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
            if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF) body = body[3..];

            switch (name)
            {
                case "header": result.Header = body; break;
                case "battle": result.Battle = body; break;
                case "result": result.Result = body; break;
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ JSON helpers (client field names keep the leading underscore)

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }
        if (element.TryGetProperty("_" + name, out value)) return true;
        return element.TryGetProperty(name, out value);
    }

    private static string GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int GetInt(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static long GetLong(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
            ? parsed
            : 0;

    private static int[] ReadIntArray(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        var list = new List<int>();
        foreach (var item in value.EnumerateArray())
            list.Add(item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var parsed) ? parsed : 0);
        return list.ToArray();
    }

    /// <summary>A file- and URL-safe id. battle-N / custom-... survive verbatim; anything else is hashed.</summary>
    internal static string SafeId(string battleId)
    {
        if (battleId.Length is > 0 and <= 64 && battleId.All(c =>
                char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return battleId;
        }
        return "r-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(battleId)))[..24].ToLowerInvariant();
    }
}
