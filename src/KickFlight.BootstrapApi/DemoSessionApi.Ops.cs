using System.Text.Json;
using KickFlight.BootstrapApi.Ops;

namespace KickFlight.BootstrapApi;

// The battle signals the retail client sends and the server used to swallow in the generic /battle/* catch-all:
//   /battle/disconnect {battleEntryId, cause}   the client left matching/the match over a lost connection
//   /battle/timeout    {battleEntryId}          the matching countdown reached 0 before the full roster arrived
//   /battle/leaving    (fields not documented)  the player left the match
// The reply is unchanged (status 0, {}) - only the ops telemetry is new. A body that cannot be decoded is still
// recorded, without its fields.
public sealed partial class DemoSessionApi
{
    private async Task<IResult?> HandleBattleSignalAsync(string path, HttpContext context, SessionState state, byte[] key)
    {
        var battleEntryId = "";
        var battleId = "";
        var cause = "";
        try
        {
            var body = await ReadBodyAsync(context.Request);
            if (body.Length > D2CCodec.VectorSizeBytes)
            {
                using var document = JsonDocument.Parse(D2CCodec.Decode(body, key));
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    TryReadString(root, "battleEntryId", out battleEntryId);
                    TryReadString(root, "battleId", out battleId);
                    TryReadString(root, "cause", out cause);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode {Path} body for {UserId}: {Error}", path, state.UserId, ex.Message);
        }

        var info = OpsClientInfo.FromRequest(context);
        switch (path)
        {
            case "/battle/disconnect":
                _ops.RecordDisconnect(state.UserId, battleEntryId, cause, info);
                break;
            case "/battle/timeout":
                _ops.RecordTimeout(state.UserId, battleEntryId, info);
                break;
            default:
                _ops.RecordLeaving(state.UserId, battleEntryId, battleId, info);
                break;
        }
        _logger.LogInformation("Handled {Path} for {UserId}: entry={BattleEntryId} cause={Cause}",
            path, state.UserId, battleEntryId, cause);

        context.Response.Headers["x-app-status-code"] = "0";
        return BinaryJson("{}", key);
    }
}
