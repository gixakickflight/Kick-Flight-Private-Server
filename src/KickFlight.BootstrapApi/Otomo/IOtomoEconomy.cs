using System.Text.Json;
using System.Text.Json.Nodes;
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

// The state a game hangs on the item rather than on the player (economy's internal/attrs, a jsonb column on the
// possession row). The schema is closed - level, counter, available_at - and 0 means "the game has set nothing",
// which is what every row that predates it reads as.
//
// A disc's level lives here: it is this record's Level, not a field of the adapter's save.
public sealed record EconomyItemAttrs(
    [property: JsonPropertyName("level")] long Level,
    [property: JsonPropertyName("counter")] long Counter,
    [property: JsonPropertyName("available_at")] DateTimeOffset? AvailableAt);

// One row of GET /api/player/economy/inventory. `stackable` comes from the rules as they are now, `qty` is what
// the player actually holds, and `attrs` is the item's own state (defaulted so a response from a service that
// predates the column still deserializes).
public sealed record EconomyItem(
    [property: JsonPropertyName("item")] string Item,
    [property: JsonPropertyName("qty")] long Qty,
    [property: JsonPropertyName("stackable")] bool Stackable,
    [property: JsonPropertyName("acquired_at")] DateTimeOffset? AcquiredAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt,
    [property: JsonPropertyName("attrs")] EconomyItemAttrs? Attrs = null);

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

// One line of the currency journal a mutating call wrote. Every movement is a pair (the player's side and a system
// account's); only the player's side is what the player received or spent, so a caller filters on OwnerKind.
public sealed record EconomyEntry(
    [property: JsonPropertyName("owner_kind")] string? OwnerKind,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("delta")] long Delta);

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
    [property: JsonPropertyName("replayed")] bool Replayed,
    [property: JsonPropertyName("entries")] IReadOnlyList<EconomyEntry>? Entries = null);

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
    [property: JsonPropertyName("repetition")] int Repetition,
    // The high-water mark a step is held to, and how the counter moves ("cumulative" or "gauge"). For a gauge `Value`
    // is where the streak is now and `Best` the peak; a service that predates them answers neither, and both default.
    [property: JsonPropertyName("best")] long Best = 0,
    [property: JsonPropertyName("mode")] string? Mode = null);

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
    [property: JsonPropertyName("replayed")] bool Replayed,
    [property: JsonPropertyName("entries")] IReadOnlyList<EconomyEntry>? Entries = null);

// POST /internal/economy/grants: a configured class paid to one player under the caller's key. The amounts are the
// class's, never the caller's; a replayed key answers the first call's result with Replayed set.
public sealed record EconomyGrantResult(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("class")] string? Class,
    [property: JsonPropertyName("balances")] IReadOnlyList<EconomyBalance>? Balances,
    [property: JsonPropertyName("entries")] IReadOnlyList<EconomyEntry>? Entries,
    [property: JsonPropertyName("items")] IReadOnlyList<EconomyItemMove>? Items,
    [property: JsonPropertyName("duplicates")] IReadOnlyList<EconomyItemMove>? Duplicates,
    [property: JsonPropertyName("replayed")] bool Replayed);

// One grant to make: which class, to whom, under which key. Like a progress report, the key names the fact (the
// battle, the tutorial step) rather than the moment, so a retry pays once.
public sealed record EconomyGrantRequest(string PlayerId, string Class, string Key, string Reason, string Reference);

// POST /internal/economy/consume: what a sink took from one player under the caller's key, in one transaction - the
// items and the currencies leave together or not at all. `items` carries what each item totals afterwards (an item
// at zero is gone from the inventory), `balances` what each currency stands at. A replayed key answers the first
// call's result and moves nothing.
public sealed record EconomyConsumeResult(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("items")] IReadOnlyList<EconomyItemMove>? Items,
    [property: JsonPropertyName("balances")] IReadOnlyList<EconomyBalance>? Balances,
    [property: JsonPropertyName("replayed")] bool Replayed = false);

public sealed record EconomyItemCost(string Item, long Qty);

public sealed record EconomyCurrencyCost(string Currency, long Amount);

// One consume to make. Unlike a grant, the amounts are the caller's: a sink has no class to read them from, which is
// why only the game-server key may call it. The key names the fact (this disc reaching this level), so a retry of
// the same decision spends once.
public sealed record EconomyConsumeRequest(string PlayerId, string Key, string Reason, string Reference,
    IReadOnlyList<EconomyItemCost> Items, IReadOnlyList<EconomyCurrencyCost> Currencies);

// One track's counter value after a progress report.
// POST /internal/economy/items/advance: one value of one possession moved by a positive amount, up to a ceiling.
// The answer is the item's state afterwards, which is what a caller that has already taken payment reports as the
// fact it bought.
public sealed record EconomyAdvanceResult(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("item")] string Item,
    [property: JsonPropertyName("attrs")] EconomyItemAttrs? Attrs,
    [property: JsonPropertyName("value")] long Value,
    [property: JsonPropertyName("replayed")] bool Replayed);

// One advance to make. `Max` 0 means no ceiling; a disc passes its rarity's top level so the advance cannot run
// past the build-up table even if a caller asked for more.
public sealed record EconomyAdvanceRequest(string PlayerId, string ItemId, string Key, string Reason,
    string Reference, string Field, long By, long Max);

public sealed record EconomyTrackProgress(
    [property: JsonPropertyName("track_id")] string TrackId,
    [property: JsonPropertyName("period")] string? Period,
    [property: JsonPropertyName("value")] long Value,
    [property: JsonPropertyName("best")] long Best = 0);

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

// POST /internal/economy/facts: a declared fact and its typed fields. Economy's tracks decide which reports count and
// what each adds; this carries no amount and no reward. `Fields` are the JSON values of the fact's declared fields -
// the schema is closed both ways, so every declared field is present and no other.
public sealed record EconomyFactReport(string PlayerId, string Fact, IReadOnlyDictionary<string, object?> Fields,
    string Key, string Reason, string Reference);

public sealed record EconomyFact(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("fact")] string Fact,
    [property: JsonPropertyName("tracks")] IReadOnlyList<EconomyTrackProgress>? Tracks,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("replayed")] bool Replayed);

// POST /internal/economy/progress/gauge: the present value of a counter that is set rather than added to (a streak).
public sealed record EconomyGaugeReport(string PlayerId, string Counter, long Value, string Key, string Reason,
    string Reference);

// One row of economy's entitlement table (internal/entitlement): a reward owed to a player, claimable once and not before
// `available_at`, paid through the reward class `rule` when it is claimed. `meta` is the caller's own document, handed
// back untouched. A row the player has claimed carries `claimed_at`.
public sealed record EconomyEntitlement(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("rule")] string Rule,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("available_at")] DateTimeOffset AvailableAt,
    [property: JsonPropertyName("claimed_at")] DateTimeOffset? ClaimedAt,
    [property: JsonPropertyName("meta")] JsonElement? Meta,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt = null);

// GET /internal/economy/entitlements: `{player_id, entitlements: [...]}`, newest first.
public sealed record EconomyEntitlements(
    [property: JsonPropertyName("player_id")] string? PlayerId,
    [property: JsonPropertyName("entitlements")] IReadOnlyList<EconomyEntitlement>? Entitlements);

// POST /internal/economy/entitlements/{id}/claim. `applied` is false when the payout had already been made (a claim that
// raced, or one that crashed after paying): the class is paid once per entitlement whatever the caller's key.
public sealed record EconomyEntitlementClaim(
    [property: JsonPropertyName("applied")] bool Applied,
    [property: JsonPropertyName("balances")] IReadOnlyList<EconomyBalance>? Balances);

// One entitlement to record. `Rule` is a reward class (never an amount), `Kind` one the deployment declares, `Key` the
// caller's name for this create: the same key for the same player answers the row it already made.
public sealed record EconomyEntitlementRequest(string PlayerId, string Kind, string Rule, string Source,
    DateTimeOffset AvailableAt, JsonNode? Meta, string Key);

// A player's economy state as one request needs it. It is assembled per request from the routes that request
// actually uses and is never kept: the adapter holds no value, not even in a cache.
public sealed record EconomySnapshot(
    IReadOnlyList<EconomyBalance> Balances,
    IReadOnlyList<EconomyItem> Items,
    IReadOnlyList<EconomyOffer> Offers,
    IReadOnlyList<EconomyPool> Pools,
    IReadOnlyList<EconomyTrack>? Tracks = null)
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
// The exceptions are ReportProgressAsync, GrantAsync and ConsumeAsync, which are the internal plane by design: they
// name a player and take the game-server key, which is why they are optional and off by default
// (OtomoEconomyOptions.ProgressEnabled is the one switch for all three).
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

    // Reports one declared fact on the internal plane (POST /internal/economy/facts). Keyed, so a retry counts once.
    // Same switch and key as the progress report; Disabled when either is missing. Refused 400 unknown_fact /
    // unknown_fact_field / missing_fact_field / invalid_fact_field for a report the tenant's documents do not declare.
    Task<EconomyFact> ReportFactAsync(EconomyFactReport report, CancellationToken cancellationToken);

    // Sets a gauge counter's present value (POST /internal/economy/progress/gauge). The answer carries each listening
    // track's value and its high-water mark. Same switch and key as the progress report.
    Task<EconomyProgress> ReportGaugeAsync(EconomyGaugeReport report, CancellationToken cancellationToken);

    // Pays one configured class on the internal plane: a battle's capsule token, a tutorial step's disc or skin.
    // Same switch and key as the progress report; Disabled when either is missing.
    Task<EconomyGrantResult> GrantAsync(EconomyGrantRequest grant, CancellationToken cancellationToken);

    // Takes items and currencies from one player on the internal plane, atomically: a disc level-up spending its
    // duplicates and its Disc Force. Same switch and key as the grant. Refused (409 insufficient_items or
    // insufficient_funds) when the player does not hold enough, and then nothing has moved.
    Task<EconomyConsumeResult> ConsumeAsync(EconomyConsumeRequest consume, CancellationToken cancellationToken);

    // Moves one attrs value of one possession: a disc's level, once the consume above has taken payment for it. Same
    // switch, key and plane as the consume. The answer carries the item's state afterwards, which is the fact a badge
    // is recorded from - the level moved, and economy is the only thing that knows it did.
    Task<EconomyAdvanceResult> AdvanceAsync(EconomyAdvanceRequest advance, CancellationToken cancellationToken);

    // Records a reward owed to a player, for later (POST /internal/economy/entitlements): a battle capsule is one. Same
    // switch, key and plane as the grant. Refused 422 kind_unknown / class_unknown for a kind or rule the deployment does
    // not declare, 409 entitlement_cap when the player already holds the kind's max_open, and 409 idempotency_key_reused
    // when the key already made a row with another kind or rule.
    Task<EconomyEntitlement> CreateEntitlementAsync(EconomyEntitlementRequest request, CancellationToken cancellationToken);

    // One player's entitlements, newest first (GET /internal/economy/entitlements). Unclaimed rows only unless
    // `includeClaimed`.
    Task<IReadOnlyList<EconomyEntitlement>> ListEntitlementsAsync(string playerId, bool includeClaimed,
        CancellationToken cancellationToken);

    // Pays the row's reward class, once (POST /internal/economy/entitlements/{id}/claim). Refused 409 entitlement_not_ready
    // before `available_at`, 409 entitlement_claimed when the row was already claimed, 404 entitlement_not_found.
    Task<EconomyEntitlementClaim> ClaimEntitlementAsync(string entitlementId, string playerId, string key,
        CancellationToken cancellationToken);
}
