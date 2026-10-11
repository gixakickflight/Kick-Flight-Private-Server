using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using KickFlight.BootstrapApi.Ops;

namespace KickFlight.BootstrapApi;

// Client telemetry, POST /analysis/index. Colorful.AnalysisManager sends
// AnalysisRequestData { analysisList: [ { key, value } ] } through the same encrypted, authenticated pipeline as
// any other request, so this handler runs after auth (from TryHandleStubAsync) and never changes the reply:
// whatever happens, the client gets the empty success object the stub answered with today.
//
// Where the entries go:
//   Every accepted entry is a client_events row of the ops telemetry (Ops/OpsTelemetryService): PostgreSQL in the
//   deployment, NDJSON under Ops:Directory otherwise, enriched with the device headers, the client address and the
//   battle context. That is the durable record.
//   Analysis:Directory (empty by default) additionally appends the raw entries to daily NDJSON files
//   (analysis-YYYYMMDD.ndjson). The old fallback - one structured Information line per entry
//   ("AnalysisTelemetry {Entry}"), lost with the container's log - is only used when the ops telemetry is
//   disabled (Ops:Enabled=false) and no directory is set or it cannot be written.
public sealed partial class DemoSessionApi
{
    private const int DefaultAnalysisEntriesPerMinute = 30;
    private const int DefaultAnalysisMaxBodyBytes = 256 * 1024;
    private const int DefaultAnalysisValueMaxBytes = 8 * 1024;

    private readonly object _analysisInitLock = new();
    private AnalysisTelemetrySink? _analysisSink;

    // Always answers with the stub's success payload and swallows everything: telemetry must never surface as a
    // client-visible error.
    private async Task<IResult?> HandleAnalysisIndexAsync(HttpContext context, SessionState state, byte[] key)
    {
        try
        {
            await IngestAnalysisAsync(context, state, key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Analysis telemetry ingest failed for {UserId}: {Error}", state.UserId, ex.Message);
        }
        return OkJson(context, key, "{}");
    }

    private async Task IngestAnalysisAsync(HttpContext context, SessionState state, byte[] key)
    {
        var sink = GetOrCreateAnalysisSink(context.RequestServices);
        var body = await ReadCappedBodyAsync(context.Request, sink.MaxBodyBytes, context.RequestAborted);
        if (body is null)
        {
            _logger.LogDebug("Dropped /analysis/index from {UserId}: body exceeds {MaxBytes} bytes", state.UserId, sink.MaxBodyBytes);
            return;
        }
        if (body.Length == 0) return;

        JsonDocument document;
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            document = JsonDocument.Parse(plaintext);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Malformed /analysis/index from {UserId}: {Error}", state.UserId, ex.Message);
            return;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("analysisList", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Malformed /analysis/index from {UserId}: analysisList missing", state.UserId);
                return;
            }

            var appVersion = context.Request.Headers["x-app-application-version"].ToString();
            var client = OpsClientInfo.FromRequest(context);
            var userName = state.HasName ? state.UserName : "";
            var utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var accepted = 0;
            var dropped = 0;
            var malformed = 0;

            foreach (var entry in list.EnumerateArray())
            {
                if (!TryReadAnalysisEntry(entry, out var entryKey, out var entryValue))
                {
                    malformed++;
                    continue;
                }
                if (!sink.TryConsume(state.UserId))
                {
                    dropped++;
                    continue;
                }
                sink.Write(BuildAnalysisLine(utc, state.UserId, userName, appVersion, entryKey, entryValue, sink.ValueMaxBytes),
                    logFallback: !_ops.Enabled);
                _ops.RecordAnalysis(state.UserId, entryKey, TruncateUtf8(entryValue, sink.ValueMaxBytes), client);
                accepted++;
            }

            if (dropped > 0)
            {
                _logger.LogDebug("Dropped {Dropped} /analysis/index entries over the {Limit}/minute limit for {UserId}",
                    dropped, sink.EntriesPerMinute, state.UserId);
            }
            if (malformed > 0)
            {
                _logger.LogWarning("Malformed /analysis/index from {UserId}: {Count} entries without a key", state.UserId, malformed);
            }
        }
    }

    private AnalysisTelemetrySink GetOrCreateAnalysisSink(IServiceProvider services)
    {
        var existing = _analysisSink;
        if (existing is not null) return existing;
        lock (_analysisInitLock)
        {
            _analysisSink ??= AnalysisTelemetrySink.Create(services.GetRequiredService<IConfiguration>(), _contentRoot, _logger);
            return _analysisSink;
        }
    }

    private static bool TryReadAnalysisEntry(JsonElement entry, out string key, out string value)
    {
        key = "";
        value = "";
        if (entry.ValueKind != JsonValueKind.Object) return false;
        if (!entry.TryGetProperty("key", out var keyProp)) return false;
        key = keyProp.ValueKind == JsonValueKind.String ? keyProp.GetString() ?? "" : keyProp.GetRawText();
        if (string.IsNullOrEmpty(key)) return false;
        if (entry.TryGetProperty("value", out var valueProp))
        {
            value = valueProp.ValueKind == JsonValueKind.String ? valueProp.GetString() ?? "" : valueProp.GetRawText();
        }
        return true;
    }

    private static string BuildAnalysisLine(string utc, string userId, string userName, string appVersion,
        string key, string value, int valueMaxBytes) =>
        JsonSerializer.Serialize(new
        {
            utc,
            userId,
            userName,
            appVersion,
            key,
            value = TruncateUtf8(value, valueMaxBytes)
        });

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (maxBytes <= 0) return "";
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = maxBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80) length--;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    // The global Kestrel cap is 256 MB for bug-report uploads, which is far too much for one telemetry POST.
    // Returns null when the body is over the cap; the caller drops the whole request and still answers success.
    private static async Task<byte[]?> ReadCappedBodyAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength is long contentLength && contentLength > maxBytes) return null;
        request.EnableBuffering();
        request.Body.Position = 0;
        using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes) return null;
                buffer.Write(rented, 0, read);
            }
            return buffer.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            request.Body.Position = 0;
        }
    }

    // One sink per server process: the rate-limit windows and the file lock have to be shared across requests.
    private sealed class AnalysisTelemetrySink
    {
        private readonly string _directory;
        private readonly ILogger _logger;
        private readonly object _fileLock = new();
        private readonly ConcurrentDictionary<string, RateWindow> _windows = new(StringComparer.Ordinal);
        private bool _fileModeUnavailable;

        private AnalysisTelemetrySink(string directory, int entriesPerMinute, int maxBodyBytes, int valueMaxBytes, ILogger logger)
        {
            _directory = directory;
            EntriesPerMinute = entriesPerMinute;
            MaxBodyBytes = maxBodyBytes;
            ValueMaxBytes = valueMaxBytes;
            _logger = logger;
        }

        public int EntriesPerMinute { get; }
        public int MaxBodyBytes { get; }
        public int ValueMaxBytes { get; }

        public static AnalysisTelemetrySink Create(IConfiguration configuration, string contentRoot, ILogger logger)
        {
            var section = configuration.GetSection("Analysis");
            var directory = section.GetValue<string>("Directory") ?? "";
            if (!string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.IsPathRooted(directory)
                    ? directory
                    : Path.GetFullPath(Path.Combine(contentRoot, directory));
            }
            return new AnalysisTelemetrySink(
                directory,
                Math.Max(1, section.GetValue("EntriesPerMinute", DefaultAnalysisEntriesPerMinute)),
                Math.Max(1024, section.GetValue("MaxBodyBytes", DefaultAnalysisMaxBodyBytes)),
                Math.Max(0, section.GetValue("ValueMaxBytes", DefaultAnalysisValueMaxBytes)),
                logger);
        }

        public bool TryConsume(string userId)
        {
            var now = DateTime.UtcNow;
            var window = _windows.GetOrAdd(userId, _ => new RateWindow());
            lock (window)
            {
                if (now - window.Start >= TimeSpan.FromMinutes(1))
                {
                    window.Start = now;
                    window.Count = 0;
                }
                if (window.Count >= EntriesPerMinute) return false;
                window.Count++;
                return true;
            }
        }

        public void Write(string line, bool logFallback)
        {
            if (!string.IsNullOrEmpty(_directory) && !_fileModeUnavailable && TryAppendToFile(line)) return;
            if (logFallback) _logger.LogInformation("AnalysisTelemetry {Entry}", line);
        }

        private bool TryAppendToFile(string line)
        {
            try
            {
                lock (_fileLock)
                {
                    Directory.CreateDirectory(_directory);
                    var path = Path.Combine(_directory, $"analysis-{DateTime.UtcNow:yyyyMMdd}.ndjson");
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                }
                return true;
            }
            catch (Exception ex)
            {
                lock (_fileLock)
                {
                    if (!_fileModeUnavailable)
                    {
                        _fileModeUnavailable = true;
                        _logger.LogWarning("Analysis telemetry directory {Directory} is not writable ({Error}); using log lines", _directory, ex.Message);
                    }
                }
                return false;
            }
        }

        private sealed class RateWindow
        {
            public DateTime Start { get; set; } = DateTime.UtcNow;
            public int Count { get; set; }
        }
    }
}
