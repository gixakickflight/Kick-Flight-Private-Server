using System.Collections.Concurrent;
using System.Text.Json;
using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.Extensions.Logging;

namespace KickFlight.BootstrapApi;

/// <summary>
/// Executes only badge conditions whose facts are persisted and verified by this server. Catalog entries for
/// other game conditions remain visible to the catalog tooling but are never guessed into an award.
/// </summary>
public sealed class BadgeAwardRules
{
    public const int DefaultHonorId = 6010000;
    private const string CatalogRelativePath = "config/badge_rule_catalog.json";
    private static readonly ConcurrentDictionary<long, object> PlayerLocks = new();

    private readonly IReadOnlyList<BattleCountRule> _battleCountRules;
    private readonly IReadOnlyList<DiscUpgradeRule> _discUpgradeRules;
    private readonly IReadOnlyList<LoginDayRule> _loginDayRules;
    private readonly IReadOnlyList<RegistrationRule> _registrationRules;
    private readonly IReadOnlyList<RankRule> _rankRules;
    private readonly IReadOnlyList<StatRule> _statRules;
    public int SupportedRuleCount => _battleCountRules.Count + _discUpgradeRules.Count + _loginDayRules.Count
        + _registrationRules.Count + _rankRules.Count + _statRules.Count;
    /// <summary>Rules driven by client-reported-but-bounded battle stats (or confirmed likes).</summary>
    public int StatRuleCount => _statRules.Count;
    /// <summary>The trust label of a stat rule's honor, or null when the honor has no stat rule.</summary>
    public string? GetStatRuleTrust(int honorId) => _statRules.FirstOrDefault(rule => rule.HonorId == honorId)?.Trust;

    private BadgeAwardRules(
        IReadOnlyList<BattleCountRule> battleCountRules,
        IReadOnlyList<DiscUpgradeRule> discUpgradeRules,
        IReadOnlyList<LoginDayRule> loginDayRules,
        IReadOnlyList<RegistrationRule> registrationRules,
        IReadOnlyList<RankRule> rankRules,
        IReadOnlyList<StatRule>? statRules = null)
    {
        _statRules = statRules ?? [];
        _battleCountRules = battleCountRules;
        _discUpgradeRules = discUpgradeRules;
        _loginDayRules = loginDayRules;
        _registrationRules = registrationRules;
        _rankRules = rankRules;
    }

    public static BadgeAwardRules Load(string contentRoot, ILogger? logger = null)
    {
        try
        {
            var path = RepositoryPaths.Resolve(CatalogRelativePath, contentRoot);
            if (!File.Exists(path))
            {
                logger?.LogError("Badge rule catalog missing at {Path}; gameplay badge awards are disabled", path);
                return new BadgeAwardRules([], [], [], [], []);
            }

            var rules = Parse(File.ReadAllText(path));
            if (rules.SupportedRuleCount == 0)
                logger?.LogError("Badge rule catalog at {Path} has no supported award rules", path);
            else
                logger?.LogInformation("Loaded {Count} executable badge rules from {Path}", rules.SupportedRuleCount, path);
            return rules;
        }
        catch (Exception ex)
        {
            // A missing or malformed catalog must fail closed: a startup problem should not grant badges.
            logger?.LogError(ex, "Could not load badge rule catalog from {Path}; gameplay badge awards are disabled", CatalogRelativePath);
            return new BadgeAwardRules([], [], [], [], []);
        }
    }

    public static BadgeAwardRules Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return new BadgeAwardRules([], [], [], [], []);

        var rules = new Dictionary<int, BattleCountRule>();
        var discRules = new Dictionary<int, DiscUpgradeRule>();
        var loginRules = new Dictionary<int, LoginDayRule>();
        var registrationRules = new Dictionary<int, RegistrationRule>();
        var rankRules = new Dictionary<int, RankRule>();
        var statRules = new Dictionary<int, StatRule>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id) || id <= 0)
                continue;
            if (!entry.TryGetProperty("requirements", out var requirements) || requirements.ValueKind != JsonValueKind.Object)
                continue;
            if (!requirements.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
                continue;
            if (!requirements.TryGetProperty("target", out var targetElement) || !targetElement.TryGetInt32(out var target) || target <= 0)
                continue;

            switch (kindElement.GetString())
            {
                case "battle_count":
                    rules.TryAdd(id, new BattleCountRule(id, target));
                    break;
                case "disc_upgrades":
                    discRules.TryAdd(id, new DiscUpgradeRule(id, target));
                    break;
                case "login_days":
                    loginRules.TryAdd(id, new LoginDayRule(id, target));
                    break;
                case "registration_complete":
                    registrationRules.TryAdd(id, new RegistrationRule(id));
                    break;
                case "rank":
                    if (requirements.TryGetProperty("rule", out var ruleElement)
                        && ruleElement.TryGetInt32(out var battleRuleType)
                        && battleRuleType is >= 1 and <= 3)
                        rankRules.TryAdd(id, new RankRule(id, battleRuleType, target));
                    break;
                default:
                    if (TryParseStatRule(id, kindElement.GetString(), entry, requirements, target) is { } statRule)
                        statRules.TryAdd(id, statRule);
                    break;
            }
        }

        return new BadgeAwardRules(
            rules.Values.OrderBy(rule => rule.Target).ThenBy(rule => rule.HonorId).ToArray(),
            discRules.Values.OrderBy(rule => rule.Target).ThenBy(rule => rule.HonorId).ToArray(),
            loginRules.Values.OrderBy(rule => rule.Target).ThenBy(rule => rule.HonorId).ToArray(),
            registrationRules.Values.OrderBy(rule => rule.HonorId).ToArray(),
            rankRules.Values.OrderBy(rule => rule.BattleRuleType).ThenBy(rule => rule.Target).ThenBy(rule => rule.HonorId).ToArray(),
            statRules.Values.OrderBy(rule => rule.HonorId).ToArray());
    }

    /// <summary>
    /// Stat rules fail closed: an unknown kind, a missing/unknown trust label, a missing discriminator or a
    /// non-positive threshold yields no rule. Only the client_reported_bounded label is accepted here because every
    /// one of these kinds reads numbers the client reported (or likes confirmed against a room roster).
    /// </summary>
    private static StatRule? TryParseStatRule(
        int id, string? kind, JsonElement entry, JsonElement requirements, int target)
    {
        if (!entry.TryGetProperty("trust", out var trustElement) || trustElement.ValueKind != JsonValueKind.String
            || trustElement.GetString() != BadgeTrust.ClientReportedBounded)
            return null;

        int Int(string name) => requirements.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var number) ? number : 0;
        string Text(string name) => requirements.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? "" : "";

        switch (kind)
        {
            case "wins_total":
                return new StatRule(id, StatRuleKind.WinsTotal, target, 0, "", BadgeTrust.ClientReportedBounded);
            case "kicker_wins" when Int("kickerId") > 0:
                return new StatRule(id, StatRuleKind.KickerWins, target, Int("kickerId"), "", BadgeTrust.ClientReportedBounded);
            case "mode_wins" when Int("battleRuleType") is >= 1 and <= 3:
                return new StatRule(id, StatRuleKind.ModeWins, target, Int("battleRuleType"), "", BadgeTrust.ClientReportedBounded);
            case "queue_wins" when Text("queue") is "solo" or "team":
                return new StatRule(id, StatRuleKind.QueueWins, target, 0, Text("queue"), BadgeTrust.ClientReportedBounded);
            case "win_streak" when Int("battleRuleType") is >= 1 and <= 3:
                return new StatRule(id, StatRuleKind.WinStreak, target, Int("battleRuleType"), "", BadgeTrust.ClientReportedBounded);
            case "mvp_count":
                return new StatRule(id, StatRuleKind.StatTotal, target, 0, BadgeStatNames.Mvp, BadgeTrust.ClientReportedBounded);
            case "stat_total" when BadgeStatNames.All.Contains(Text("stat")):
                return new StatRule(id, StatRuleKind.StatTotal, target, 0, Text("stat"), BadgeTrust.ClientReportedBounded);
            default:
                return null;
        }
    }

    public static IReadOnlyList<int> GetOwnedHonorIds(SessionState state)
    {
        EnsureCollections(state);
        if (!state.UserHonorIds.Contains(DefaultHonorId)) state.UserHonorIds.Add(DefaultHonorId);
        return state.UserHonorIds.Distinct().Order().ToArray();
    }

    public static bool EquipHonor(SessionState state, int honorId)
    {
        if (honorId <= 0 || !GetOwnedHonorIds(state).Contains(honorId)) return false;
        state.HonorId = honorId;
        return true;
    }

    public void SavePlayerState(
        SessionState state, IPlayerStore store, string? uuid = null, bool allowEquippedHonorChange = false)
    {
        if (state.PlayerId <= 0)
        {
            store.Save(state, uuid);
            return;
        }

        lock (PlayerLocks.GetOrAdd(state.PlayerId, static _ => new object()))
        {
            var latest = store.TryLoad(state.PlayerId);
            if (latest is not null) MergeProgress(latest, state, allowEquippedHonorChange);
            EnsureCollections(state);
            if (!state.UserHonorIds.Contains(DefaultHonorId)) state.UserHonorIds.Add(DefaultHonorId);
            if (!state.UserHonorIds.Contains(state.HonorId)) state.HonorId = DefaultHonorId;
            store.Save(state, uuid);
        }
    }

    /// <summary>
    /// Counts one battle only when the caller has already proved that the issued result entry belongs to this player.
    /// The store applies it at most once per entry: under a PostgreSQL row lock (any number of API processes) or a
    /// per-player lock (JSON store); a retry returns the awards of the first application.
    /// </summary>
    public IReadOnlyList<int> RecordVerifiedBattle(
        SessionState state, IPlayerStore store, string battleEntryId, int? rankedBattleRuleType = null, int? rankAfter = null,
        BadgeBattleStatReport? stats = null, DateTimeOffset? now = null)
    {
        if (state.PlayerId <= 0 || string.IsNullOrWhiteSpace(battleEntryId) || battleEntryId.Length > 256)
            return [];

        var at = now ?? DateTimeOffset.UtcNow;
        var result = store.MutateBadges(state.PlayerId, state,
            new BadgeLedgerKey(BadgeLedgerKinds.Battle, battleEntryId, at.ToUnixTimeSeconds()),
            current => TryApplyVerifiedBattle(current, battleEntryId, out var awarded, rankedBattleRuleType, rankAfter, stats, at)
                ? awarded
                : null);
        if (result.Snapshot is not null) CopyProgress(result.Snapshot, state);
        return result.Awarded;
    }

    public bool TryApplyVerifiedBattle(
        SessionState state,
        string battleEntryId,
        out IReadOnlyList<int> newlyAwarded,
        int? rankedBattleRuleType = null,
        int? rankAfter = null,
        BadgeBattleStatReport? stats = null,
        DateTimeOffset? now = null)
    {
        newlyAwarded = [];
        if (string.IsNullOrWhiteSpace(battleEntryId) || battleEntryId.Length > 256) return false;

        EnsureCollections(state);
        if (!state.UserHonorIds.Contains(DefaultHonorId)) state.UserHonorIds.Add(DefaultHonorId);
        if (!state.ProcessedBadgeBattleEntryIds.Add(battleEntryId)) return false;

        state.VerifiedBattleCount = Math.Max(0, state.VerifiedBattleCount) + 1;
        var awarded = new List<int>();
        foreach (var rule in _battleCountRules)
        {
            if (state.VerifiedBattleCount >= rule.Target && !state.UserHonorIds.Contains(rule.HonorId))
            {
                state.UserHonorIds.Add(rule.HonorId);
                awarded.Add(rule.HonorId);
            }
        }
        if (rankedBattleRuleType is >= 1 and <= 3 && rankAfter is >= 0)
        {
            foreach (var rule in _rankRules.Where(rule => rule.BattleRuleType == rankedBattleRuleType.Value))
            {
                if (rankAfter.Value >= rule.Target && !state.UserHonorIds.Contains(rule.HonorId))
                {
                    state.UserHonorIds.Add(rule.HonorId);
                    awarded.Add(rule.HonorId);
                }
            }
        }

        if (stats is not null) ApplyBattleStats(state, battleEntryId, stats, now ?? DateTimeOffset.UtcNow);
        AwardStatRules(state, awarded);

        newlyAwarded = awarded;
        return true;
    }

    /// <summary>
    /// Credits one validated stat report. Reached only once per entry (the caller already won the processed-entry gate)
    /// and additionally guarded by the bounded credited-battle ledger, so merged stale documents cannot double count.
    /// </summary>
    private static void ApplyBattleStats(SessionState state, string battleEntryId, BadgeBattleStatReport stats, DateTimeOffset now)
    {
        if (!stats.CreditStats) return;
        var counters = state.BadgeStats;
        // Entries are only accepted inside MaxEntryAgeSeconds, so a record older than that can never be replayed.
        if (!BadgeStatState.TryRecord(counters.CreditedBattles, battleEntryId,
                now.ToUnixTimeSeconds(), BadgeStatLimits.CreditedBattleLedgerCapacity, BadgeStatLimits.MaxEntryAgeSeconds + 600))
        {
            stats.CreditStats = false;
            stats.Reason = "ledger_full_or_duplicate";
            return;
        }

        foreach (var (stat, amount) in stats.Stats) counters.AddTotal(stat, amount);
        if (stats.Won is not { } won) return;
        counters.RecordStreak(stats.BattleRuleType, won);
        if (!won) return;
        counters.WinsTotal++;
        BadgeStatState.Increment(counters.ModeWins, stats.BattleRuleType);
        BadgeStatState.Increment(counters.KickerWins, stats.KickerId);
        BadgeStatState.Increment(counters.QueueWins, stats.QueuedAsParty ? "team" : "solo");
    }

    private void AwardStatRules(SessionState state, List<int> awarded)
    {
        var counters = state.BadgeStats;
        foreach (var rule in _statRules)
        {
            if (state.UserHonorIds.Contains(rule.HonorId) || rule.Progress(counters) < rule.Target) continue;
            state.UserHonorIds.Add(rule.HonorId);
            awarded.Add(rule.HonorId);
        }
    }

    /// <summary>
    /// Credits one like to <paramref name="target"/> if the caller proved (against the server-held room roster) that
    /// liker and target were both humans of the same finalized battle. Idempotent per (battle, liker) pair.
    /// </summary>
    public IReadOnlyList<int> RecordVerifiedLike(
        long targetPlayerId, string likerUserId, string battleId, DateTimeOffset now, IPlayerStore store)
    {
        if (targetPlayerId <= 0 || string.IsNullOrWhiteSpace(likerUserId) || string.IsNullOrWhiteSpace(battleId)) return [];

        var key = battleId + "|" + likerUserId;
        // Writes the target's row, not the caller's, in its own transaction; the ledger key stops repeats.
        var result = store.MutateBadges(targetPlayerId, null,
            new BadgeLedgerKey(BadgeLedgerKinds.Like, key, now.ToUnixTimeSeconds()),
            target =>
            {
                EnsureCollections(target);
                if (!BadgeStatState.TryRecord(target.BadgeStats.LikeLedger, key, now.ToUnixTimeSeconds(),
                        BadgeStatLimits.LikeLedgerCapacity, BadgeStatLimits.MaxEntryAgeSeconds + 600))
                    return null;

                target.BadgeStats.AddTotal(BadgeStatNames.LikesReceived, 1);
                var awarded = new List<int>();
                AwardStatRules(target, awarded);
                return awarded;
            });
        return result.Applied ? result.Awarded : [];
    }

    public IReadOnlyList<int> ApplyVerifiedDiscUpgrade(SessionState state)
    {
        EnsureCollections(state);
        state.DiscUpgradeCount = Math.Max(0, state.DiscUpgradeCount) + 1;
        var awarded = new List<int>();
        foreach (var rule in _discUpgradeRules)
        {
            if (state.DiscUpgradeCount >= rule.Target && !state.UserHonorIds.Contains(rule.HonorId))
            {
                state.UserHonorIds.Add(rule.HonorId);
                awarded.Add(rule.HonorId);
            }
        }
        return awarded;
    }

    public IReadOnlyList<int> RecordVerifiedDiscUpgrade(SessionState state, IPlayerStore store)
    {
        if (state.PlayerId <= 0) return [];
        var result = store.MutateBadges(state.PlayerId, state, null, ApplyVerifiedDiscUpgrade);
        if (result.Snapshot is not null) CopyProgress(result.Snapshot, state);
        return result.Awarded;
    }

    public IReadOnlyList<int> ApplyRegistrationComplete(SessionState state)
    {
        EnsureCollections(state);
        var awarded = new List<int>();
        foreach (var rule in _registrationRules)
        {
            if (state.UserHonorIds.Contains(rule.HonorId)) continue;
            state.UserHonorIds.Add(rule.HonorId);
            awarded.Add(rule.HonorId);
        }
        return awarded;
    }

    public IReadOnlyList<int> RecordRegistrationComplete(SessionState state, IPlayerStore store)
    {
        if (state.PlayerId <= 0) return [];
        var result = store.MutateBadges(state.PlayerId, state, null, ApplyRegistrationComplete);
        if (result.Snapshot is not null) CopyProgress(result.Snapshot, state);
        return result.Awarded;
    }

    public IReadOnlyList<int> RecordVerifiedLoginDay(SessionState state, IPlayerStore store, DateTimeOffset now)
    {
        if (state.PlayerId <= 0 || _loginDayRules.Count == 0) return [];

        var day = now.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var result = store.MutateBadges(state.PlayerId, state,
            new BadgeLedgerKey(BadgeLedgerKinds.LoginDay, day, now.ToUnixTimeSeconds()),
            current => TryApplyVerifiedLoginDay(current, now, out var awarded) ? awarded : null);
        if (result.Snapshot is not null) CopyProgress(result.Snapshot, state);
        return result.Applied ? result.Awarded : [];
    }

    public bool TryApplyVerifiedLoginDay(SessionState state, DateTimeOffset now, out IReadOnlyList<int> newlyAwarded)
    {
        newlyAwarded = [];
        if (_loginDayRules.Count == 0) return false;

        EnsureCollections(state);
        var day = now.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (state.VerifiedLoginDays.Contains(day, StringComparer.Ordinal)) return false;

        var maxDays = _loginDayRules.Max(rule => rule.Target);
        if (state.VerifiedLoginDays.Count >= maxDays) return false;
        state.VerifiedLoginDays.Add(day);

        var awarded = new List<int>();
        foreach (var rule in _loginDayRules)
        {
            if (state.VerifiedLoginDays.Count >= rule.Target && !state.UserHonorIds.Contains(rule.HonorId))
            {
                state.UserHonorIds.Add(rule.HonorId);
                awarded.Add(rule.HonorId);
            }
        }

        newlyAwarded = awarded;
        return true;
    }

    private static void EnsureCollections(SessionState state)
    {
        state.UserHonorIds ??= [DefaultHonorId];
        state.ProcessedBadgeBattleEntryIds ??= new HashSet<string>(StringComparer.Ordinal);
        state.ProcessedBadgeBattleAwards ??= new Dictionary<string, List<int>>(StringComparer.Ordinal);
        state.VerifiedLoginDays ??= [];
        state.BadgeStats ??= new BadgeStatState();
        state.BadgeStats.EnsureCollections();
    }

    private static void CopyProgress(SessionState source, SessionState destination)
    {
        destination.HonorId = source.HonorId;
        destination.VerifiedBattleCount = source.VerifiedBattleCount;
        destination.UserHonorIds = [.. source.UserHonorIds];
        destination.ProcessedBadgeBattleEntryIds = new HashSet<string>(source.ProcessedBadgeBattleEntryIds, StringComparer.Ordinal);
        destination.ProcessedBadgeBattleAwards = source.ProcessedBadgeBattleAwards.ToDictionary(
            pair => pair.Key, pair => new List<int>(pair.Value), StringComparer.Ordinal);
        destination.VerifiedLoginDays = [.. source.VerifiedLoginDays];
        destination.DiscUpgradeCount = source.DiscUpgradeCount;
        destination.BadgeStats = source.BadgeStats.Clone();
    }

    private static void MergeProgress(SessionState latest, SessionState destination, bool allowEquippedHonorChange)
    {
        EnsureCollections(latest);
        EnsureCollections(destination);
        destination.UserHonorIds = latest.UserHonorIds.Concat(destination.UserHonorIds).Distinct().ToList();
        destination.VerifiedBattleCount = Math.Max(latest.VerifiedBattleCount, destination.VerifiedBattleCount);
        destination.ProcessedBadgeBattleEntryIds = new HashSet<string>(
            latest.ProcessedBadgeBattleEntryIds.Concat(destination.ProcessedBadgeBattleEntryIds), StringComparer.Ordinal);
        foreach (var (battleEntryId, honorIds) in latest.ProcessedBadgeBattleAwards)
            destination.ProcessedBadgeBattleAwards.TryAdd(battleEntryId, [.. honorIds]);
        destination.VerifiedLoginDays = latest.VerifiedLoginDays.Concat(destination.VerifiedLoginDays)
            .Distinct(StringComparer.Ordinal).ToList();
        destination.DiscUpgradeCount = Math.Max(latest.DiscUpgradeCount, destination.DiscUpgradeCount);
        destination.BadgeStats.MergeFrom(latest.BadgeStats);
        if (!allowEquippedHonorChange) destination.HonorId = latest.HonorId;
    }

    private sealed record BattleCountRule(int HonorId, int Target);
    private sealed record DiscUpgradeRule(int HonorId, int Target);
    private sealed record LoginDayRule(int HonorId, int Target);
    private sealed record RegistrationRule(int HonorId);
    private sealed record RankRule(int HonorId, int BattleRuleType, int Target);

    private enum StatRuleKind { WinsTotal, KickerWins, ModeWins, QueueWins, WinStreak, StatTotal }

    private sealed record StatRule(int HonorId, StatRuleKind Kind, long Target, int Key, string Name, string Trust)
    {
        public long Progress(BadgeStatState stats) => Kind switch
        {
            StatRuleKind.WinsTotal => stats.WinsTotal,
            StatRuleKind.KickerWins => stats.KickerWins.GetValueOrDefault(Key),
            StatRuleKind.ModeWins => stats.ModeWins.GetValueOrDefault(Key),
            StatRuleKind.QueueWins => stats.QueueWins.GetValueOrDefault(Name),
            StatRuleKind.WinStreak => stats.WinStreaks.TryGetValue(Key, out var streak) ? streak.Best : 0,
            StatRuleKind.StatTotal => stats.Total(Name),
            _ => 0
        };
    }
}
