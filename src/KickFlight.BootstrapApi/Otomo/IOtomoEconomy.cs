using System.Text.Json.Serialization;

namespace KickFlight.BootstrapApi.Otomo;

// Why an economy call failed. The adapter turns this into the game's error contract, so the distinction has to
// survive all the way out, exactly as it does for identity:
//
//   Disabled     Otomo:Economy is off: there is no economy to ask, and no value may be invented instead.
//   Unavailable  the service could not be reached, timed out, or answered 5xx (and answered 5xx again). The
//                player's state is unknown, not refused, so the caller answers a 503 for everyone.
//   Refused      the service answered a 4xx: this call was not allowed. StatusCode and Code are what it said, so
//                a caller can tell "you cannot afford this" (409 insufficient_funds) from "this pool does not
//                exist" (404 unknown_pool) without parsing a message.
public enum OtomoEconomyFailure
{
    Disabled,
    Unavailable,
    Refused
}

public sealed class OtomoEconomyException : Exception
{
    public OtomoEconomyException(OtomoEconomyFailure failure, string message, int statusCode = 0,
        string? code = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        StatusCode = statusCode;
        Code = code;
    }

    public OtomoEconomyFailure Failure { get; }

    // The HTTP status economy answered, or 0 when no answer arrived.
    public int StatusCode { get; }

    // The COM-5 error code economy answered (`insufficient_funds`, `unknown_offer`, ...), when it sent one.
    public string? Code { get; }
}

// --- the wire types ---------------------------------------------------------------------------------------------
//
// These are economy's handlers' own bodies (services/economy/internal/api and internal/ledger), not a local
// invention: the names below are the JSON keys those handlers write. They are the contract for every byte this
// client sends and reads, so nothing here renames a field for convenience.

public sealed record EconomyBalance(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("balance")] long Balance);

// GET /api/player/economy/wallet. `balances` is an array so its order is the currency order, not a JSON object's.
public sealed record EconomyWallet(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("balances")] IReadOnlyList<EconomyBalance>? Balances);

// One row of GET /api/player/economy/inventory. `stackable` comes from the rules as they are now, `qty` is what
// the player actually holds.
public sealed record EconomyItem(
    [property: JsonPropertyName("item")] string Item,
    [property: JsonPropertyName("qty")] long Qty,
    [property: JsonPropertyName("stackable")] bool Stackable,
    [property: JsonPropertyName("acquired_at")] DateTimeOffset? AcquiredAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt);

public sealed record EconomyInventory(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("items")] IReadOnlyList<EconomyItem>? Items);

// One amount a class, an offer or a pool moves: a currency, an item, or (in a cost) either. `amount` is minor
// units for a currency and a count for an item.
public sealed record EconomyGrant(
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("item")] string? Item,
    [property: JsonPropertyName("amount")] long Amount);

public sealed record EconomyWindow(
    [property: JsonPropertyName("start")] string? Start,
    [property: JsonPropertyName("end")] string? End);

public sealed record EconomyLimit(
    [property: JsonPropertyName("count")] long Count,
    [property: JsonPropertyName("window")] string? Window);

// One offer on the shelf as the client sees it: its price and its grants, never the rules behind them.
public sealed record EconomyOffer(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("cost")] IReadOnlyList<EconomyGrant>? Cost,
    [property: JsonPropertyName("grants")] IReadOnlyList<EconomyGrant>? Grants,
    [property: JsonPropertyName("limit")] EconomyLimit? Limit,
    [property: JsonPropertyName("window")] EconomyWindow? Window,
    [property: JsonPropertyName("available")] bool Available);

public sealed record EconomyShop(
    [property: JsonPropertyName("release")] string? Release,
    [property: JsonPropertyName("offers")] IReadOnlyList<EconomyOffer>? Offers);

// One thing a pool can drop: an item, a relative weight and the rarity label pity is expressed in.
public sealed record EconomyPoolEntry(
    [property: JsonPropertyName("item")] string Item,
    [property: JsonPropertyName("weight")] long Weight,
    [property: JsonPropertyName("rarity")] string? Rarity);

public sealed record EconomyPity(
    [property: JsonPropertyName("after")] long After,
    [property: JsonPropertyName("rarity")] string? Rarity);

public sealed record EconomyPool(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("cost")] IReadOnlyList<EconomyGrant>? Cost,
    [property: JsonPropertyName("entries")] IReadOnlyList<EconomyPoolEntry>? Entries,
    [property: JsonPropertyName("total_weight")] long TotalWeight,
    [property: JsonPropertyName("pity")] EconomyPity? Pity,
    [property: JsonPropertyName("pity_misses")] long PityMisses,
    [property: JsonPropertyName("window")] EconomyWindow? Window,
    [property: JsonPropertyName("open")] bool Open);

public sealed record EconomyPools(
    [property: JsonPropertyName("release")] string? Release,
    [property: JsonPropertyName("pools")] IReadOnlyList<EconomyPool>? Pools);

// One movement in the item journal: what moved and what the player holds afterwards.
public sealed record EconomyItemMove(
    [property: JsonPropertyName("item")] string Item,
    [property: JsonPropertyName("qty")] long Qty,
    [property: JsonPropertyName("total")] long Total);

// One draw's outcome. Duplicate and the conversion to currency are economy's decision, not the adapter's.
public sealed record EconomyDraw(
    [property: JsonPropertyName("item")] string Item,
    [property: JsonPropertyName("rarity")] string? Rarity,
    [property: JsonPropertyName("duplicate")] bool Duplicate,
    [property: JsonPropertyName("paid_currency")] string? PaidCurrency,
    [property: JsonPropertyName("paid_amount")] long PaidAmount,
    [property: JsonPropertyName("from_pity")] bool FromPity);

// POST /api/player/economy/shop/purchase.
public sealed record EconomyPurchase(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("offer_id")] string OfferId,
    [property: JsonPropertyName("quantity")] long Quantity,
    [property: JsonPropertyName("balances")] IReadOnlyList<EconomyBalance>? Balances,
    [property: JsonPropertyName("items")] IReadOnlyList<EconomyItemMove>? Items,
    [property: JsonPropertyName("duplicates")] IReadOnlyList<EconomyItemMove>? Duplicates,
    [property: JsonPropertyName("replayed")] bool Replayed);

// POST /api/player/economy/gacha/roll.
public sealed record EconomyRoll(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("pool_id")] string PoolId,
    [property: JsonPropertyName("release")] string? Release,
    [property: JsonPropertyName("count")] long Count,
    [property: JsonPropertyName("draws")] IReadOnlyList<EconomyDraw>? Draws,
    [property: JsonPropertyName("balances")] IReadOnlyList<EconomyBalance>? Balances,
    [property: JsonPropertyName("items")] IReadOnlyList<EconomyItemMove>? Items,
    [property: JsonPropertyName("pity")] long Pity,
    [property: JsonPropertyName("replayed")] bool Replayed);

// One rung of a track as the player route reports it.
public sealed record EconomyStep(
    [property: JsonPropertyName("step")] int Step,
    [property: JsonPropertyName("target")] long Target,
    [property: JsonPropertyName("class")] string? Class,
    [property: JsonPropertyName("reached")] bool Reached,
    [property: JsonPropertyName("claimed")] bool Claimed,
    [property: JsonPropertyName("claimable")] bool Claimable);

// One track's progress for one player, in one period.
public sealed record EconomyTrack(
    [property: JsonPropertyName("track_id")] string TrackId,
    [property: JsonPropertyName("counter")] string Counter,
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("value")] long Value,
    [property: JsonPropertyName("repeat")] bool Repeat,
    [property: JsonPropertyName("steps")] IReadOnlyList<EconomyStep>? Steps,
    [property: JsonPropertyName("repetition")] int Repetition);

public sealed record EconomyTasks(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("tracks")] IReadOnlyList<EconomyTrack>? Tracks);

// POST /api/player/economy/tasks/claim: the server decides which rung is next, so a client cannot skip ahead.
public sealed record EconomyClaim(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("track_id")] string TrackId,
    [property: JsonPropertyName("period")] string? Period,
    [property: JsonPropertyName("step")] int Step,
    [property: JsonPropertyName("target")] long Target,
    [property: JsonPropertyName("class")] string? Class,
    [property: JsonPropertyName("balances")] IReadOnlyList<EconomyBalance>? Balances,
    [property: JsonPropertyName("items")] IReadOnlyList<EconomyItemMove>? Items,
    [property: JsonPropertyName("duplicates")] IReadOnlyList<EconomyItemMove>? Duplicates,
    [property: JsonPropertyName("replayed")] bool Replayed);

// One track's counter value after a progress report.
public sealed record EconomyTrackProgress(
    [property: JsonPropertyName("track_id")] string TrackId,
    [property: JsonPropertyName("period")] string? Period,
    [property: JsonPropertyName("value")] long Value);

// POST /internal/economy/progress. A game fact, a counter and a delta: never a reward.
public sealed record EconomyProgress(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("counter")] string Counter,
    [property: JsonPropertyName("delta")] long Delta,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("ref")] string? Ref,
    [property: JsonPropertyName("tracks")] IReadOnlyList<EconomyTrackProgress>? Tracks,
    [property: JsonPropertyName("replayed")] bool Replayed);

// One report to make: who advanced which counter, by how much, under which key. The key is what makes a
// retried report count once, so it names the fact (the match and the counter) rather than the moment.
public sealed record EconomyProgressReport(string PlayerId, string Counter, long Delta, string Key,
    string Reason, string Reference);

// A player's economy state as one request needs it. It is assembled per request from the routes that request
// actually uses and is never kept: the adapter holds no value, not even in a cache.
public sealed record EconomySnapshot(
    IReadOnlyList<EconomyBalance> Balances,
    IReadOnlyList<EconomyItem> Items,
    IReadOnlyList<EconomyOffer> Offers,
    IReadOnlyList<EconomyPool> Pools)
{
    public static EconomySnapshot None { get; } = new([], [], [], []);
}

// The adapter's only view of the economy.
//
// One implementation (OtomoEconomy) talks to otomo's economy service over HTTP with the player's *own* access
// token - the one OtomoIdentity caches per device - and never with a service key on a player route. The player
// id is implied by the token: none of these calls takes one, so there is no argument a caller could change to
// reach another player's wallet.
//
// The one exception is ReportProgressAsync, which is the internal plane by design: it names a player and takes
// the game-server key, which is why it is optional and off by default (OtomoEconomyOptions.ProgressEnabled).
public interface IOtomoEconomy
{
    // False when Otomo:Economy is off. Callers fail closed rather than serve a value they do not have.
    bool Enabled { get; }

    // True when the progress report is configured: the switch is on and a service key is readable.
    bool ProgressEnabled { get; }

    Task<EconomyWallet> GetWalletAsync(string accessToken, CancellationToken cancellationToken);

    Task<EconomyInventory> GetInventoryAsync(string accessToken, CancellationToken cancellationToken);

    Task<EconomyShop> GetShopAsync(string accessToken, CancellationToken cancellationToken);

    Task<EconomyPools> GetPoolsAsync(string accessToken, CancellationToken cancellationToken);

    // Buys one offer. The price comes from the shop document, never from here; idempotencyKey makes a retry of
    // this decision free, and the caller mints a fresh one per accepted request.
    Task<EconomyPurchase> PurchaseAsync(string accessToken, string offerId, long quantity, string idempotencyKey,
        CancellationToken cancellationToken);

    // Draws from a pool. Count is the number of draws; the price is the pool's cost multiplied by it.
    Task<EconomyRoll> RollAsync(string accessToken, string poolId, long count, string idempotencyKey,
        CancellationToken cancellationToken);

    Task<EconomyTasks> GetTasksAsync(string accessToken, CancellationToken cancellationToken);

    Task<EconomyClaim> ClaimAsync(string accessToken, string trackId, string idempotencyKey,
        CancellationToken cancellationToken);

    // Reports one battle fact on the internal plane. Throws OtomoEconomyException(Disabled) when the report is
    // not configured, which a caller treats as "nothing to do", not as a failure.
    Task<EconomyProgress> ReportProgressAsync(EconomyProgressReport report, CancellationToken cancellationToken);
}
