using System.Security.Cryptography;
using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;

namespace KickFlight.BootstrapApi;

// The replay screens, as the client reads them.
//
// None of it is kept here. A replay is a recording in otomo's patch service (IOtomoRecordings): uploaded with the
// game-server key, listed by tag and by the published curated list, and fetched with the player's own token. This file
// decodes the client's bodies and answers in its contract; BattleReplayService is the translation.
//
// Failure is the same contract as the social screens: patch not answering is a 503 the client can act on, never an
// empty list (which it would render as fact) and never a replay served from somewhere else.
public sealed partial class DemoSessionApi
{
    private IResult ReplayUnavailable(HttpContext context, string path, Exception failure)
    {
        var reason = failure switch
        {
            OtomoIdentityException identity => identity.Failure.ToString(),
            OtomoRecordingsException recordings => recordings.Failure.ToString(),
            _ => OtomoRecordingsFailure.Unavailable.ToString()
        };
        _logger.LogError("Refusing {Path}: patch recordings {Reason} ({Detail})", path, reason, failure.Message);

        context.Response.Headers["x-app-status-code"] = "1";
        return Results.Json(new { error = "replay-unavailable", reason },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // /battle/upload: base64( gzip( archive ) ) inside the D2C body, exactly what ReplayUtility.GetReplayWorkZipData
    // hands to Convert.ToBase64String (see docs/REPLAYS.md). The response DTO is empty.
    private async Task<IResult?> HandleBattleUploadAsync(HttpContext context, KfIdentityTicket ticket, SessionState state,
        byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            var battleId = TryGetString(root, "battleId", out var parsedId) ? parsedId : "";
            var uploadFile = TryGetString(root, "uploadFile", out var parsedFile) ? parsedFile : "";
            if (string.IsNullOrEmpty(uploadFile)) return StatusError(context, key);

            // Reject on the base64 length before allocating the decoded buffer (base64 is 4/3 of the raw size).
            if (uploadFile.Length > _replays.MaxUploadBytes / 3 * 4 + 8)
            {
                _logger.LogWarning("Rejected /battle/upload for {UserId}: base64 upload is over the {Cap}-byte cap",
                    state.UserId, _replays.MaxUploadBytes);
                return StatusError(context, key);
            }

            byte[] gzip;
            try
            {
                gzip = Convert.FromBase64String(uploadFile);
            }
            catch (FormatException)
            {
                _logger.LogWarning("Rejected /battle/upload for {UserId}: uploadFile is not base64", state.UserId);
                return StatusError(context, key);
            }

            bool stored;
            try
            {
                stored = await _replays.StoreUploadAsync(battleId, gzip, ticket.OtomoPlayerId, ResolveOtomoPlayer,
                    _timeProvider.GetUtcNow().UtcDateTime, context.RequestAborted);
            }
            catch (OtomoRecordingsException exception)
            {
                return ReplayUnavailable(context, "/battle/upload", exception);
            }

            context.Response.Headers["x-app-status-code"] = "0";
            context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-upload";
            _logger.LogInformation("Handled /battle/upload for {UserId}: battle={BattleId} bytes={Bytes} stored={Stored}",
                state.UserId, battleId, gzip.Length, stored);
            return BinaryJson("{}", key);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException or InvalidDataException or IOException)
        {
            _logger.LogWarning("Could not handle /battle/upload for {UserId}: {Error}", state.UserId, ex.Message);
            return StatusError(context, key);
        }
    }

    // A battle names its players by the numeric id this server hands the client; patch knows them by otomo account.
    private string? ResolveOtomoPlayer(string battleUserId) =>
        long.TryParse(battleUserId, out var playerId) ? _playerStore.FindOtomoPlayer(playerId)?.OtomoPlayerId : null;

    // /battleReplay/index: Featured is channel id 1 (the served type-3 master row); each kicker is 1000+kickerId
    // (the type-2 rows). The client matches entries by battleReplayChannelId against BattleReplayChannelMaster.
    private async Task<IResult> HandleBattleReplayIndexAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        var kickerIds = _kickerList.Select(k => k.Id).OrderBy(id => id).ToArray();
        string json;
        try
        {
            var accessToken = await PlayerTokenAsync(ticket, context.RequestAborted);
            json = await _replays.BuildIndexJsonAsync(accessToken, kickerIds, DefaultCostumeRowId, context.RequestAborted);
        }
        catch (Exception exception) when (exception is OtomoRecordingsException or OtomoIdentityException)
        {
            return ReplayUnavailable(context, "/battleReplay/index", exception);
        }
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-replay-index";
        return OkJson(context, key, json);
    }

    private int DefaultCostumeRowId(int kickerId) =>
        _costumesByKicker.TryGetValue(kickerId, out var rows) && rows.Count > 0 ? rows[0] : 1;

    private async Task<IResult?> HandleBattleReplayPlayAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        var replayId = "";
        try
        {
            var body = await ReadBodyAsync(context.Request);
            if (body.Length > D2CCodec.VectorSizeBytes)
            {
                using var document = JsonDocument.Parse(D2CCodec.Decode(body, key));
                if (TryGetString(document.RootElement, "battleReplayId", out var parsed)) replayId = parsed;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
        {
            _logger.LogWarning("Could not decode /battleReplay/play request: {Error}", ex.Message);
            return StatusError(context, key);
        }

        string keyId, encryptionKey;
        try
        {
            (keyId, encryptionKey) = await _replays.CreatePlayKeyAsync(replayId, ticket.DeviceId,
                _timeProvider.GetUtcNow().UtcDateTime, context.RequestAborted);
        }
        catch (OtomoRecordingsException exception)
        {
            return ReplayUnavailable(context, "/battleReplay/play", exception);
        }

        var url = string.IsNullOrEmpty(keyId)
            ? ""
            : $"{context.Request.Scheme}://{context.Request.Host}/battleReplay/blob/{keyId}";
        _logger.LogInformation("Handled /battleReplay/play for {UserId}: replay={ReplayId} found={Found}",
            state.UserId, replayId, !string.IsNullOrEmpty(keyId));
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-replay-play";
        return OkJson(context, key, JsonSerializer.Serialize(new { battleReplayUrl = url, encryptionKey }));
    }

    // GET /battleReplay/blob/{keyId}: the recording from patch's internal route with the game-server key, in the client's
    // encryption. No session headers arrive here, so a failure is a bare status, not the game's error body.
    private async Task<IResult> HandleBattleReplayBlobAsync(HttpContext context, string keyId)
    {
        try
        {
            var encoded = await _replays.TryEncodeBlobAsync(keyId, _timeProvider.GetUtcNow().UtcDateTime,
                context.RequestAborted);
            return encoded is null ? Results.NotFound() : Results.Bytes(encoded, "application/octet-stream");
        }
        catch (OtomoRecordingsException exception)
        {
            _logger.LogError("Refusing /battleReplay/blob: {Reason}", exception.Message);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
