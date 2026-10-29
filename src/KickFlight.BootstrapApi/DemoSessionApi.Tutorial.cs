using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;

namespace KickFlight.BootstrapApi;

// The tutorial ladder: the client's own long tutorial, step by step (see TutorialStatus in DemoSessionApi.cs).
//
//   /tutorial/start             1     the kickers the tutorial can show (TutorialStartResponseData.userKickerList)
//   /tutorial/battleProgress    101+  the scripted battle reports its sub-steps; stored as sent (101-113)
//   /tutorial/battleEnd         113
//   /tutorial/capsuleOpen       202   the tutorial capsule: pays class tutorial.disc
//   /tutorial/discBuildup       203   shows the disc; nothing is spent and no level changes
//   /tutorial/gachaDiscDraw     204   the disc draw: the same tutorial.disc grant, so it shows that disc
//   /tutorial/gachaKickerDraw   205   pays class tutorial.kicker
//   /tutorial/kickerChange      206   switches to the kicker the player picked; the name window is next
//   /tutorial/end               207   the name (HandleTutorialEndAsync)
//
// Each step stores the furthest status reached, never a lower one, so a client that repeats a step after a crash
// cannot move a player backwards. The two reward steps are internal grants keyed per player
// (tutorial.disc:<player>, tutorial.kicker:<player>), so a step repeated, or a second device on the same account,
// pays nothing more: economy answers the first grant again. The disc is paid at the capsule step because the
// build-up step right after it needs a disc to show; the disc draw re-presents that same grant rather than paying
// a second disc, since economy publishes exactly one tutorial disc.
//
// A grant that cannot be made answers like any economy failure - 503 when economy is unreachable or the internal
// plane is off, the error contract when economy refused - and the status does not advance, so the client's retry
// lands on the same step.
public sealed partial class DemoSessionApi
{
    private const string TutorialDiscClass = "tutorial.disc";
    private const string TutorialKickerClass = "tutorial.kicker";

    private async Task<IResult?> HandleTutorialStepAsync(string path, HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        switch (path)
        {
            case "/tutorial/start":
            {
                var economy = await ReadPlayerEconomyAsync(ticket, EconomyRead.Inventory, path, context.RequestAborted);
                if (economy.Snapshot is null) return EconomyUnavailable(context, path, economy.Error);
                AdvanceTutorial(state, TutorialStatusStart);
                NormalizeCostume(state);
                var userKickerList = Payloads(state).BuildUserKickerList(economy.Snapshot, state.KickerId,
                    state.KickerCostumeId);
                return OkJson(context, key, JsonSerializer.Serialize(new { userKickerList }));
            }
            case "/tutorial/battleProgress":
            {
                // TutorialBattleProgressRequestData { tutorialProgressStatus }: only the battle's own range is
                // accepted, so a progress report cannot jump the ladder past the reward steps.
                var reported = await ReadTutorialIntAsync(context, key, "tutorialProgressStatus");
                if (reported is >= 101 and <= 113) AdvanceTutorial(state, reported.Value);
                else _logger.LogWarning("tutorial/battleProgress for user {UserId}: status {Status} ignored",
                    state.UserId, reported);
                return OkJson(context, key, "{}");
            }
            case "/tutorial/battleEnd":
                AdvanceTutorial(state, 113);
                return OkJson(context, key, "{}");
            case "/tutorial/capsuleOpen":
                return await HandleTutorialDiscRewardAsync(path, context, ticket, state, key, draw: false);
            case "/tutorial/discBuildup":
                return await HandleTutorialDiscBuildupAsync(context, ticket, state, key);
            case "/tutorial/gachaDiscDraw":
                return await HandleTutorialDiscRewardAsync(path, context, ticket, state, key, draw: true);
            case "/tutorial/gachaKickerDraw":
                return await HandleTutorialKickerDrawAsync(context, ticket, state, key);
            case "/tutorial/kickerChange":
                return await HandleTutorialKickerChangeAsync(context, state, key);
            default:
                _logger.LogInformation("Unknown tutorial step {Path} for user {UserId}; answering {{}}", path, state.UserId);
                return OkJson(context, key, "{}");
        }
    }

    // Moves the stored status forward only, and saves when it moved.
    private void AdvanceTutorial(SessionState state, int status)
    {
        lock (state)
        {
            if (state.TutorialProgress >= status) return;
            state.TutorialProgress = status;
            SaveUserState(state);
        }
        _logger.LogInformation("Tutorial: user {UserId} reached status {Status}", state.UserId, status);
    }

    private async Task<int?> ReadTutorialIntAsync(HttpContext context, byte[] key, string property)
    {
        try
        {
            var body = await ReadBodyAsync(context.Request);
            if (body.Length == 0) return null;
            using var document = JsonDocument.Parse(D2CCodec.Decode(body, key));
            return TryReadInt(document.RootElement, property, out var value) ? value : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode tutorial step body: {Error}", ex.Message);
            return null;
        }
    }

    // A tutorial reward, paid once per player. Returns the grant, or the answer to send when it could not be made.
    private async Task<(EconomyGrantResult? Grant, IResult? Failure)> GrantTutorialRewardAsync(string path,
        HttpContext context, KfIdentityTicket ticket, byte[] key, string grantClass)
    {
        try
        {
            var grant = await GrantClassAsync(ticket, grantClass, $"{grantClass}:{ticket.OtomoPlayerId}",
                "tutorial", path, context.RequestAborted);
            return grant is null
                ? (null, EconomyUnavailable(context, path, new OtomoEconomyException(OtomoEconomyFailure.Disabled,
                    "the internal plane is off, so the tutorial reward cannot be paid")))
                : (grant, null);
        }
        catch (OtomoEconomyException ex) when (ex.Failure == OtomoEconomyFailure.Refused)
        {
            _logger.LogWarning("{Path}: economy refused {Class}: {Status} {Code}", path, grantClass, ex.StatusCode, ex.Code);
            return (null, StatusError(context, key));
        }
        catch (OtomoEconomyException ex)
        {
            return (null, EconomyUnavailable(context, path, ex));
        }
    }

    // /tutorial/capsuleOpen and /tutorial/gachaDiscDraw: the one tutorial disc, granted at whichever comes first.
    private async Task<IResult?> HandleTutorialDiscRewardAsync(string path, HttpContext context,
        KfIdentityTicket ticket, SessionState state, byte[] key, bool draw)
    {
        var (grant, failure) = await GrantTutorialRewardAsync(path, context, ticket, key, TutorialDiscClass);
        if (failure is not null) return failure;
        AdvanceTutorial(state, draw ? 204 : 202);

        var payloads = Payloads(state);
        int LevelOf(int discId) => StoredDiscLevel(state, discId);
        var moved = grant!.Items ?? [];
        var dropDiscList = moved.Where(move => EconomyCatalog.TryDiscId(move.Item, out _))
            .Select(move =>
            {
                EconomyCatalog.TryDiscId(move.Item, out var discId);
                return (object)new { discId, amount = 1, discForceAmount = 0 };
            }).ToArray();
        var receivedUserDiscList = payloads.BuildReceivedUserDiscList(moved, LevelOf);
        var receivedRewardList = payloads.BuildReceivedRewards(grant.Entries, moved);

        if (!draw)
        {
            return OkJson(context, key, JsonSerializer.Serialize(new
            {
                dropDiscList,
                receivedUserDiscList,
                receivedUserItemList = Array.Empty<object>(),
                userPresentList = Array.Empty<object>(),
                receivedRewardList
            }));
        }

        // The draw screen repeats the counters; they are read back, and an unreadable economy fails closed here
        // rather than drawing a zero wallet under the tutorial's disc.
        var economy = await ReadPlayerEconomyAsync(ticket, EconomyRead.Balances | EconomyRead.Inventory, path,
            context.RequestAborted);
        if (economy.Snapshot is null) return EconomyUnavailable(context, path, economy.Error);
        var gacha = _discGachaById.Values.OrderBy(g => g.Id).FirstOrDefault();
        return OkJson(context, key, JsonSerializer.Serialize(new
        {
            dropDiscList,
            receivedUserDiscList,
            receivedUserItemList = Array.Empty<object>(),
            userItemList = payloads.BuildUserItemList(economy.Snapshot),
            modelId = gacha?.ModelId ?? 0,
            capsuleEffectType = 1,
            userPresentList = Array.Empty<object>(),
            receivedRewardList
        }));
    }

    // /tutorial/discBuildup {discId}: the build-up screen's lesson. It answers the disc as it stands - its stored
    // level and the player's Disc Force - and changes nothing: a level is progression the player pays for, and the
    // paid route is gated until economy can take the cost (HandleDiscBuildupAsync).
    private async Task<IResult?> HandleTutorialDiscBuildupAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        const string path = "/tutorial/discBuildup";
        var discId = await ReadTutorialIntAsync(context, key, "discId") ?? 0;
        var economy = await ReadPlayerEconomyAsync(ticket, EconomyRead.Balances | EconomyRead.Inventory, path,
            context.RequestAborted);
        if (economy.Snapshot is null) return EconomyUnavailable(context, path, economy.Error);
        AdvanceTutorial(state, 203);

        var owned = economy.Snapshot.Items.FirstOrDefault(item =>
            EconomyCatalog.TryDiscId(item.Item, out var ownedDiscId) && ownedDiscId == discId);
        var discForce = economy.Snapshot.Balances.FirstOrDefault(balance =>
            EconomyCatalog.TryItemIdForCurrency(balance.Currency, out var itemId) && itemId == 3)?.Balance ?? 0;
        return OkJson(context, key, JsonSerializer.Serialize(new
        {
            userDisc = new
            {
                discId,
                amount = (int)Math.Clamp(owned?.Qty ?? 0, 0, int.MaxValue),
                level = StoredDiscLevel(state, discId),
                exp = 0,
                registerDatetime = EconomyPayloads.Stamp(owned?.AcquiredAt)
            },
            userItem = new { itemId = 3, amount = (int)Math.Clamp(discForce, 0, int.MaxValue) }
        }));
    }

    // /tutorial/gachaKickerDraw: the tutorial's kicker, class tutorial.kicker.
    private async Task<IResult?> HandleTutorialKickerDrawAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        const string path = "/tutorial/gachaKickerDraw";
        var (grant, failure) = await GrantTutorialRewardAsync(path, context, ticket, key, TutorialKickerClass);
        if (failure is not null) return failure;

        var economy = await ReadPlayerEconomyAsync(ticket, EconomyRead.Balances | EconomyRead.Inventory, path,
            context.RequestAborted);
        if (economy.Snapshot is null) return EconomyUnavailable(context, path, economy.Error);
        AdvanceTutorial(state, 205);

        // The grant's item moves read as draws, so the kicker rows are built the way a real draw's are.
        var draws = (grant!.Items ?? []).Select(move => new EconomyDraw(move.Item, null, false, null, 0, false)).ToList();
        var payloads = Payloads(state);
        return OkJson(context, key, JsonSerializer.Serialize(new
        {
            receivedUserKickerList = payloads.BuildReceivedUserKickerList(draws, economy.Snapshot, state.KickerId,
                state.KickerCostumeId),
            userItemList = payloads.BuildUserItemList(economy.Snapshot),
            userPresentList = Array.Empty<object>(),
            receivedRewardList = payloads.BuildReceivedRewards(grant.Entries, grant.Items)
        }));
    }

    // /tutorial/kickerChange {kickerId, kickerCostumeId}: the kicker the player picked after the draw. A costume that
    // is not the named kicker's is ignored rather than stored, the way /kicker/change normalizes one.
    private async Task<IResult?> HandleTutorialKickerChangeAsync(HttpContext context, SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        try
        {
            using var document = JsonDocument.Parse(D2CCodec.Decode(body, key));
            var root = document.RootElement;
            if (TryReadInt(root, "kickerId", out var kickerId) && TryReadInt(root, "kickerCostumeId", out var costumeId) &&
                _costumeKicker.TryGetValue(costumeId, out var owner) && owner == kickerId)
            {
                lock (state)
                {
                    state.KickerId = kickerId;
                    state.KickerCostumeId = costumeId;
                    SaveUserState(state);
                }
            }
            else
            {
                _logger.LogWarning("tutorial/kickerChange for user {UserId}: no valid kicker/costume pair, selection kept",
                    state.UserId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode tutorial kicker change: {Error}", ex.Message);
        }

        AdvanceTutorial(state, TutorialStatusInputName);
        return OkJson(context, key, "{}");
    }
}
