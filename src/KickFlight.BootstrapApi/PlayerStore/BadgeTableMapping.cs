using System.Globalization;
using System.Text.Json;

namespace KickFlight.BootstrapApi.PlayerStore;

// Idempotency ledgers kept per player. The key space of each kind is independent.
public static class BadgeLedgerKinds
{
    public const string Battle = "battle";       // issued battle entry id -> honors it awarded
    public const string Like = "like";           // "<battleId>|<likerUserId>"
    public const string LoginDay = "login_day";  // "yyyy-MM-dd"
}

/// <summary>Identifies one at-most-once badge mutation. NowUnix orders ledger rows.</summary>
public sealed record BadgeLedgerKey(string Kind, string Key, long NowUnix);

/// <summary>
/// Outcome of <see cref="IPlayerStore.MutateBadges"/>. Applied is false when the ledger key was already
/// consumed (Awarded then holds what the first application awarded) or the mutation declined. Snapshot is the
/// player's state after the call (null when the player does not exist).
/// </summary>
public sealed record BadgeMutationResult(bool Applied, IReadOnlyList<int> Awarded, SessionState? Snapshot);

/// <summary>Flat, table-shaped view of the badge fields of a <see cref="SessionState"/>.</summary>
public sealed class BadgeTableSnapshot
{
    public Dictionary<string, long> Counters { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, WinStreakState> Streaks { get; } = new();
    public HashSet<int> Honors { get; } = new();
    // (kind, key) -> (unix seconds, awarded honors)
    public Dictionary<(string Kind, string Key), (long Unix, List<int> Honors)> Ledger { get; } = new();
}

/// <summary>
/// Maps the badge fields of the account document to and from the player_badge_* tables, and implements the
/// document-side ledger lookup the JSON store uses. The tables are the source of truth in PostgreSQL; the jsonb
/// document keeps a mirror for readers that only know the document.
/// </summary>
public static class BadgeTableMapping
{
    // Only the most recent ledger rows are mirrored into the document; the table keeps every one.
    public const int MirroredLedgerRows = 512;

    private const string VerifiedBattles = "verified_battles";
    private const string DiscUpgrades = "disc_upgrades";
    private const string LoginDays = "login_days";
    private const string WinsTotal = "wins_total";

    /// <summary>Names of the top-level document properties that are mirrored from the tables.</summary>
    public static readonly string[] MirroredProperties =
    [
        nameof(SessionState.UserHonorIds), nameof(SessionState.VerifiedBattleCount),
        nameof(SessionState.ProcessedBadgeBattleEntryIds), nameof(SessionState.ProcessedBadgeBattleAwards),
        nameof(SessionState.VerifiedLoginDays), nameof(SessionState.DiscUpgradeCount), nameof(SessionState.BadgeStats)
    ];

    public static BadgeTableSnapshot Flatten(SessionState state)
    {
        var snapshot = new BadgeTableSnapshot();
        state.UserHonorIds ??= [];
        state.ProcessedBadgeBattleEntryIds ??= new HashSet<string>(StringComparer.Ordinal);
        state.ProcessedBadgeBattleAwards ??= new Dictionary<string, List<int>>(StringComparer.Ordinal);
        state.VerifiedLoginDays ??= [];
        state.BadgeStats ??= new BadgeStatState();
        var stats = state.BadgeStats;
        stats.EnsureCollections();

        foreach (var honor in state.UserHonorIds) if (honor > 0) snapshot.Honors.Add(honor);
        var counters = snapshot.Counters;
        Put(counters, VerifiedBattles, state.VerifiedBattleCount);
        Put(counters, DiscUpgrades, state.DiscUpgradeCount);
        Put(counters, LoginDays, state.VerifiedLoginDays.Distinct(StringComparer.Ordinal).Count());
        Put(counters, WinsTotal, stats.WinsTotal);
        foreach (var (stat, value) in stats.Totals) Put(counters, "total:" + stat, value);
        foreach (var (kicker, value) in stats.KickerWins) Put(counters, "kicker_wins:" + kicker.ToString(CultureInfo.InvariantCulture), value);
        foreach (var (mode, value) in stats.ModeWins) Put(counters, "mode_wins:" + mode.ToString(CultureInfo.InvariantCulture), value);
        foreach (var (queue, value) in stats.QueueWins) Put(counters, "queue_wins:" + queue, value);
        foreach (var (mode, streak) in stats.WinStreaks)
            snapshot.Streaks[mode] = new WinStreakState { Battles = streak.Battles, Current = streak.Current, Best = streak.Best };

        foreach (var entry in state.ProcessedBadgeBattleEntryIds)
        {
            var awards = state.ProcessedBadgeBattleAwards.TryGetValue(entry, out var honors) ? [.. honors] : new List<int>();
            var unix = stats.CreditedBattles.TryGetValue(entry, out var credited) ? credited : 0;
            snapshot.Ledger[(BadgeLedgerKinds.Battle, entry)] = (unix, awards);
        }
        foreach (var (key, unix) in stats.LikeLedger) snapshot.Ledger[(BadgeLedgerKinds.Like, key)] = (unix, []);
        foreach (var day in state.VerifiedLoginDays) snapshot.Ledger[(BadgeLedgerKinds.LoginDay, day)] = (0, []);
        return snapshot;
    }

    private static void Put(Dictionary<string, long> counters, string key, long value)
    {
        if (value > 0) counters[key] = value;
    }

    /// <summary>
    /// Folds table rows into <paramref name="state"/> without ever lowering anything (counters by maximum, honors and
    /// login days by union, streaks by the monotonic battle sequence). With <paramref name="replaceLedgers"/> the
    /// mirrored ledgers become exactly the supplied recent rows (they are a bounded view of the table).
    /// </summary>
    public static void ApplyTo(SessionState state, BadgeTableSnapshot tables, bool replaceLedgers)
    {
        Flatten(state); // normalises null collections
        var stats = state.BadgeStats;

        var latest = new BadgeStatState();
        long Counter(string key) => tables.Counters.GetValueOrDefault(key);
        latest.WinsTotal = Counter(WinsTotal);
        foreach (var (key, value) in tables.Counters)
        {
            var separator = key.IndexOf(':');
            if (separator < 0) continue;
            var name = key[(separator + 1)..];
            switch (key[..separator])
            {
                case "total": latest.Totals[name] = value; break;
                case "kicker_wins" when int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kicker):
                    latest.KickerWins[kicker] = value; break;
                case "mode_wins" when int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode):
                    latest.ModeWins[mode] = value; break;
                case "queue_wins": latest.QueueWins[name] = value; break;
            }
        }
        foreach (var (mode, streak) in tables.Streaks)
            latest.WinStreaks[mode] = new WinStreakState { Battles = streak.Battles, Current = streak.Current, Best = streak.Best };

        var mirroredEntries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ((kind, key), (unix, honors)) in tables.Ledger)
        {
            switch (kind)
            {
                case BadgeLedgerKinds.Like: latest.LikeLedger[key] = unix; break;
                case BadgeLedgerKinds.Battle: mirroredEntries.Add(key); break;
            }
        }

        stats.MergeFrom(latest);
        if (replaceLedgers) stats.LikeLedger = new Dictionary<string, long>(latest.LikeLedger, StringComparer.Ordinal);

        state.VerifiedBattleCount = (int)Math.Min(int.MaxValue, Math.Max(state.VerifiedBattleCount, Counter(VerifiedBattles)));
        state.DiscUpgradeCount = (int)Math.Min(int.MaxValue, Math.Max(state.DiscUpgradeCount, Counter(DiscUpgrades)));
        state.UserHonorIds = state.UserHonorIds.Concat(tables.Honors).Append(BadgeAwardRules.DefaultHonorId).Distinct().Order().ToList();

        var days = new HashSet<string>(state.VerifiedLoginDays, StringComparer.Ordinal);
        var battleAwards = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var ((kind, key), (_, honors)) in tables.Ledger)
        {
            if (kind == BadgeLedgerKinds.LoginDay) days.Add(key);
            else if (kind == BadgeLedgerKinds.Battle) battleAwards[key] = honors;
        }
        state.VerifiedLoginDays = days.Order(StringComparer.Ordinal).ToList();

        if (replaceLedgers)
        {
            state.ProcessedBadgeBattleEntryIds = mirroredEntries;
            state.ProcessedBadgeBattleAwards = battleAwards;
        }
        else
        {
            foreach (var entry in mirroredEntries) state.ProcessedBadgeBattleEntryIds.Add(entry);
            foreach (var (entry, honors) in battleAwards) state.ProcessedBadgeBattleAwards.TryAdd(entry, honors);
        }
    }

    /// <summary>Document-side ledger lookup (JSON store). Returns the awards recorded by the first application.</summary>
    public static bool TryGetLedger(SessionState state, BadgeLedgerKey ledger, out IReadOnlyList<int> awards)
    {
        awards = [];
        state.ProcessedBadgeBattleEntryIds ??= new HashSet<string>(StringComparer.Ordinal);
        state.ProcessedBadgeBattleAwards ??= new Dictionary<string, List<int>>(StringComparer.Ordinal);
        state.VerifiedLoginDays ??= [];
        state.BadgeStats ??= new BadgeStatState();
        state.BadgeStats.EnsureCollections();
        switch (ledger.Kind)
        {
            case BadgeLedgerKinds.Battle:
                if (!state.ProcessedBadgeBattleEntryIds.Contains(ledger.Key)) return false;
                awards = state.ProcessedBadgeBattleAwards.TryGetValue(ledger.Key, out var honors) ? honors : [];
                return true;
            case BadgeLedgerKinds.Like:
                return state.BadgeStats.LikeLedger.ContainsKey(ledger.Key);
            case BadgeLedgerKinds.LoginDay:
                return state.VerifiedLoginDays.Contains(ledger.Key, StringComparer.Ordinal);
            default:
                return false;
        }
    }

    /// <summary>Stores a battle's awards next to its entry id so a retry returns the same list.</summary>
    public static void RecordLedgerAwards(SessionState state, BadgeLedgerKey ledger, IReadOnlyList<int> awards)
    {
        if (ledger.Kind != BadgeLedgerKinds.Battle) return;
        state.ProcessedBadgeBattleEntryIds.Add(ledger.Key);
        state.ProcessedBadgeBattleAwards[ledger.Key] = [.. awards];
    }

    public static string EncodePayload(IReadOnlyList<int> honors) =>
        JsonSerializer.Serialize(new { honors });

    public static List<int> DecodePayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return [];
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("honors", out var honors)
                || honors.ValueKind != JsonValueKind.Array)
                return [];
            var result = new List<int>();
            foreach (var honor in honors.EnumerateArray())
                if (honor.TryGetInt32(out var id)) result.Add(id);
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
