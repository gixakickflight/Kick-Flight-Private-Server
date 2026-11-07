using System.Text;
using System.Text.Json;

namespace KickFlight.BootstrapApi.ChatTranslation;

/// <summary>Validates the model's JSON in C# (response-schema constraints such as minLength are not enforced upstream).</summary>
public static class ChatTranslationParser
{
    public const int MaxTranslationLength = 500;

    /// <summary>Returns the valid languages only; null when the payload is unusable. Missing/empty/oversized
    /// individual languages are dropped so readers of that language fall back to the original text.</summary>
    public static IReadOnlyDictionary<string, string>? Parse(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "empty"; return null; }
        var json = StripFence(text.Trim());
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { error = "malformed-json"; return null; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { error = "not-an-object"; return null; }
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var lang in ChatLanguages.All)
            {
                if (!doc.RootElement.TryGetProperty(lang, out var value) || value.ValueKind != JsonValueKind.String) continue;
                var s = Sanitize(value.GetString());
                if (s.Length == 0 || s.Length > MaxTranslationLength) continue;
                result[lang] = s;
            }
            if (result.Count == 0) { error = "no-valid-language"; return null; }
            return result;
        }
    }

    private static string Sanitize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '\r' or '\n' or '\t') sb.Append(' ');
            else if (!char.IsControl(ch)) sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    private static string StripFence(string s)
    {
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
        var firstNewline = s.IndexOf('\n');
        if (firstNewline < 0) return s;
        var body = s[(firstNewline + 1)..];
        var end = body.LastIndexOf("```", StringComparison.Ordinal);
        return (end >= 0 ? body[..end] : body).Trim();
    }
}
