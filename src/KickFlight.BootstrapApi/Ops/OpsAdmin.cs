namespace KickFlight.BootstrapApi.Ops;

/// <summary>
/// Read-only ops endpoints for the balance tool's Health tab, loopback only like the other /admin routes (see
/// ClientUpdateAdmin): tools/balance proxies them with
///   docker exec deploy-api-1 curl -s 'http://127.0.0.1:8080/admin/ops/summary?window=deploy'
/// GET /admin/ops/summary?window=deploy|restart|24h|7d   totals, breakdowns, problem matches
/// GET /admin/ops/battle/{battleId}                       participants and the event timeline of one match
/// GET /admin/ops/exceptions?window=...                   client error reports grouped by fingerprint
/// Each first waits (briefly) for the telemetry queue to drain, so what was just recorded is in the answer.
/// </summary>
public static class OpsAdmin
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

    public static async Task<IResult> SummaryAsync(HttpContext context, OpsTelemetryService ops, TimeProvider time, string? window)
    {
        if (!MaintenanceAdmin.IsLocalAdmin(context)) return Results.NotFound();
        var ct = context.RequestAborted;
        await ops.FlushAsync(FlushTimeout);
        var resolved = await OpsReports.ResolveWindowAsync(ops, window, time.GetUtcNow().UtcDateTime, ct);
        if (resolved is null) return UnknownWindow(window);
        var battles = await ops.Store.LoadBattlesAsync(resolved.Since, ct);
        var participants = await ops.Store.LoadParticipantsAsync(resolved.Since, ct);
        var kinds = await ops.Store.CountEventsByKindAsync(resolved.Since, ct);
        return Results.Json(OpsReports.BuildSummary(ops, resolved, battles, participants, kinds));
    }

    public static async Task<IResult> ExceptionsAsync(HttpContext context, OpsTelemetryService ops, TimeProvider time, string? window)
    {
        if (!MaintenanceAdmin.IsLocalAdmin(context)) return Results.NotFound();
        var ct = context.RequestAborted;
        await ops.FlushAsync(FlushTimeout);
        var resolved = await OpsReports.ResolveWindowAsync(ops, window, time.GetUtcNow().UtcDateTime, ct);
        if (resolved is null) return UnknownWindow(window);
        var events = await ops.Store.LoadEventsAsync(
            new OpsEventQuery(resolved.Since, Kinds: [OpsFingerprint.ExceptionKind, OpsFingerprint.AnomalyKind], Limit: 20_000), ct);
        return Results.Json(OpsReports.BuildExceptions(ops, resolved, events));
    }

    public static async Task<IResult> BattleAsync(HttpContext context, OpsTelemetryService ops, TimeProvider time, string battleId)
    {
        if (!MaintenanceAdmin.IsLocalAdmin(context)) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(battleId) || battleId.Length > 128)
            return Results.BadRequest(new { error = "battle id expected" });
        var ct = context.RequestAborted;
        await ops.FlushAsync(FlushTimeout);
        var battle = await ops.Store.LoadBattleAsync(battleId, ct);
        var participants = await ops.Store.LoadBattleParticipantsAsync(battleId, ct);
        if (battle is null && participants.Count == 0) return Results.Json(new { error = "unknown battle", battleId }, statusCode: 404);
        var since = (battle?.CreatedAt ?? participants.Min(p => p.CreatedAt)) - TimeSpan.FromHours(1);
        var events = await ops.Store.LoadEventsAsync(new OpsEventQuery(since, BattleId: battleId, Limit: 5000), ct);
        return Results.Json(OpsReports.BuildBattle(battle, participants, events));
    }

    private static IResult UnknownWindow(string? window) => Results.BadRequest(new
    {
        error = $"unknown window '{window}'",
        windows = OpsReports.WindowNames
    });
}
