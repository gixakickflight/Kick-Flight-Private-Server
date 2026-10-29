using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;

namespace KickFlight.BootstrapApi;

// Missions: the client's mission screen over economy's tracks.
//
// A track is economy's (a counter the battle result reports, steps with a target and a class), and so is every
// decision about it: which step is reached, which one a claim pays, and what it pays. This file translates ids -
// a track to a missionId, a step to a missionTaskId, through Otomo/EconomyMissions.cs - and nothing more. The
// claim is the player's own, on the player plane: the adapter cannot pay a mission, it can only ask economy to.
//
// The routes, from the client (dump.cs, docs/ECONOMY_DTOS.md):
//
//   /mission/index, /mission/read, /mission/tweet   the screen, re-read: userMissionProgressList
//   /mission/receipt {missionTaskId, loopCount}     the receive button: POST /api/player/economy/tasks/claim
//
// userDailyRandomMissionTaskList stays empty: the daily random missions have no economy counterpart.
public sealed partial class DemoSessionApi
{
    private async Task<IResult?> TryHandleMissionAsync(string path, HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        switch (path)
        {
            case "/mission/index":
            case "/mission/read":
            case "/mission/tweet":
            {
                // A mission list that cannot be read fails closed like every other economy read: an empty list
                // would tell the player their missions are gone, or done.
                var economy = await ReadPlayerEconomyAsync(ticket, EconomyRead.Tasks, path, context.RequestAborted);
                if (economy.Snapshot is null) return EconomyUnavailable(context, path, economy.Error);
                var response = new
                {
                    userMissionProgressList = Payloads(state).BuildMissionProgressList(economy.Snapshot.Tracks),
                    userDailyRandomMissionTaskList = Array.Empty<object>()
                };
                return OkJson(context, key, JsonSerializer.Serialize(response));
            }
            case "/mission/receipt":
                return await HandleMissionReceiptAsync(context, ticket, state, key);
            default:
                return null;
        }
    }

    // The receive button. The client names a task; the task names a track; economy claims that track's next
    // reached step, whichever rung the client tapped, so a client cannot skip ahead or be paid twice for one.
    //
    // The idempotency key is the rung itself - track, period, repetition and the step economy says is claimable -
    // so a retried receipt (the client resends after a timeout) is economy's replay of the first claim rather than
    // a claim of the next rung.
    private async Task<IResult?> HandleMissionReceiptAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        int missionTaskId;
        try
        {
            using var document = JsonDocument.Parse(D2CCodec.Decode(await ReadBodyAsync(context.Request), key));
            missionTaskId = document.RootElement.GetProperty("missionTaskId").GetInt32();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode mission receipt request: {Error}", ex.Message);
            return StatusError(context, key);
        }

        if (EconomyMissions.ByTaskId(missionTaskId) is not { } target)
        {
            _logger.LogWarning("mission/receipt for user {UserId}: task {TaskId} is not a served mission", state.UserId,
                missionTaskId);
            return StatusError(context, key);
        }

        var before = await ReadPlayerEconomyAsync(ticket, EconomyRead.Tasks, "/mission/receipt", context.RequestAborted);
        if (before.Snapshot is null) return EconomyUnavailable(context, "/mission/receipt", before.Error);
        var track = before.Snapshot.Tracks?.FirstOrDefault(row => row.TrackId == target.Mission.TrackId);
        var rung = track?.Steps?.FirstOrDefault(step => step.Claimable);
        if (track is null || rung is null)
        {
            _logger.LogInformation("mission/receipt for user {UserId}: track {Track} has nothing to claim",
                state.UserId, target.Mission.TrackId);
            return StatusError(context, key);
        }

        EconomyClaim claim;
        try
        {
            claim = await _economy.ClaimAsync(await PlayerTokenAsync(ticket, context.RequestAborted), track.TrackId,
                ProgressKeyPart($"mission:{track.TrackId}:{track.Period}:{track.Repetition}:{rung.Step}"),
                context.RequestAborted);
        }
        catch (OtomoIdentityException ex)
        {
            return EconomyUnavailable(context, "/mission/receipt", ex);
        }
        catch (OtomoEconomyException ex) when (ex.Failure == OtomoEconomyFailure.Refused)
        {
            _logger.LogWarning("mission/receipt track {Track} was refused by economy: {Status} {Code}",
                track.TrackId, ex.StatusCode, ex.Code);
            return StatusError(context, key);
        }
        catch (OtomoEconomyException ex)
        {
            return EconomyUnavailable(context, "/mission/receipt", ex);
        }

        // The claim has paid, so from here the answer must describe it rather than fail: a progress list that cannot
        // be re-read falls back to the one read before the claim.
        var after = await ReadPlayerEconomyAsync(ticket, EconomyRead.Tasks, "/mission/receipt", context.RequestAborted);
        var payloads = Payloads(state);
        var response = new
        {
            receivedUserCapsuleList = Array.Empty<object>(),
            receivedUserDiscList = payloads.BuildReceivedUserDiscList(claim.Items, discId => DiscStandingOf(state, discId)),
            receivedUserHonorList = Array.Empty<object>(),
            receivedUserItemList = payloads.BuildReceivedCounters(claim.Balances, claim.Entries, claim.Items),
            receivedUserStampList = Array.Empty<object>(),
            userPresentList = Array.Empty<object>(),
            userMissionProgressList = payloads.BuildMissionProgressList((after.Snapshot ?? before.Snapshot).Tracks),
            receivedRewardList = payloads.BuildReceivedRewards(claim.Entries, claim.Items)
        };

        _logger.LogInformation("mission/receipt: user {UserId} claimed {Track} step {Step} ({Class}){Replayed}",
            state.UserId, claim.TrackId, claim.Step, claim.Class, claim.Replayed ? " (replayed)" : "");
        return OkJson(context, key, JsonSerializer.Serialize(response));
    }

    // The tracks after a battle, for the result screen's mission rows. Best effort: the result has already been
    // applied, and a mission list that cannot be read is an empty list here, never a broken result screen.
    private async Task<object[]> ReadMissionProgressAfterBattleAsync(KfIdentityTicket ticket, SessionState state,
        CancellationToken cancellationToken)
    {
        var economy = await ReadPlayerEconomyAsync(ticket, EconomyRead.Tasks, "/battle/result", cancellationToken);
        return economy.Snapshot is { } snapshot ? Payloads(state).BuildMissionProgressList(snapshot.Tracks) : [];
    }
}
