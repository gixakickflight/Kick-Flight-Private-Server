using System.Text.Json;
using System.Text.Json.Nodes;
using KickFlight.BootstrapApi.Otomo;

namespace KickFlight.BootstrapApi;

// A player's loadout - the decks, the active deck and the gear in each costume's slots - is session's Loadout (the tenant's
// session.loadout rules, schema version 1), not this server's and not ext. The gear roll waiting to be equipped is the one
// part the player does not write: it stays in ext (ext.session.player, pending_gear), written by the server.
//
//   reads    GET /api/player/session/loadout with the player's own token, and GET /me for the card (name and the roll): startup,
//            home, the battle entry / team handlers and every kicker list that shows gear. Another player's deck (a friend card)
//            is GET /internal/session/players/{id}/loadout with the game-server key.
//   writes   GET -> modify -> PUT /api/player/session/loadout with If-Match: <revision read>, on the player's own token
//            (/disc/change, /gear/set). A 409 revision_conflict carries the current document: the edit is applied again to
//            that and sent again, up to LoadoutSaveAttempts times in all. A 422 invalid_loadout is logged with its problems
//            and is the game's error contract. The default decks of a player with no loadout are saved once with If-Match: 0;
//            a 409 there means a concurrent first save won, and the loadout is simply read again.
//   ext      the roll (/gear/create, /gear/destroy, and cleared by /gear/set) is POST /internal/session/players/ext, merge =
//            true, keyed so a retry writes once: the client's x-app-response-cache-id when it sent one (a retry carries the
//            same id, as for a gacha draw), else a fresh key.
//   failure  session down is the 503 contract (LoadoutUnavailable). No loadout is ever invented: the one default - a new
//            player's five starter decks - is saved to session, not just assumed.
public sealed partial class DemoSessionApi
{
    // The most times one edit is tried against a loadout that keeps changing under it: the first attempt and two retries.
    private const int LoadoutSaveAttempts = 3;

    // What one read of the caller's card and loadout gave: the loadout, and the name session shows for the player.
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

    // GET /me for the card and GET /loadout for the decks; a player session has no loadout for yet gets the starter decks
    // saved once with If-Match: 0, so two first reads that race save one value. The loadout returned is what session holds.
    private async Task<OwnLoadout> ReadOwnLoadoutAsync(KfIdentityTicket ticket, CancellationToken cancellationToken)
    {
        if (!_profile.Enabled)
        {
            throw new OtomoRankException(OtomoRankFailure.Disabled,
                "Otomo:Rank:Enabled is false: this server holds no loadout and cannot say what a player's decks are");
        }

        var token = await PlayerTokenAsync(ticket, cancellationToken);
        var meRead = _profile.GetOwnProfileAsync(token, cancellationToken);
        var loadoutRead = _profile.GetOwnLoadoutAsync(token, cancellationToken);
        await Task.WhenAll(meRead, loadoutRead);
        var me = await meRead;
        var roll = Loadout.RollFromExt(me.Ext);
        var stored = await loadoutRead ?? await SaveStarterLoadoutAsync(ticket, token, roll, cancellationToken);
        return new OwnLoadout(RepairDecks(Loadout.FromSession(stored, roll), ticket.OtomoPlayerId), me.DisplayName);
    }

    private async Task<SessionLoadout> SaveStarterLoadoutAsync(KfIdentityTicket ticket, string token, GearRoll roll,
        CancellationToken cancellationToken)
    {
        var write = await _profile.PutOwnLoadoutAsync(token, Loadout.Starter(roll).ToDocument(), 0, cancellationToken);
        if (write.Saved is { } saved)
        {
            _logger.LogInformation("Saved the starter loadout of player {OtomoPlayerId}", ticket.OtomoPlayerId);
            return saved;
        }
        if (write.Current is not null)
        {
            // Another request saved the first loadout between our read and our save: read what it saved.
            _logger.LogInformation("The starter loadout of player {OtomoPlayerId} was saved by a concurrent request; reading it",
                ticket.OtomoPlayerId);
            return await _profile.GetOwnLoadoutAsync(token, cancellationToken)
                   ?? throw new OtomoRankException(OtomoRankFailure.Unavailable,
                       "session reported a concurrent first save, but its loadout reads as absent");
        }
        _logger.LogError("session refused the starter loadout of player {OtomoPlayerId}: {Problems}", ticket.OtomoPlayerId,
            DescribeIssues(write.Issues));
        throw new OtomoRankException(OtomoRankFailure.Unavailable,
            "session refused the starter loadout; the adapter and the tenant's session.loadout schema disagree");
    }

    private static string DescribeIssues(IReadOnlyList<SessionLoadoutIssue>? issues) =>
        issues is null || issues.Count == 0
            ? "(no problems listed)"
            : string.Join("; ", issues.Select(issue => $"{(issue.Pointer.Length == 0 ? "(document)" : issue.Pointer)} {issue.Message}"));

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

    // Edits the caller's loadout and saves it: GET -> modify -> PUT with If-Match, on the player's own token. `own` is the read
    // the handler already made. If the loadout changed meanwhile (409 revision_conflict), the edit is applied again to the
    // document session returned and sent again, LoadoutSaveAttempts times in all; a loadout that never settles is the 503
    // contract. A 422 is logged with its problems and is the game's error contract; nothing was stored.
    private async Task<(Loadout? Saved, IResult? Failure)> TryEditLoadoutAsync(HttpContext context,
        KfIdentityTicket ticket, byte[] key, string path, OwnLoadout own, Action<Loadout> edit)
    {
        try
        {
            if (!_profile.Enabled)
            {
                throw new OtomoRankException(OtomoRankFailure.Disabled,
                    "Otomo:Rank:Enabled is false: this server cannot store a loadout");
            }
            var token = await PlayerTokenAsync(ticket, context.RequestAborted);
            var current = own.Loadout;
            for (var attempt = 1; attempt <= LoadoutSaveAttempts; attempt++)
            {
                var edited = current.Copy();
                edit(edited);
                var write = await _profile.PutOwnLoadoutAsync(token, edited.ToDocument(), current.Revision,
                    context.RequestAborted);
                if (write.Saved is { } saved) return (Loadout.FromSession(saved, current.Roll), null);
                if (write.Issues is not null)
                {
                    _logger.LogWarning("{Path}: session refused the loadout of player {OtomoPlayerId}: {Problems}", path,
                        ticket.OtomoPlayerId, DescribeIssues(write.Issues));
                    return (null, StatusError(context, key));
                }
                _logger.LogInformation("{Path}: the loadout of player {OtomoPlayerId} moved on from revision {Revision} (attempt {Attempt} of {Attempts})",
                    path, ticket.OtomoPlayerId, current.Revision, attempt, LoadoutSaveAttempts);
                current = RepairDecks(Loadout.FromSession(write.Current!, current.Roll), ticket.OtomoPlayerId);
            }
            throw new OtomoRankException(OtomoRankFailure.Unavailable,
                $"the loadout kept changing: {LoadoutSaveAttempts} saves in a row were refused as stale");
        }
        catch (OtomoRankException exception) when (exception.Failure == OtomoRankFailure.Refused)
        {
            _logger.LogWarning("{Path}: session refused the loadout save for player {OtomoPlayerId}: {Status} {Code} ({Detail})",
                path, ticket.OtomoPlayerId, exception.StatusCode, exception.Code, exception.Message);
            return (null, StatusError(context, key));
        }
        catch (Exception exception) when (exception is OtomoRankException or OtomoIdentityException)
        {
            return (null, LoadoutUnavailable(context, path, exception));
        }
    }

    // The key a gear-roll write is made under. A client retry of one request carries the same x-app-response-cache-id, so
    // the retry names the same write; without one the key is fresh and only content-idempotent.
    private string LoadoutKey(HttpContext context, string operation)
    {
        var cacheId = ProgressKeyPart(context.Request.Headers["x-app-response-cache-id"].ToString());
        return cacheId.Length > 0 ? $"loadout:{operation}:{cacheId}" : $"loadout:{operation}:{Guid.NewGuid():N}";
    }

    // A merge write of the gear roll to ext. Refused (a 4xx from session) is returned as the game's error status; an outage
    // or a disabled plane is the 503 contract. `written` is the ext as session holds it afterwards.
    private async Task<(SessionExtWrite? Written, IResult? Failure)> TryWriteGearRollAsync(HttpContext context,
        KfIdentityTicket ticket, byte[] key, string path, string operation, GearRoll roll)
    {
        try
        {
            if (!_profile.Enabled)
            {
                throw new OtomoRankException(OtomoRankFailure.Disabled,
                    "Otomo:Rank:Enabled is false: this server cannot store a gear roll");
            }
            var written = await _profile.WriteExtAsync(ticket.OtomoPlayerId,
                new JsonObject { [Loadout.GearRollField] = Loadout.RollToExt(roll) }, LoadoutKey(context, operation),
                merge: true, context.RequestAborted);
            return (written, null);
        }
        catch (OtomoRankException exception) when (exception.Failure == OtomoRankFailure.Refused)
        {
            _logger.LogWarning("{Path}: session refused the gear roll write for player {OtomoPlayerId}: {Status} {Code} ({Detail})",
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

    // Another player's card, on the internal plane: their name. Null with an error when session cannot answer, which the
    // caller turns into the 503; a player session has no card for is Refused 404.
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

    // Another player's loadout, on the internal plane: their decks. A player who has saved none has no decks (Loadout.Empty),
    // which every reader completes from the initial deck; a stranger is Refused 404 player_not_found.
    private async Task<(Loadout? Loadout, Exception? Error)> TryReadPlayerLoadoutAsync(string otomoPlayerId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_profile.Enabled)
            {
                return (null, new OtomoRankException(OtomoRankFailure.Disabled,
                    "Otomo:Rank:Enabled is false: this server cannot read another player's loadout"));
            }
            var stored = await _profile.GetPlayerLoadoutAsync(otomoPlayerId, cancellationToken);
            return (stored is null ? Loadout.Empty : Loadout.FromSession(stored, GearRoll.None), null);
        }
        catch (OtomoRankException exception)
        {
            return (null, exception);
        }
    }
}
