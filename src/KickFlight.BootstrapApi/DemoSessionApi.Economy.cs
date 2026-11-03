using System.Text;
using KickFlight.BootstrapApi.Otomo;

namespace KickFlight.BootstrapApi;

// The adapter's whole relationship with otomo's economy service, in one place: where a request gets the player's
// values from, and what it answers when it cannot.
//
// There is no cache. A request reads the slices of state it uses, on the player's own token, and builds its payload
// from that answer; nothing is kept between requests, so "what does this player own" has exactly one answer at any
// moment and this process is not a second copy of it. That is the difference between this and the demo economy that
// used to live in SessionState: the balances, the tickets, the disc force and the discs themselves are gone from the
// save, and a save from before this change simply loses them on its next write.
//
// A read that cannot be answered fails closed. The alternative - an empty wallet, an empty disc list - is a lie the
// client would render, and an operator would only hear about it from a player.
public sealed partial class DemoSessionApi
{
    // Which slices of a player's economy state one request needs. Every request asks for what it uses and nothing
    // else: the startup payload needs balances, inventory and the mission tracks, the home payload needs the shop,
    // the pools and the tracks (for the badge), and a purchase receipt needs the balances and inventory again
    // *after* the purchase.
    [Flags]
    private enum EconomyRead
    {
        Balances = 1,
        Inventory = 2,
        Shop = 4,
        Pools = 8,
        Tasks = 16
    }

    private sealed record EconomyLoad(EconomySnapshot? Snapshot, Exception? Error);

    // The payload builders, bound to this request's save and to the served masters. One per request is cheap; the
    // dictionaries it closes over are the boot-time ones.
    private EconomyPayloads Payloads(SessionState state) =>
        new(_costumeKicker, costumeId => GearSlots(state, costumeId), _discIdList, _logger);

    // One round of reads, on the player's own token.
    //
    // The token is the one OtomoIdentity already holds for this device: the identity layer signs a player in and
    // this reuses that session, so there is no second credential and no second login. Its failure is reported as the
    // economy's, because from the caller's point of view the value is what could not be obtained.
    private async Task<EconomyLoad> ReadPlayerEconomyAsync(KfIdentityTicket ticket, EconomyRead reads, string path,
        CancellationToken cancellationToken)
    {
        if (!_economy.Enabled)
        {
            return new EconomyLoad(null, new OtomoEconomyException(OtomoEconomyFailure.Disabled,
                "Otomo:Economy:Enabled is false: this server holds no value and cannot answer what a player owns"));
        }

        try
        {
            var accessToken = (await _identity.GetSessionForDeviceIdAsync(ticket.DeviceId, cancellationToken)).AccessToken;

            IReadOnlyList<EconomyBalance> balances = [];
            IReadOnlyList<EconomyItem> items = [];
            IReadOnlyList<EconomyOffer> offers = [];
            IReadOnlyList<EconomyPool> pools = [];
            IReadOnlyList<EconomyTrack>? tracks = null;

            if (reads.HasFlag(EconomyRead.Balances))
            {
                balances = (await _economy.GetWalletAsync(accessToken, cancellationToken)).Balances ?? [];
            }
            if (reads.HasFlag(EconomyRead.Inventory))
            {
                items = (await _economy.GetInventoryAsync(accessToken, cancellationToken)).Items ?? [];
            }
            if (reads.HasFlag(EconomyRead.Shop))
            {
                offers = (await _economy.GetShopAsync(accessToken, cancellationToken)).Offers ?? [];
            }
            if (reads.HasFlag(EconomyRead.Pools))
            {
                pools = (await _economy.GetPoolsAsync(accessToken, cancellationToken)).Pools ?? [];
            }
            if (reads.HasFlag(EconomyRead.Tasks))
            {
                tracks = (await _economy.GetTasksAsync(accessToken, cancellationToken)).Tracks ?? [];
            }

            return new EconomyLoad(new EconomySnapshot(balances, items, offers, pools, tracks), null);
        }
        catch (Exception exception) when (exception is OtomoIdentityException or OtomoEconomyException)
        {
            _logger.LogWarning("Economy read for {Path} failed: {Error}", path, exception.Message);
            return new EconomyLoad(null, exception);
        }
    }

    // A read that needs a value and cannot get one. The answer is the game's error contract - HTTP 503 with a
    // non-zero x-app-status-code - and never a 500, because the client shows its generic network error for that and
    // an operator gets nothing to act on. Mirrors IdentityUnavailable, which is the same situation one layer up.
    private IResult EconomyUnavailable(HttpContext context, string path, Exception? failure)
    {
        var reason = failure switch
        {
            OtomoIdentityException identity => identity.Failure.ToString(),
            OtomoEconomyException economy => economy.Failure.ToString(),
            _ => OtomoEconomyFailure.Disabled.ToString()
        };
        var detail = failure?.Message ?? "Otomo:Economy:Enabled is false";
        _logger.LogError("Refusing {Path}: economy {Reason} ({Detail})", path, reason, detail);

        context.Response.Headers["x-app-status-code"] = "1";
        return Results.Json(new { error = "economy-unavailable", reason },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // The counters of a purchase whose own answer carried the balances and the item movements but not the rest of
    // the inventory. Used only when the read *after* a successful purchase fails: the value has already moved, so
    // the answer has to describe it rather than fail.
    private static EconomySnapshot SnapshotFromPurchase(EconomyPurchase purchase) =>
        new(purchase.Balances ?? [],
            (purchase.Items ?? []).Select(move => new EconomyItem(move.Item, move.Total, Stackable: false, null, null)).ToList(),
            [], []);

    // The same, for a roll: economy answers the draws, the balances and what each item totals now.
    private static EconomySnapshot SnapshotFromRoll(EconomyRoll roll) =>
        new(roll.Balances ?? [],
            (roll.Items ?? []).Select(move => new EconomyItem(move.Item, move.Total, Stackable: false, null, null)).ToList(),
            [], []);

    // The battle's counters, reported to economy as facts: one play, one win for a ranked win, the kills and the MVP.
    //
    // This is the same trust level as the ranked result this handler already applies - the numbers are the client's
    // word, checked only against an entry this server issued - and it pays nothing. What a counter is worth is a
    // track's step, which the player claims on their own plane; a game server that could pay directly would be a
    // payout route with fewer checks. The key is <matchId>:<counter>, so a retried report counts once.
    private async Task ReportBattleProgressAsync(KfIdentityTicket ticket, string battleEntryId, bool won,
        int killCount, bool mvp, CancellationToken cancellationToken)
    {
        if (!_economy.ProgressEnabled)
        {
            _logger.LogDebug("Battle progress for entry {Entry} not reported: Otomo:Economy:ProgressEnabled is off", battleEntryId);
            return;
        }

        var matchId = ProgressKeyPart(battleEntryId);
        if (matchId.Length == 0)
        {
            _logger.LogWarning("Battle progress for player {PlayerId} not reported: the result carried no battle entry id",
                ticket.OtomoPlayerId);
            return;
        }

        var counters = new List<(string Counter, long Delta)> { ("battle.play", 1) };
        if (killCount > 0) counters.Add(("battle.kill", killCount));
        if (won) counters.Add(("battle.win", 1));
        if (mvp) counters.Add(("battle.mvp", 1));

        foreach (var (counter, delta) in counters)
        {
            try
            {
                await _economy.ReportProgressAsync(
                    new EconomyProgressReport(ticket.OtomoPlayerId, counter, delta, $"{matchId}:{counter}",
                        "battle_result", battleEntryId),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is OtomoEconomyException or HttpRequestException)
            {
                // A counter that did not arrive is a warning, never a broken result screen: the next battle reports
                // its own, and refusing the screen over a mission counter would be worse than a late mission.
                _logger.LogWarning("Battle progress {Counter} for entry {Entry} was not reported: {Error}",
                    counter, battleEntryId, exception.Message);
            }
        }
    }

    // One class paid on the internal plane, keyed by the fact it pays for. Null when the internal plane is off (the
    // caller decides what "nothing to pay with" means for its route); the economy's failure is thrown as it came,
    // so a caller can tell an outage (answer 503) from a refusal (answer the error contract).
    private async Task<EconomyGrantResult?> GrantClassAsync(KfIdentityTicket ticket, string grantClass, string key,
        string reason, string reference, CancellationToken cancellationToken)
    {
        if (!_economy.ProgressEnabled)
        {
            _logger.LogWarning("Class {Class} for player {PlayerId} not paid: the internal plane is off " +
                               "(Otomo:Economy:ProgressEnabled and a gameserver key)", grantClass, ticket.OtomoPlayerId);
            return null;
        }

        var result = await _economy.GrantAsync(
            new EconomyGrantRequest(ticket.OtomoPlayerId, grantClass, ProgressKeyPart(key), reason, ProgressKeyPart(reference)),
            cancellationToken);
        _logger.LogInformation("Paid {Class} to player {PlayerId} under {Key}{Replayed}",
            grantClass, ticket.OtomoPlayerId, key, result.Replayed ? " (replayed)" : "");
        return result;
    }

    // The whole bundle, once per account, before the name step. Order matters only for the log.
    private static readonly string[] StarterClasses =
        ["starter.discs", "starter.jetcoin", StarterDiscForceClass];

    private const string StarterDiscForceClass = "starter.disc_force";

    // What a startup still owes this account, decided from the save and from the Disc Force balance the startup has
    // read. Onboarding pays the whole bundle to an account with no name yet: a client with no discs dereferences an
    // empty deck the moment the tutorial battle starts, and the coins and the force are what its first screens
    // spend. A name ends onboarding, so a named account is owed one class at most - the starter Disc Force its
    // build-up lesson needs - and only while its wallet holds none of it. `discForce` is null when the balance is
    // not known yet (the call made before the read, which pays the bundle or nothing), and an unknown balance never
    // pays the lone class: reading the wallet is what keeps a second startup from paying twice.
    private static IReadOnlyList<string> StarterClassesOwed(SessionState state, long? discForce) =>
        state.StarterDiscForcePaid ? []
            : !state.HasName ? StarterClasses
            : discForce is null or > 0 ? []
            : [StarterDiscForceClass];

    // Pays what StarterClassesOwed names, each class under <class>:<player>, so a retry, a lost save flag or a
    // second device is economy's replay and pays nothing more. A grant that cannot be made is logged and tried
    // again on the next startup; the startup itself still answers, because the wallet read is what it shows.
    // Returns the last grant made, for a caller that has already read the wallet and answers from it.
    private async Task<EconomyGrantResult?> PayStarterAsync(KfIdentityTicket ticket, SessionState state,
        long? discForce, string path, CancellationToken cancellationToken)
    {
        var owed = StarterClassesOwed(state, discForce);
        if (owed.Count == 0) return null;

        EconomyGrantResult? paid = null;
        try
        {
            foreach (var starterClass in owed)
            {
                paid = await GrantClassAsync(ticket, starterClass,
                    $"{starterClass}:{ticket.OtomoPlayerId}", "starter", path, cancellationToken);
                if (paid is null) return null;
            }
        }
        catch (OtomoEconomyException ex)
        {
            _logger.LogWarning("{Path}: {Class} for user {UserId} not paid ({Failure} {Status} {Code}); retried on " +
                "the next startup", path, StarterDiscForceClass, state.UserId, ex.Failure, ex.StatusCode, ex.Code);
            return null;
        }

        lock (state)
        {
            state.StarterDiscForcePaid = true;
            SaveUserState(state);
        }
        return paid;
    }

    // The Disc Force in a wallet that has just been read. A wallet with no disc_force row has never held any - which
    // is exactly the account this pays - so the missing row reads as zero rather than as an error.
    private static long DiscForceBalanceOf(EconomySnapshot snapshot) =>
        snapshot.Balances.FirstOrDefault(balance =>
            string.Equals(balance.Currency, EconomyCatalog.DiscForceCurrency, StringComparison.Ordinal))?.Balance ?? 0;

    // The wallet a grant has just moved, folded into the snapshot the startup read a moment earlier: the counters
    // this payload sends are what the client prices its screens from, so they must already hold what was paid. A
    // replay answers the *first* call's balances, which may be stale by now, so only a fresh grant is folded in and
    // a replayed one leaves the wallet as this startup read it.
    private static EconomySnapshot WithGrantedBalances(EconomySnapshot snapshot, EconomyGrantResult? granted)
    {
        if (granted is not { Replayed: false, Balances: { Count: > 0 } balances }) return snapshot;

        var merged = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var balance in snapshot.Balances) merged[balance.Currency] = balance.Balance;
        foreach (var balance in balances) merged[balance.Currency] = balance.Balance;
        return snapshot with
        {
            Balances = merged.Select(pair => new EconomyBalance(pair.Key, pair.Value)).ToList()
        };
    }

    // The player's own access token, for a player-plane call outside ReadPlayerEconomyAsync (a roll, a claim).
    private async Task<string> PlayerTokenAsync(KfIdentityTicket ticket, CancellationToken cancellationToken) =>
        (await _identity.GetSessionForDeviceIdAsync(ticket.DeviceId, cancellationToken)).AccessToken;

    // A progress key is 1-128 characters of [A-Za-z0-9._:-] on economy's side and a battle entry id is opaque, so it
    // is mapped rather than trusted.
    private static string ProgressKeyPart(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, 128));
        foreach (var character in value)
        {
            if (builder.Length == 128) break;
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-' ? character : '-');
        }
        return builder.ToString();
    }
}
