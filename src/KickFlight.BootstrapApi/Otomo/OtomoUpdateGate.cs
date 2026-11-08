using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi.Otomo;

public sealed class OtomoUpdateGateOptions
{
    public const string SectionName = "Otomo:UpdateGate";

    // ops' GET /internal/ops/snapshot (e.g. http://10.169.251.183:18098/internal/ops/snapshot). Empty: the gate is
    // not otomo's, and ClientUpdateState keeps its own minimum.
    public string SnapshotUrl { get; set; } = "";

    // The game_server key ops accepts (deploy/secrets/service_keys/ops_game_server.key on the otomo host).
    public string ServiceKeyPath { get; set; } = "";

    // The release channel whose gate this game's players meet: the one its economy and masters follow.
    public string Channel { get; set; } = "live";

    // off | warn | enforce, as GATE_ENFORCE_CLIENT_VERSION: warn logs the refusal it would make and lets the client in.
    public string Mode { get; set; } = "enforce";

    public int RefreshSeconds { get; set; } = 15;
}

// The update gate, as otomo's ops states it. The KF client speaks neither the X-Otomo-* headers nor the 426 the gateway
// answers, so this adapter enforces the gate for it: it polls the same snapshot the gateway, auth and session hold
// (role game_server, an optional reader of ops' snapshot) and applies the same rule as gatepolicy.Evaluate's update
// step - the channel's release block (client_update.min_version[platform], else its default, else the release's
// min_client_version), raised by an enforcing operator gate for the channel and platform (else "default") when that
// gate asks more. The console's Update gate page and a release's client_update block are therefore the only places
// a minimum is set; the adapter's own /admin/client-update is refused while this is on.
//
// A snapshot is held until a newer one arrives (ETag), so an ops outage keeps the last gate. Before the first snapshot
// nothing is refused: the gateway does the same when its snapshot is cold (gatepolicy ResultUnchecked).
public sealed class OtomoUpdateGate : BackgroundService
{
    private static readonly Regex VersionRe = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);

    private readonly OtomoUpdateGateOptions _options;
    private readonly ILogger<OtomoUpdateGate> _logger;
    private readonly HttpClient? _http;
    private volatile GateSnapshot? _current;
    private string? _etag;

    public OtomoUpdateGate(IOptions<OtomoUpdateGateOptions> options, ILogger<OtomoUpdateGate> logger)
        : this(options.Value, logger, null)
    {
    }

    internal OtomoUpdateGate(OtomoUpdateGateOptions options, ILogger<OtomoUpdateGate> logger, HttpMessageHandler? handler)
    {
        _options = options;
        _logger = logger;
        Enabled = !string.IsNullOrWhiteSpace(options.SnapshotUrl);
        if (Enabled) _http = handler is null ? new HttpClient() : new HttpClient(handler);
        if (_http is not null) _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public bool Enabled { get; }
    public bool Warns => string.Equals(_options.Mode, "warn", StringComparison.OrdinalIgnoreCase);
    public bool Off => string.Equals(_options.Mode, "off", StringComparison.OrdinalIgnoreCase);
    public long Revision => _current?.Revision ?? 0;

    // For a test, or a caller that already holds a snapshot body.
    internal void Use(string snapshotJson) => _current = JsonSerializer.Deserialize<GateSnapshot>(snapshotJson);

    public sealed record Decision(bool Outdated, string Minimum, string Message);

    // platform is "android" or "ios"; language the client's x-app-language ("en", "ja", ...).
    public Decision Check(string? version, string platform, string? language)
    {
        var snapshot = _current;
        if (!Enabled || Off || snapshot?.Update is null) return new Decision(false, "", "");

        var channel = snapshot.Update.Channels is not null && snapshot.Update.Channels.TryGetValue(_options.Channel, out var c)
            ? c
            : null;
        var block = channel?.ClientUpdate;
        var minimum = Minimum(block, channel?.MinClientVersion ?? "", platform);
        var messages = block?.Message;

        var gate = OperatorGate(snapshot.Update.Gates, _options.Channel, platform);
        if (gate is not null && (minimum.Length == 0 || Compare(gate.MinVersion ?? "", minimum) > 0))
        {
            minimum = gate.MinVersion ?? "";
            if (gate.Message is { Count: > 0 }) messages = gate.Message;
        }

        if (minimum.Length == 0) return new Decision(false, "", "");
        var have = (version ?? "").Trim();
        var outdated = have.Length == 0 || !VersionRe.IsMatch(have) || Compare(have, minimum) < 0;
        return new Decision(outdated, minimum, outdated ? Localise(messages, language) : "");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled || _http is null) return;
        _logger.LogInformation("Update gate: otomo's ({Url}, channel {Channel}, mode {Mode})", _options.SnapshotUrl,
            _options.Channel, _options.Mode);
        var refresh = TimeSpan.FromSeconds(Math.Max(2, _options.RefreshSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnceAsync(stoppingToken);
            try { await Task.Delay(refresh, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.SnapshotUrl);
            var key = (await File.ReadAllTextAsync(_options.ServiceKeyPath, cancellationToken)).Trim();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.TryAddWithoutValidation("X-Otomo-Instance", Environment.MachineName);
            if (_etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
            using var response = await _http!.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified) return;
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Update gate: ops snapshot answered {Status}; keeping revision {Revision}",
                    (int)response.StatusCode, Revision);
                return;
            }
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var snapshot = JsonSerializer.Deserialize<GateSnapshot>(body);
            if (snapshot is null || snapshot.Schema != 1 || snapshot.Revision < 1)
            {
                _logger.LogWarning("Update gate: ops snapshot is not schema 1 with a revision; keeping revision {Revision}", Revision);
                return;
            }
            var previous = Revision;
            _current = snapshot;
            _etag = response.Headers.ETag?.ToString();
            if (snapshot.Revision != previous)
            {
                _logger.LogInformation("Update gate: snapshot revision {Revision}, android minimum {Android}, ios minimum {Ios}",
                    snapshot.Revision, Check("0.0.0", "android", null).Minimum, Check("0.0.0", "ios", null).Minimum);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
                                              or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Update gate: ops snapshot unreachable ({Error}); keeping revision {Revision}",
                exception.Message, Revision);
        }
    }

    // clientgate.Minimum: the block's entry for the platform, else its default, else the release's min_client_version.
    private static string Minimum(ClientUpdateBlock? block, string minClientVersion, string platform)
    {
        if (block is null) return minClientVersion;
        var versions = block.MinVersion ?? new Dictionary<string, string>();
        return versions.TryGetValue(platform, out var exact) ? exact : versions.GetValueOrDefault("default") ?? "";
    }

    // gatepolicy.operatorGate: the enforcing gate for the channel and platform, else the channel's "default" one.
    private static OperatorGateRow? OperatorGate(IReadOnlyList<OperatorGateRow>? gates, string channel, string platform)
    {
        OperatorGateRow? fallback = null;
        foreach (var gate in gates ?? [])
        {
            if (gate.Channel != channel || !gate.Enforce) continue;
            if (gate.Platform == platform) return gate;
            if (gate.Platform == "default") fallback = gate;
        }
        return fallback;
    }

    // clientgate.Compare: three numeric parts, a missing part is 0.
    internal static int Compare(string a, string b)
    {
        var ap = a.Split('.');
        var bp = b.Split('.');
        for (var i = 0; i < 3; i++)
        {
            ulong.TryParse(i < ap.Length ? ap[i] : "0", out var x);
            ulong.TryParse(i < bp.Length ? bp[i] : "0", out var y);
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    // clientgate.Localise for one language tag: the exact tag, then its primary language, then "default".
    private static string Localise(IReadOnlyDictionary<string, string>? messages, string? language)
    {
        if (messages is null || messages.Count == 0) return "";
        var tag = (language ?? "").Trim();
        foreach (var candidate in new[] { tag, tag.Split('-', '_')[0] })
        {
            if (candidate.Length == 0) continue;
            foreach (var (key, value) in messages)
            {
                if (key != "default" && string.Equals(key, candidate, StringComparison.OrdinalIgnoreCase)) return value;
            }
        }
        return messages.GetValueOrDefault("default") ?? "";
    }

    private sealed record GateSnapshot(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("revision")] long Revision,
        [property: JsonPropertyName("update")] UpdateSection? Update);

    private sealed record UpdateSection(
        [property: JsonPropertyName("channels")] Dictionary<string, ChannelRow>? Channels,
        [property: JsonPropertyName("gates")] List<OperatorGateRow>? Gates);

    private sealed record ChannelRow(
        [property: JsonPropertyName("min_client_version")] string? MinClientVersion,
        [property: JsonPropertyName("client_update")] ClientUpdateBlock? ClientUpdate);

    private sealed record ClientUpdateBlock(
        [property: JsonPropertyName("min_version")] Dictionary<string, string>? MinVersion,
        [property: JsonPropertyName("message")] Dictionary<string, string>? Message);

    private sealed record OperatorGateRow(
        [property: JsonPropertyName("channel")] string Channel,
        [property: JsonPropertyName("platform")] string Platform,
        [property: JsonPropertyName("min_version")] string? MinVersion,
        [property: JsonPropertyName("message")] Dictionary<string, string>? Message,
        [property: JsonPropertyName("enforce")] bool Enforce);
}
