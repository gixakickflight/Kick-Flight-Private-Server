using System.Text.Json;
using System.Text.Json.Nodes;
using KickFlight.BootstrapApi.Otomo;

namespace KickFlight.BootstrapApi;

// A player's loadout - the decks, the active deck, the gear in each costume's slots and a gear roll waiting to be
// equipped - is not kept here. It is the `ext` of the player's session card (ext.session.player), read on each request that
// needs it and written back through session's internal ext route.
//
//   reads    GET /api/player/session/me with the player's own token: startup, home, the battle entry / team handlers
//            and every kicker list that shows gear. Another player's card (a friend's deck, a search result's name) is
//            GET /internal/session/players/{id}.
//   writes   POST /internal/session/players/ext, merge = true, keyed so a retry writes once: the client's
//            x-app-response-cache-id when it sent one (a retry carries the same id, as for a gacha draw), else a fresh key.
//   failure  session down is the 503 contract (LoadoutUnavailable); a value the published schema refuses (a deck of
//            nine discs, a repeated disc) is a 400 from session and the game's error contract here. No loadout is ever
//            invented: the one default - a new player's five starter decks - is written to ext, not just assumed.
public sealed partial class DemoSessionApi
{
    // What one read of the caller's card gave: the loadout, and the name session shows for the player.
    private sealed record OwnLoadout(Loadout Loadout, string? DisplayName);

    // The caller's loadout from session, as a result a handler can turn into a 503.
    private async Task<(OwnLoadout? Own, IResult? Failure)> TryReadOwnLoadoutAsync(HttpContext context,
        KfIdentityTicket ticket, string path)
    {
        try
        {
            return (await ReadOwnLoadoutAsync(ticket, context.RequestAborted), null);
        }
        catch (Exception exception) when (exception is OtomoRankException or OtomoIdentityException)
        {
            return (null, LoadoutUnavailable(context, path, exception));
        }
    }

    // GET /me; a player session has no decks for yet gets the starter decks written once, under a key per player, so two
    // first reads that race write one value. The loadout returned is what session holds after that write.
    private async Task<OwnLoadout> ReadOwnLoadoutAsync(KfIdentityTicket ticket, CancellationToken cancellationToken)
    {
        if (!_profile.Enabled)
        {
            throw new OtomoRankException(OtomoRankFailure.Disabled,
                "Otomo:Rank:Enabled is false: this server holds no loadout and cannot say what a player's decks are");
        }

        var me = await _profile.GetOwnProfileAsync(await PlayerTokenAsync(ticket, cancellationToken), cancellationToken);
        var loadout = Loadout.FromExt(me.Ext);
        if (!loadout.HasDecks)
        {
            var starter = new JsonObject { [Loadout.DecksField] = Loadout.DecksToExt(Loadout.DefaultDecks()) };
            // A merge replaces a field whole, so an active deck the player already chose is left out of the write.
            if (me.Ext is not { ValueKind: JsonValueKind.Object } held || !held.TryGetProperty(Loadout.ActiveDeckField, out _))
                starter[Loadout.ActiveDeckField] = 1;
            var seeded = await _profile.WriteExtAsync(ticket.OtomoPlayerId, starter,
                $"loadout-default:{ticket.OtomoPlayerId}", merge: true, cancellationToken);
            _logger.LogInformation("Wrote the starter decks to the session card of player {OtomoPlayerId}{Replayed}",
                ticket.OtomoPlayerId, seeded.Applied ? "" : " (already written)");
            loadout = Loadout.FromExt(seeded.Ext);
            if (!loadout.HasDecks)
            {
                throw new OtomoRankException(OtomoRankFailure.Unavailable,
                    "session accepted the starter decks but its card still carries none");
            }
        }
        return new OwnLoadout(RepairDecks(loadout, ticket.OtomoPlayerId), me.DisplayName);
    }

    // A deck slot pointing at a disc that no longer exists in the disc master makes the client throw in
    // PlayerDeckParameter..ctor and the battle never load, so such a slot reads as a valid disc instead. In memory only:
    // the repaired value is what this request serves, and the next write of that deck stores it.
    private Loadout RepairDecks(Loadout loadout, string otomoPlayerId)
    {
        if (_discIdList.Count == 0) return loadout;
        foreach (var (deckNumber, deck) in loadout.DeckSets)
        {
            for (var slot = 0; slot < deck.Count; slot++)
            {
                if (_discIdList.Contains(deck[slot])) continue;
                var replacement = _discIdList.FirstOrDefault(id => !deck.Contains(id), _discIdList[0]);
                _logger.LogWarning("Player {OtomoPlayerId} deck {Deck} slot {Slot}: disc {DiscId} is not in the masters, served as {Replacement}",
                    otomoPlayerId, deckNumber, slot + 1, deck[slot], replacement);
                deck[slot] = replacement;
            }
        }
        return loadout;
    }

    // The name a request serves for the caller: session's display_name when this request already read the card and the
    // player has chosen a name, else the adapter's own. The local copy is what the onboarding
    // gate (HasName) and the requests that never read the card still use.
    private static string OwnName(SessionState state, OwnLoadout? own) =>
        state.HasName && !string.IsNullOrWhiteSpace(own?.DisplayName) ? own!.DisplayName! : CurrentUserName(state);

    // The name a battle entry carries: session's display_name when the entry read the card and the player has a name, else
    // the adapter's own (empty for an unnamed player, as it always was).
    private static string EntryName(SessionState state, OwnLoadout own) =>
        state.HasName && !string.IsNullOrWhiteSpace(own.DisplayName) ? own.DisplayName! : state.UserName;

    // The key a loadout write is made under. A client retry of one request carries the same x-app-response-cache-id, so
    // the retry names the same write; without one the key is fresh and only content-idempotent.
    private string LoadoutKey(HttpContext context, string operation)
    {
        var cacheId = ProgressKeyPart(context.Request.Headers["x-app-response-cache-id"].ToString());
        return cacheId.Length > 0 ? $"loadout:{operation}:{cacheId}" : $"loadout:{operation}:{Guid.NewGuid():N}";
    }

    // A merge write of some ext fields. Refused (a 4xx from session) is returned as the game's error status; an outage
    // or a disabled plane is the 503 contract. `written` is the ext as session holds it afterwards.
    private async Task<(SessionExtWrite? Written, IResult? Failure)> TryWriteLoadoutAsync(HttpContext context,
        KfIdentityTicket ticket, byte[] key, string path, string operation, JsonObject ext)
    {
        try
        {
            if (!_profile.Enabled)
            {
                throw new OtomoRankException(OtomoRankFailure.Disabled,
                    "Otomo:Rank:Enabled is false: this server cannot store a loadout");
            }
            var written = await _profile.WriteExtAsync(ticket.OtomoPlayerId, ext, LoadoutKey(context, operation),
                merge: true, context.RequestAborted);
            return (written, null);
        }
        catch (OtomoRankException exception) when (exception.Failure == OtomoRankFailure.Refused)
        {
            _logger.LogWarning("{Path}: session refused the loadout write for player {OtomoPlayerId}: {Status} {Code} ({Detail})",
                path, ticket.OtomoPlayerId, exception.StatusCode, exception.Code, exception.Message);
            return (null, StatusError(context, key));
        }
        catch (Exception exception) when (exception is OtomoRankException or OtomoIdentityException)
        {
            return (null, LoadoutUnavailable(context, path, exception));
        }
    }

    // A read or write of the loadout that session could not answer: the game's error contract, 503, and never an
    // invented loadout. Mirrors SocialUnavailable, which is the same situation for the follow relation.
    private IResult LoadoutUnavailable(HttpContext context, string path, Exception? failure)
    {
        var reason = failure switch
        {
            OtomoIdentityException identity => identity.Failure.ToString(),
            OtomoRankException rank => rank.Failure.ToString(),
            _ => OtomoRankFailure.Disabled.ToString()
        };
        _logger.LogError("Refusing {Path}: session loadout {Reason} ({Detail})", path, reason,
            failure?.Message ?? "Otomo:Rank:Enabled is false");

        context.Response.Headers["x-app-status-code"] = "1";
        return Results.Json(new { error = "session-unavailable", reason },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // Another player's card, on the internal plane: their name and their ext (so their decks). Null with an error when
    // session cannot answer, which the caller turns into the 503; a player session has no card for is Refused 404.
    private async Task<(SessionPlayerProfile? Card, Exception? Error)> TryReadCardAsync(string otomoPlayerId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_profile.Enabled)
            {
                return (null, new OtomoRankException(OtomoRankFailure.Disabled,
                    "Otomo:Rank:Enabled is false: this server cannot read another player's card"));
            }
            return (await _profile.GetPlayerProfileAsync(otomoPlayerId, cancellationToken), null);
        }
        catch (OtomoRankException exception)
        {
            return (null, exception);
        }
    }
}
