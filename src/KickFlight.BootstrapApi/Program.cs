using System.Security.Cryptography.X509Certificates;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.Ops;
using KickFlight.BootstrapApi.Otomo;
using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.UseUtcTimestamp = true;
});

builder.Services.Configure<HarnessOptions>(builder.Configuration.GetSection("Harness"));
builder.Services.Configure<PhotonServerOptions>(builder.Configuration.GetSection(PhotonServerOptions.SectionName));
builder.Services.AddSingleton<FixtureStore>();
builder.Services.AddSingleton<ResourceCatalogStore>();
builder.Services.AddSingleton<SafeRequestInspector>();
builder.Services.AddSingleton<IPhotonServerManager, PhotonServerManager>();
builder.Services.AddSingleton<MaintenanceState>();
builder.Services.AddSingleton<ClientUpdateState>();
builder.Services.AddSingleton<LatestInformationState>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<BattleMatchmakingService>();
builder.Services.AddSingleton<BattleReplayService>();
builder.Services.AddSingleton<OpenMatchFrontendService>();
// Player state: PostgreSQL when a connection string is configured, the per-user JSON files otherwise.
builder.Services.AddSingleton<IPlayerStore>(services => PlayerStoreFactory.Create(services, builder.Configuration));
// Identity: otomo owns every account (see docs/OTOMO_IDENTITY.md). This adapter's client for it is a singleton
// because its per-device token cache is the only session state in the process.
builder.Services.Configure<OtomoOptions>(builder.Configuration.GetSection(OtomoOptions.SectionName));
builder.Services.AddSingleton(services => new KfAccessTokenCodec(
    builder.Configuration[$"{OtomoOptions.SectionName}:TokenSecret"],
    services.GetRequiredService<ILogger<KfAccessTokenCodec>>()));
builder.Services.AddSingleton<IOtomoIdentity>(services => OtomoIdentity.Create(
    services.GetRequiredService<IOptions<OtomoOptions>>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<OtomoIdentity>>()));
// Ops telemetry (Ops/OpsTelemetryService): the Record* calls are made by DemoSessionApi and the matchmaking room
// events; the same singleton is the background batch writer and 60 s status sweep.
builder.Services.AddSingleton<OpsTelemetryService>();
builder.Services.AddHostedService(services => services.GetRequiredService<OpsTelemetryService>());
builder.Services.AddSingleton<DemoSessionApi>();
builder.Services.AddGrpc();

var certificatePath = builder.Configuration["Certificate:Path"];
var certificatePassword = builder.Configuration["Certificate:Password"];
var httpPort = builder.Configuration.GetValue("HttpPort", 8080);
var grpcPort = builder.Configuration.GetValue("GrpcPort", 18081);

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 256L * 1024 * 1024);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 256L * 1024 * 1024; // phone bug-report zips on /diag/upload-file
    options.ListenAnyIP(httpPort, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
    options.ListenAnyIP(grpcPort, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
    if (!string.IsNullOrWhiteSpace(certificatePath) && File.Exists(certificatePath))
    {
        options.ListenAnyIP(builder.Configuration.GetValue("HttpsPort", 8443), listen =>
            listen.UseHttps(new X509Certificate2(certificatePath, certificatePassword)));
    }
});

var app = builder.Build();

// Resolve the player store here rather than on the first request. Its constructor is what applies the schema
// migrations and opens the connection pool, so a database that is unreachable has to fail the start loudly: if
// this were left lazy, the first request would throw and /health/ready - which never touches the store - would
// keep answering "ready" for a server that cannot serve a single player.
app.Services.GetRequiredService<IPlayerStore>();
// Loads data/maintenance.json now, so a restart during hard maintenance answers 503 from the first request.
app.Services.GetRequiredService<MaintenanceState>();
// Loads data/client-update.json now, so a restart keeps the forced-update minimum from the first request.
app.Services.GetRequiredService<ClientUpdateState>();
app.Services.GetRequiredService<LatestInformationState>();
// Loads every master now (rather than on the first player request) so the one-line "master overrides"
// startup summary - and any missing config - is reported while the server boots.
var demoSessionApi = app.Services.GetRequiredService<DemoSessionApi>();
// One server_runs row per start, tagged with the release (KF_RELEASE_SHA from the deploy workflow): the Health
// dashboard's default window starts at the first run of the current release.
app.Services.GetRequiredService<OpsTelemetryService>().RecordServerStart(
    demoSessionApi.MasterVersion, app.Services.GetRequiredService<ClientUpdateState>().Current.MinimumVersion);

app.UseMiddleware<RequestCaptureMiddleware>();
app.MapGrpcService<OpenMatchFrontendService>();

app.MapGet("/health/live", () => Results.Json(new { status = "live" }));
// Gym mode: open these in any browser (phone too) while the server runs; applies to the next match you start.
app.MapGet("/gym", () => Results.Json(new { gym = BattleMatchmakingService.GymEnabled, bots = BattleMatchmakingService.GymBotCount, usage = "/gym/on | /gym/off" }));
app.MapGet("/gym/on", () => { BattleMatchmakingService.GymEnabled = true; return Results.Text("gym ON: next match = you vs 3 mannequin bots + harmless guardian"); });
app.MapGet("/gym/off", () => { BattleMatchmakingService.GymEnabled = false; return Results.Text("gym OFF: normal 4v4 bot matches"); });
// Maintenance switch (MaintenanceState): loopback only, from inside the container, e.g.
//   docker exec deploy-api-1 curl -s -X POST http://127.0.0.1:8080/admin/maintenance -d '{"mode":"hard"}'
app.MapGet("/admin/maintenance", MaintenanceAdmin.Get);
app.MapPost("/admin/maintenance", MaintenanceAdmin.PostAsync);
// Forced client update (ClientUpdateState): same loopback-only switch, e.g.
//   docker exec deploy-api-1 curl -s -X POST http://127.0.0.1:8080/admin/client-update -d '{"minimumVersion":"2.12.0"}'
app.MapGet("/admin/client-update", ClientUpdateAdmin.Get);
app.MapPost("/admin/client-update", ClientUpdateAdmin.PostAsync);
app.MapGet("/admin/latest-information", LatestInformationAdmin.Get);
app.MapPost("/admin/latest-information", LatestInformationAdmin.PostAsync);
// Ops telemetry for the balance tool's Health tab (OpsAdmin): read-only, same loopback-only rule.
app.MapGet("/admin/ops/summary", (HttpContext context, OpsTelemetryService ops, TimeProvider time, string? window) =>
    OpsAdmin.SummaryAsync(context, ops, time, window));
app.MapGet("/admin/ops/exceptions", (HttpContext context, OpsTelemetryService ops, TimeProvider time, string? window) =>
    OpsAdmin.ExceptionsAsync(context, ops, time, window));
app.MapGet("/admin/ops/battle/{battleId}", (HttpContext context, OpsTelemetryService ops, TimeProvider time, string battleId) =>
    OpsAdmin.BattleAsync(context, ops, time, battleId));
app.Map("/admin/{**rest}", () => Results.NotFound());
// The client's noticeboard opens a WebView and paints its network-error page for any non-200 answer, so every
// /webview/ URL has to return HTML: the real one below, and the catch-all for every other page it may link to.
app.MapGet("/webview/information/index", (HttpContext context, LatestInformationState latestInformation) =>
    latestInformation.Serve(context));
app.MapGet("/webview/{**rest}", (string? rest) => Results.Content($"""
<!doctype html><html lang="es"><meta charset="utf-8">
<meta name=viewport content="width=device-width,initial-scale=1">
<title>/webview/{rest}</title>
<body style="font-family:sans-serif;padding:24px;max-width:640px">
<h2>/webview/{rest}</h2>
<p>Pagina no disponible en este servidor.</p>
</body></html>
""", "text/html; charset=utf-8"));
// APK download for phones: http://<server>:18080/apk (LAN build) and /apk/remote (kickflightsg.ddns.net build).
// Files live in .local/ (git-ignored, produced by .local/build.sh); 404 with the expected path when missing.
var repoRoot = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
var diagnosticUploadDirectory = builder.Configuration["Diagnostics:UploadDirectory"]
    ?? Path.Combine(repoRoot, ".local", "run");
var diagnosticUploads = new DiagnosticUploadStore(diagnosticUploadDirectory);
IResult ServeApk(string fileName)
{
    var path = Path.Combine(repoRoot, ".local", fileName);
    return File.Exists(path)
        ? Results.File(path, "application/vnd.android.package-archive", fileName, enableRangeProcessing: true)
        : Results.Json(new { error = "apk-not-built", expected = path }, statusCode: 404);
}
app.MapGet("/apk", () => ServeApk("KickFlight-2.11.0-current-patches.apk"));
app.MapGet("/apk/remote", () => ServeApk("KickFlight-2.11.5-remote.apk"));
app.MapGet("/apk/diag", () => ServeApk("KickFlight-2.11.0-DIAG.apk"));
app.MapGet("/apk/diag-remote", () => ServeApk("KickFlight-2.11.0-DIAG-remote.apk"));
// Merged patch set (offline combat + Photon 2-player, scripts/patch-il2cpp-endpoints.py since 46f9392), built by
// OUT=.local/KickFlight-2.11.0-merged.apk .local/build.sh (LAN) and with URL=http://kickflightsg.ddns.net:18080 (remote).
app.MapGet("/apk/merged", () => ServeApk("KickFlight-2.11.0-merged.apk"));
app.MapGet("/apk/merged-remote", () => ServeApk("KickFlight-2.11.0-merged-remote.apk"));
app.MapGet("/apk/merged-diag", () => ServeApk("KickFlight-2.11.0-merged-DIAG.apk"));
app.MapGet("/apk/merged-diag-remote", () => ServeApk("KickFlight-2.11.0-merged-DIAG-remote.apk"));
// Photon flavour (KF_PHOTON=1, KF_PHOTON_HOST=51.79.241.70): real 2-player rooms on the LuxonServer hosted at 51.79.241.70.
app.MapGet("/apk/photon-remote", () => ServeApk("KickFlight-2.11.0-photon-remote.apk"));
app.MapGet("/apk/photon-diag", () => ServeApk("KickFlight-2.11.0-photon-DIAG.apk"));
app.MapGet("/apk/photon-diag-remote", () => ServeApk("KickFlight-2.11.0-photon-DIAG-remote.apk"));
// Diagnostic uploads are private server-side files. Production sets Diagnostics__UploadDirectory to a dedicated
// writable bind mount; local runs fall back to .local/run. Nginx exposes only these POST routes.
app.MapGet("/diag", () => Results.Content("""
<!doctype html><meta name=viewport content="width=device-width,initial-scale=1">
<title>KF diag upload</title>
<body style="font-family:sans-serif;padding:24px;max-width:480px">
<h2>Kick-Flight diag upload</h2>
<form method=post action="/diag/upload-file" enctype="multipart/form-data">
<p><input type=file name=file required></p>
<p><input type=submit value="Upload" style="font-size:1.2em;padding:8px 24px"></p>
</form>
<p style="color:#666">Bug report: Ajustes &gt; Opciones de desarrollador &gt; "Crear informe de errores" (interactivo), luego
comparte el zip a Archivos/Descargas y subelo aqui. Max 200 MB.</p>
</body>
""", "text/html; charset=utf-8"));
app.MapPost("/diag/upload-file", async (HttpContext context) =>
{
    if (!context.Request.HasFormContentType) return Results.Json(new { error = "multipart form expected" }, statusCode: 400);
    IFormCollection form;
    try { form = await context.Request.ReadFormAsync(context.RequestAborted); }
    catch (InvalidDataException) { return Results.Json(new { error = "invalid multipart form" }, statusCode: 400); }
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.Json(new { error = "no file" }, statusCode: 400);
    const long max = 200L * 1024 * 1024;
    if (file.Length > max) return Results.Json(new { error = "too-large", max }, statusCode: 413);
    try
    {
        await using var source = file.OpenReadStream();
        var saved = await diagnosticUploads.SaveAsync(source, file.FileName, max, context.RequestAborted);
        return Results.Json(new { saved = saved.Name, bytes = saved.Bytes });
    }
    catch (DiagnosticUploadTooLargeException ex)
    {
        return Results.Json(new { error = "too-large", max = ex.MaxBytes }, statusCode: 413);
    }
    catch (DiagnosticUploadCapacityException ex)
    {
        return Results.Json(new { error = "diagnostic-storage-full", maxStoredBytes = ex.MaxBytes }, statusCode: 507);
    }
}).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(210L * 1024 * 1024));
app.MapPost("/diag/upload", async (HttpContext context) =>
{
    const long max = 4L * 1024 * 1024;
    try
    {
        var saved = await diagnosticUploads.SaveAsync(context.Request.Body, "logcat.txt", max, context.RequestAborted);
        return Results.Json(new { saved = saved.Name, bytes = saved.Bytes });
    }
    catch (DiagnosticUploadTooLargeException ex)
    {
        return Results.Json(new { error = "too-large", max = ex.MaxBytes }, statusCode: 413);
    }
    catch (DiagnosticUploadCapacityException ex)
    {
        return Results.Json(new { error = "diagnostic-storage-full", maxStoredBytes = ex.MaxBytes }, statusCode: 507);
    }
}).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(5L * 1024 * 1024));
app.MapGet("/health/photon", async (IPhotonServerManager photonManager, CancellationToken ct) =>
{
    var status = await photonManager.CheckHealthAsync(ct);
    return Results.Json(status, statusCode: (status.MasterReachable || !status.Enabled) ? 200 : 503);
});
app.MapGet("/health/ready", async (FixtureStore store, ResourceCatalogStore resourceStore, IPhotonServerManager photonManager, CancellationToken ct) =>
{
    var snapshot = store.GetSnapshot();
    var resourceSnapshot = resourceStore.GetSnapshot();
    var iosResourceSnapshot = resourceStore.GetIosSnapshot();
    var iosBootFixtureReady = store.GetIosBootFixture() is not null;
    var photonStatus = await photonManager.CheckHealthAsync(ct);
    var errors = snapshot.Errors.Concat(resourceSnapshot.Errors).Concat(iosResourceSnapshot.Errors).ToList();
    if (resourceStore.IsIosCatalogReady && !iosBootFixtureReady)
        errors.Add("The iOS platform-2 boot fixture is missing or invalid.");
    if (photonStatus.Enabled && !photonStatus.MasterReachable)
    {
        errors.Add($"Photon MasterServer unreachable at {photonStatus.Host}:{photonStatus.MasterServerPort}");
    }
    return errors.Count == 0
        ? Results.Json(new
        {
            status = "ready", fixtureCount = snapshot.Fixtures.Count,
            resourceCount = resourceSnapshot.Resources.Count,
            iosResourceCount = iosResourceSnapshot.Resources.Count,
            iosCatalogReady = resourceStore.IsIosCatalogReady,
            iosBootFixtureReady,
            photon = photonStatus
        })
        : Results.Json(new { status = "not-ready", errors, photon = photonStatus }, statusCode: 503);
});

app.MapMethods("/{**path}", new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" },
    async (HttpContext context, FixtureStore store, ResourceCatalogStore resourceStore, DemoSessionApi demoSessionApi,
        IOptions<HarnessOptions> options, ILoggerFactory loggerFactory) =>
    {
        var host = FixtureStore.NormalizeHost(context.Request.Host.Host);
        var firstParty = options.Value.FirstPartyHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
        var directClient = options.Value.DirectClientHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
            || (!options.Value.StrictMode && System.Net.IPAddress.TryParse(host, out _));
        if (!firstParty && !directClient)
        {
            return Results.Json(new { error = "local-host-not-allowed", host }, statusCode: 421);
        }

        var demoResult = await demoSessionApi.TryHandleAsync(context);
        if (demoResult is not null) return demoResult;

        var requestPath = context.Request.Path.Value ?? "/";
        var isIosList = requestPath.StartsWith($"/v1/list/{ResourceCatalogStore.IosAssetVersion}/", StringComparison.Ordinal);
        var isIosCdn = requestPath.StartsWith("/ios/cdn/", StringComparison.Ordinal);
        var isIosBoot = directClient && HttpMethods.IsPost(context.Request.Method) && requestPath == "/boot/index" &&
                        int.TryParse(context.Request.Headers["x-app-asset-platform"], out var platform) && platform == 2;
        if (directClient && (isIosBoot || isIosList || isIosCdn) &&
            (!resourceStore.IsIosCatalogReady || (isIosBoot && store.GetIosBootFixture() is null)))
        {
            context.Response.Headers["x-app-status-code"] = "1";
            return Results.Json(new { error = "ios-resource-catalog-unavailable", assetVersion = ResourceCatalogStore.IosAssetVersion },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            if (directClient && isIosList)
            {
                var fromRevisionText = requestPath[(requestPath.LastIndexOf('/') + 1)..];
                if (!int.TryParse(fromRevisionText, out var fromRevision) ||
                    fromRevision is < 0 or > ResourceCatalogStore.IosWireRevision)
                    return Results.Json(new { error = "unsupported-ios-catalog-revision" }, statusCode: StatusCodes.Status404NotFound);
                var database = IosOctoDatabase.Build(resourceStore.GetIosSnapshot(), fromRevision);
                return new LocalFixtureResult(database, "application/x-protobuf", StatusCodes.Status200OK);
            }
            var resource = directClient
                ? isIosCdn
                    ? resourceStore.MatchIosDirect(requestPath)
                    : resourceStore.MatchDirect(requestPath)
                : resourceStore.Match(host, requestPath);
            if (resource is not null)
            {
                context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
                context.Response.Headers.ETag = $"\"{resource.Definition.Sha256.ToLowerInvariant()}\"";
                loggerFactory.CreateLogger("ResourceRouting").LogInformation(
                    "request -> resource: {Method} {Host}{Path} -> {ResourceId} ({LogicalName})",
                    context.Request.Method, host, context.Request.Path, resource.Definition.Id, resource.Definition.LogicalName);
                return Results.File(resource.SourceFile, resource.Definition.ContentType, enableRangeProcessing: true);
            }
        }

        var fixture = directClient
            ? isIosBoot ? store.GetIosBootFixture() : store.MatchDirect(context.Request.Method, requestPath)
            : store.Match(host, context.Request.Method, requestPath);
        if (fixture is null)
        {
            loggerFactory.CreateLogger("FixtureRouting").LogWarning(
                "No local fixture for {Method} {Host}{Path}; upstream is disabled",
                context.Request.Method, host, context.Request.Path);
            return Results.Json(new
            {
                error = options.Value.StrictMode ? "local-fixture-missing" : "local-no-response",
                upstream = "disabled",
                host,
                method = context.Request.Method,
                path = context.Request.Path.Value
            }, statusCode: options.Value.StrictMode ? 404 : 501);
        }

        foreach (var header in fixture.Headers)
        {
            context.Response.Headers[header.Key] = header.Value;
        }

        loggerFactory.CreateLogger("FixtureRouting").LogInformation(
            "request -> fixture -> response: {Method} {Host}{Path} -> {FixtureId} -> {StatusCode}",
            context.Request.Method, host, context.Request.Path, fixture.Id, fixture.StatusCode);

        var body = fixture.GetBodyBytes();
        if (directClient && requestPath.StartsWith("/v1/list/", StringComparison.Ordinal))
        {
            // The Octo database carries the CDN url format (field 5). It is baked with the LAN address at build time,
            // but a client reaching us through another name (kickflightsg.ddns.net from outside) must download from
            // that same name, so rewrite the format to the host the request actually came through - or to the
            // configured external CDN when the bundles are hosted elsewhere.
            string? urlFormat;
            if (isIosList)
            {
                urlFormat = options.Value.IosOctoCdnUrlFormatsByHost.FirstOrDefault(pair =>
                    string.Equals(pair.Key, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase)).Value;
                if (string.IsNullOrWhiteSpace(urlFormat))
                    urlFormat = $"{context.Request.Scheme}://{context.Request.Host}/ios/cdn/{{o}}";
            }
            else
            {
                urlFormat = options.Value.OctoCdnUrlFormat;
                if (string.IsNullOrWhiteSpace(urlFormat))
                {
                    var hostFormat = options.Value.OctoCdnUrlFormatsByHost.FirstOrDefault(pair =>
                        string.Equals(pair.Key, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase)).Value;
                    urlFormat = string.IsNullOrWhiteSpace(hostFormat)
                        ? $"{context.Request.Scheme}://{context.Request.Host}/cdn/{{o}}"
                        : hostFormat;
                }
            }
            body = OctoDatabaseUrl.Rewrite(body, urlFormat);
        }
        return new LocalFixtureResult(body, fixture.ContentType, fixture.StatusCode);
    });

app.Run();

public partial class Program;

/// <summary>Replaces the top-level string field 5 (urlFormat) of an Octo.Proto.Database message.</summary>
public static class OctoDatabaseUrl
{
    public static byte[] Rewrite(byte[] message, string urlFormat)
    {
        var output = new List<byte>(message.Length + 64);
        var i = 0;
        while (i < message.Length)
        {
            var start = i;
            if (!TryReadVarint(message, ref i, out var key)) return message;
            var field = (int)(key >> 3);
            var wireType = (int)(key & 7);
            switch (wireType)
            {
                case 0: if (!TryReadVarint(message, ref i, out _)) return message; break;
                case 1: i += 8; break;
                case 5: i += 4; break;
                case 2:
                    if (!TryReadVarint(message, ref i, out var length)) return message;
                    i += (int)length;
                    break;
                default: return message; // unknown wire type: leave the message untouched
            }
            if (i > message.Length) return message;
            if (field != 5) output.AddRange(message[start..i]);
        }
        var text = System.Text.Encoding.UTF8.GetBytes(urlFormat);
        output.Add((5 << 3) | 2);
        WriteVarint(output, (ulong)text.Length);
        output.AddRange(text);
        return output.ToArray();
    }

    private static bool TryReadVarint(byte[] data, ref int index, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (index < data.Length && shift < 64)
        {
            var b = data[index++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    private static void WriteVarint(List<byte> output, ulong value)
    {
        while (value >= 0x80) { output.Add((byte)(value | 0x80)); value >>= 7; }
        output.Add((byte)value);
    }
}
