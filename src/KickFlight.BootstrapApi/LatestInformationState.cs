using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KickFlight.BootstrapApi;

public sealed record LatestInformationSnapshot(
    string Content,
    DateTime ChangedAtUtc,
    string Mode = "text",
    string HtmlContent = "",
    string ExternalUrl = "");

/// <summary>
/// Editable announcements shown by the client's Latest Information WebView. Text is rendered as escaped HTML;
/// operator-supplied HTML is served verbatim under a restrictive browser sandbox policy.
/// </summary>
public sealed class LatestInformationState
{
    public const int MaximumContentLength = 12000;
    public const int MaximumHtmlBytes = 1024 * 1024;
    public const int MaximumExternalUrlLength = 2048;
    public const string HtmlResourceContentSecurityPolicy =
        "default-src https: http: data: blob:; " +
        "script-src 'unsafe-inline' https: http: data: blob:; " +
        "style-src 'unsafe-inline' https: http: data: blob:; " +
        "img-src https: http: data: blob:; " +
        "font-src https: http: data: blob:; " +
        "connect-src https: http: data: blob:; " +
        "media-src https: http: data: blob:; " +
        "frame-src https: http:; " +
        "object-src 'none'; base-uri 'none'; form-action https: http:";
    public const string HtmlContentSecurityPolicy =
        "sandbox allow-scripts allow-forms allow-popups allow-top-navigation-by-user-activation; " + HtmlResourceContentSecurityPolicy;
    public const string DefaultContent = """
**Bienvenido a Fenix**
Este es un servidor privado de pruebas. El progreso es local y puede reiniciarse sin aviso.

**Tienda y gacha**
La tienda y el gacha todavia no estan disponibles; llegaran en una proxima actualizacion.
""";

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true };
    private static readonly Regex InlinePattern = new(
        @"(?<bold>\*\*(?<boldText>.+?)\*\*)|(?<code>`(?<codeText>[^`]+)`)|(?<url>https?://[^\s<>]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ILogger<LatestInformationState> _logger;
    private readonly object _lock = new();
    private LatestInformationSnapshot _current;

    public LatestInformationState(ILogger<LatestInformationState> logger, IWebHostEnvironment environment, IConfiguration configuration)
        : this(logger, configuration["LatestInformation:FilePath"] is { Length: > 0 } configured
            ? configured
            : Path.Combine(environment.ContentRootPath, "data", "latest-information.json"))
    {
    }

    public LatestInformationState(ILogger<LatestInformationState> logger, string filePath)
    {
        _logger = logger;
        FilePath = Path.GetFullPath(filePath);
        _current = new LatestInformationSnapshot(DefaultContent, DateTime.UtcNow);
        Load();
    }

    public string FilePath { get; }

    public LatestInformationSnapshot Current
    {
        get { lock (_lock) return _current; }
    }

    public LatestInformationSnapshot Set(string? content)
    {
        var normalized = content ?? "";
        if (normalized.Length > MaximumContentLength)
            throw new ArgumentOutOfRangeException(nameof(content), $"content must be {MaximumContentLength} characters or fewer");

        LatestInformationSnapshot next;
        lock (_lock)
        {
            next = _current with
            {
                Content = normalized.Replace("\r\n", "\n").Replace('\r', '\n'),
                Mode = "text",
                ChangedAtUtc = DateTime.UtcNow
            };
            Save(next);
            _current = next;
        }
        _logger.LogInformation("Latest information updated ({Length} characters)", next.Content.Length);
        return next;
    }

    public LatestInformationSnapshot SetHtml(string? htmlContent)
    {
        var html = htmlContent ?? "";
        if (Encoding.UTF8.GetByteCount(html) > MaximumHtmlBytes)
            throw new ArgumentOutOfRangeException(nameof(htmlContent), $"HTML must be {MaximumHtmlBytes} UTF-8 bytes or fewer");

        LatestInformationSnapshot next;
        lock (_lock)
        {
            next = _current with { HtmlContent = html, Mode = "html", ChangedAtUtc = DateTime.UtcNow };
            Save(next);
            _current = next;
        }
        _logger.LogInformation("Latest information HTML updated ({Bytes} UTF-8 bytes)", Encoding.UTF8.GetByteCount(html));
        return next;
    }

    public LatestInformationSnapshot SetExternalUrl(string? externalUrl)
    {
        if (!TryNormalizeExternalUrl(externalUrl, out var normalizedUrl))
            throw new ArgumentException("URL must be an absolute HTTP or HTTPS URL without embedded credentials", nameof(externalUrl));

        LatestInformationSnapshot next;
        lock (_lock)
        {
            next = _current with { ExternalUrl = normalizedUrl, Mode = "url", ChangedAtUtc = DateTime.UtcNow };
            Save(next);
            _current = next;
        }
        _logger.LogInformation("Latest information external URL updated");
        return next;
    }

    public static bool IsValidExternalUrl(string? value) => TryNormalizeExternalUrl(value, out _);

    public static bool TryNormalizeExternalUrl(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (trimmed.Length > MaximumExternalUrlLength || trimmed.Any(char.IsControl)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo))
            return false;

        normalized = parsed.AbsoluteUri;
        return normalized.Length <= MaximumExternalUrlLength;
    }

    public IResult Serve(HttpContext context)
    {
        var snapshot = Current;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (snapshot.Mode == "url") return Results.Redirect(snapshot.ExternalUrl, permanent: false);
        if (snapshot.Mode == "html")
        {
            context.Response.Headers["Content-Security-Policy"] = HtmlContentSecurityPolicy;
            return Results.Content(snapshot.HtmlContent, "text/html; charset=utf-8");
        }

        return Results.Content(BuildTextHtml(snapshot.Content), "text/html; charset=utf-8");
    }

    public string BuildHtml()
    {
        var content = Current.Content;
        return BuildTextHtml(content);
    }

    private static string BuildTextHtml(string content)
    {
        var body = RenderContent(content);
        if (body.Length == 0) body = "<p>No hay avisos nuevos.</p>";
        return """
<!doctype html><html lang="es"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Latest Information</title><body style="font-family:sans-serif;padding:24px;max-width:640px;margin:auto;line-height:1.5;color:#222">
<h2>Latest Information</h2>
""" + body + "</body></html>";
    }

    private static string RenderContent(string content)
    {
        var output = new StringBuilder();
        var paragraph = new List<string>();
        var inList = false;
        void CloseParagraph()
        {
            if (paragraph.Count == 0) return;
            output.Append("<p>").Append(string.Join("<br>", paragraph.Select(RenderInline))).Append("</p>");
            paragraph.Clear();
        }
        void CloseList()
        {
            if (!inList) return;
            output.Append("</ul>");
            inList = false;
        }

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                CloseParagraph();
                CloseList();
                continue;
            }
            var heading = Regex.Match(line, @"^(#{1,3})\s+(.+)$");
            if (heading.Success)
            {
                CloseParagraph();
                CloseList();
                var level = Math.Min(heading.Groups[1].Value.Length + 1, 4);
                output.Append("<h").Append(level).Append('>').Append(RenderInline(heading.Groups[2].Value))
                    .Append("</h").Append(level).Append('>');
                continue;
            }
            var bullet = Regex.Match(line, @"^\s*[-*]\s+(.+)$");
            if (bullet.Success)
            {
                CloseParagraph();
                if (!inList)
                {
                    output.Append("<ul>");
                    inList = true;
                }
                output.Append("<li>").Append(RenderInline(bullet.Groups[1].Value)).Append("</li>");
                continue;
            }
            CloseList();
            paragraph.Add(line);
        }
        CloseParagraph();
        CloseList();
        return output.ToString();
    }

    private static string RenderInline(string text)
    {
        var output = new StringBuilder();
        var last = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            output.Append(WebUtility.HtmlEncode(text[last..match.Index]));
            if (match.Groups["bold"].Success)
                output.Append("<strong>").Append(WebUtility.HtmlEncode(match.Groups["boldText"].Value)).Append("</strong>");
            else if (match.Groups["code"].Success)
                output.Append("<code>").Append(WebUtility.HtmlEncode(match.Groups["codeText"].Value)).Append("</code>");
            else
            {
                var url = match.Groups["url"].Value;
                if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
                    output.Append("<a href=\"").Append(WebUtility.HtmlEncode(url))
                        .Append("\" target=\"_blank\" rel=\"noopener noreferrer\">")
                        .Append(WebUtility.HtmlEncode(url)).Append("</a>");
                else output.Append(WebUtility.HtmlEncode(url));
            }
            last = match.Index + match.Length;
        }
        output.Append(WebUtility.HtmlEncode(text[last..]));
        return output.ToString();
    }

    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<LatestInformationSnapshot>(File.ReadAllText(FilePath), FileJson);
            if (loaded is not null && loaded.Content is { Length: <= MaximumContentLength })
            {
                var htmlContent = loaded.HtmlContent ?? "";
                var externalUrl = loaded.ExternalUrl ?? "";
                var mode = loaded.Mode is "text" or "html" or "url" ? loaded.Mode : "text";
                if (Encoding.UTF8.GetByteCount(htmlContent) > MaximumHtmlBytes)
                {
                    mode = mode == "html" ? "text" : mode;
                    htmlContent = "";
                }
                if (!IsValidExternalUrl(externalUrl) && mode == "url") mode = "text";
                _current = loaded with { Mode = mode, HtmlContent = htmlContent, ExternalUrl = externalUrl };
            }
            _logger.LogInformation("Loaded latest information from {Path}", FilePath);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not read latest information state {Path}: {Error}", FilePath, ex.Message);
        }
    }

    private void Save(LatestInformationSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, FileJson));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not persist latest information to {Path}", FilePath);
            throw new InvalidOperationException("Could not persist latest information", ex);
        }
    }
}

/// <summary>GET/POST /admin/latest-information, limited to API loopback callers as for maintenance.</summary>
public static class LatestInformationAdmin
{
    public static IResult Get(HttpContext context, LatestInformationState state) =>
        MaintenanceAdmin.IsLocalAdmin(context) ? Status(state.Current) : Results.NotFound();

    public static async Task<IResult> PostAsync(HttpContext context, LatestInformationState state)
    {
        if (!MaintenanceAdmin.IsLocalAdmin(context)) return Results.NotFound();
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "body must be a JSON object" });
            if (!root.TryGetProperty("mode", out var modeProperty))
            {
                if (root.TryGetProperty("content", out var legacyProperty) && legacyProperty.ValueKind == JsonValueKind.String)
                {
                    var legacyContent = legacyProperty.GetString() ?? "";
                    if (legacyContent.Length > LatestInformationState.MaximumContentLength)
                        return Results.BadRequest(new { error = $"content must be {LatestInformationState.MaximumContentLength} characters or fewer" });
                    return Status(state.Set(legacyContent));
                }
                return Results.BadRequest(new { error = "body must include mode: text, html, or url" });
            }
            if (modeProperty.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new { error = "mode must be text, html, or url" });

            switch (modeProperty.GetString())
            {
                case "text":
                    if (!root.TryGetProperty("content", out var contentProperty) || contentProperty.ValueKind != JsonValueKind.String)
                        return Results.BadRequest(new { error = "text mode requires a string content" });
                    var content = contentProperty.GetString() ?? "";
                    if (content.Length > LatestInformationState.MaximumContentLength)
                        return Results.BadRequest(new { error = $"content must be {LatestInformationState.MaximumContentLength} characters or fewer" });
                    return Status(state.Set(content));
                case "html":
                    if (!root.TryGetProperty("htmlContent", out var htmlProperty) || htmlProperty.ValueKind != JsonValueKind.String)
                        return Results.BadRequest(new { error = "html mode requires a string htmlContent" });
                    var html = htmlProperty.GetString() ?? "";
                    if (Encoding.UTF8.GetByteCount(html) > LatestInformationState.MaximumHtmlBytes)
                        return Results.BadRequest(new { error = $"HTML must be {LatestInformationState.MaximumHtmlBytes} UTF-8 bytes or fewer" });
                    return Status(state.SetHtml(html));
                case "url":
                    if (!root.TryGetProperty("externalUrl", out var urlProperty) || urlProperty.ValueKind != JsonValueKind.String)
                        return Results.BadRequest(new { error = "URL mode requires a string externalUrl" });
                    var url = urlProperty.GetString() ?? "";
                    if (!LatestInformationState.TryNormalizeExternalUrl(url, out var normalizedUrl))
                        return Results.BadRequest(new { error = "URL must be an absolute HTTP or HTTPS URL without embedded credentials" });
                    return Status(state.SetExternalUrl(normalizedUrl));
                default:
                    return Results.BadRequest(new { error = "mode must be text, html, or url" });
            }
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "body must be valid Latest Information JSON", detail = ex.Message });
        }
        catch (InvalidOperationException)
        {
            return Results.Json(new { error = "could not persist latest information; the previous announcement is still active" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult Status(LatestInformationSnapshot current) => Results.Json(new
    {
        content = current.Content,
        mode = current.Mode,
        htmlContent = current.HtmlContent,
        externalUrl = current.ExternalUrl,
        htmlResourceCsp = LatestInformationState.HtmlResourceContentSecurityPolicy,
        defaultContent = LatestInformationState.DefaultContent,
        changedAtUtc = current.ChangedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
    });
}
