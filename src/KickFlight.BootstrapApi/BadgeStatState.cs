namespace KickFlight.BootstrapApi;

/// <summary>
/// Lifetime battle counters behind the stat-based badges. It lives inside <see cref="SessionState"/> and therefore
/// inside the single account document of either store (JSON file or the PostgreSQL <c>state jsonb</c> column).
///
/// Merge contract (see <see cref="MergeFrom"/>): a whole-document save from a stale session must never lower a
/// counter or forget a processed entry. Every scalar/dictionary counter therefore merges as a per-key maximum,
/// every ledger as a union, and the win streak (the only value that legitimately goes down) is merged by the
/// monotonic <see cref="WinStreakState.Battles"/> sequence.
/// </summary>
public sealed class BadgeStatState
{
    // Stat name (BadgeStatNames) -> lifetime total: kills, deaths, mvp, crystals_*, flag_*, likes_received.
    public Dictionary<string, long> Totals { get; set; } = new(StringComparer.Ordinal);
    public long WinsTotal { get; set; }
    // kickerId -> wins with that kicker (the kicker the server recorded when it issued the entry).
    public Dictionary<int, long> KickerWins { get; set; } = new();
    // battleRuleType (1 Scramble, 2 Flag Flight, 3 Rapid Ball) -> wins.
    public Dictionary<int, long> ModeWins { get; set; } = new();
    // "solo" | "team" (entry queued without / with a team lobby) -> wins.
    public Dictionary<string, long> QueueWins { get; set; } = new(StringComparer.Ordinal);
    // battleRuleType -> consecutive-win state.
    public Dictionary<int, WinStreakState> WinStreaks { get; set; } = new();
    // Bounded ledger of battle entries whose client-reported stats were already credited: entryId -> unix seconds.
    public Dictionary<string, long> CreditedBattles { get; set; } = new(StringComparer.Ordinal);
    // Bounded ledger of likes already credited to this account: "<battleId>|<likerUserId>" -> unix seconds.
    public Dictionary<string, long> LikeLedger { get; set; } = new(StringComparer.Ordinal);

    public void EnsureCollections()
    {
        Totals ??= new(StringComparer.Ordinal);
        KickerWins ??= new();
        ModeWins ??= new();
        QueueWins ??= new(StringComparer.Ordinal);
        WinStreaks ??= new();
        CreditedBattles ??= new(StringComparer.Ordinal);
        LikeLedger ??= new(StringComparer.Ordinal);
    }

    public long Total(string stat) => Totals.TryGetValue(stat, out var value) ? value : 0;

    public void AddTotal(string stat, long amount)
    {
        if (amount <= 0) return;
        Totals[stat] = Total(stat) + amount;
    }

    public static void Increment<TKey>(Dictionary<TKey, long> counters, TKey key) where TKey : notnull =>
        counters[key] = (counters.TryGetValue(key, out var value) ? value : 0) + 1;

    public void RecordStreak(int battleRuleType, bool won)
    {
        var streak = WinStreaks.TryGetValue(battleRuleType, out var existing) ? existing : new WinStreakState();
        streak.Battles++;
        streak.Current = won ? streak.Current + 1 : 0;
        streak.Best = Math.Max(streak.Best, streak.Current);
        WinStreaks[battleRuleType] = streak;
    }

    /// <summary>
    /// Adds <paramref name="key"/> to a bounded ledger. When full, the oldest record is evicted only if it is already
    /// older than <paramref name="retentionSeconds"/>: an entry still inside the acceptance window must stay
    /// remembered, so the call refuses instead (returns false) and the caller drops the credit.
    /// </summary>
    public static bool TryRecord(
        Dictionary<string, long> ledger, string key, long nowUnix, int capacity, int retentionSeconds)
    {
        if (ledger.ContainsKey(key)) return false;
        while (ledger.Count >= capacity)
        {
            var oldest = ledger.MinBy(pair => pair.Value);
            if (nowUnix - oldest.Value <= retentionSeconds) return false;
            ledger.Remove(oldest.Key);
        }
        ledger[key] = nowUnix;
        return true;
    }

    public BadgeStatState Clone()
    {
        var copy = new BadgeStatState { WinsTotal = WinsTotal };
        EnsureCollections();
        foreach (var (key, value) in Totals) copy.Totals[key] = value;
        foreach (var (key, value) in KickerWins) copy.KickerWins[key] = value;
        foreach (var (key, value) in ModeWins) copy.ModeWins[key] = value;
        foreach (var (key, value) in QueueWins) copy.QueueWins[key] = value;
        foreach (var (key, value) in WinStreaks)
            copy.WinStreaks[key] = new WinStreakState { Battles = value.Battles, Current = value.Current, Best = value.Best };
        foreach (var (key, value) in CreditedBattles) copy.CreditedBattles[key] = value;
        foreach (var (key, value) in LikeLedger) copy.LikeLedger[key] = value;
        return copy;
    }

    /// <summary>Folds the freshest stored copy into this (possibly stale) document without ever decreasing anything.</summary>
    public void MergeFrom(BadgeStatState latest)
    {
        EnsureCollections();
        latest.EnsureCollections();
        WinsTotal = Math.Max(WinsTotal, latest.WinsTotal);
        MaxMerge(Totals, latest.Totals);
        MaxMerge(KickerWins, latest.KickerWins);
        MaxMerge(ModeWins, latest.ModeWins);
        MaxMerge(QueueWins, latest.QueueWins);
        MaxMerge(CreditedBattles, latest.CreditedBattles);
        MaxMerge(LikeLedger, latest.LikeLedger);
        foreach (var (mode, theirs) in latest.WinStreaks)
        {
            if (!WinStreaks.TryGetValue(mode, out var mine))
            {
                WinStreaks[mode] = new WinStreakState { Battles = theirs.Battles, Current = theirs.Current, Best = theirs.Best };
                continue;
            }
            // The copy that has seen more battles of this mode holds the live streak; the best is a pure maximum.
            if (theirs.Battles > mine.Battles)
            {
                mine.Battles = theirs.Battles;
                mine.Current = theirs.Current;
            }
            mine.Best = Math.Max(mine.Best, theirs.Best);
        }
    }

    private static void MaxMerge<TKey>(Dictionary<TKey, long> destination, Dictionary<TKey, long> source) where TKey : notnull
    {
        foreach (var (key, value) in source)
            destination[key] = destination.TryGetValue(key, out var mine) ? Math.Max(mine, value) : value;
    }
}

public sealed class WinStreakState
{
    // Battles of this mode with a decided outcome counted so far (monotonic; orders two copies of the document).
    public long Battles { get; set; }
    public int Current { get; set; }
    public int Best { get; set; }
}
