using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace KickFlight.BootstrapApi;

/// <summary>
/// What the adapter knows about badges: the ids the client draws, and how the catalog's rule kinds map onto the tenant's
/// otomo documents. It evaluates no rule. A badge is earned when a lifetime track in economy pays its reward class, and
/// owned when the honor:&lt;id&gt; item is in the player's inventory; see docs/OTOMO_BADGES.md.
/// </summary>
public static class BadgeCatalog
{
    public const int DefaultHonorId = 6010000;
    public const string CatalogPath = "config/badge_rule_catalog.json";

    /// <summary>
    /// The catalog kinds the tenant documents express as tracks (kf-otomo kf/tools/gen_badges.py). Every kind the old
    /// local evaluator executed is here; a catalog entry of an executable kind that is not, would be listed at startup.
    /// </summary>
    public static readonly IReadOnlySet<string> TranslatedKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "battle_count", "disc_upgrades", "login_days", "registration_complete", "rank", "wins_total", "kicker_wins",
        "mode_wins", "queue_wins", "win_streak", "mvp_count", "stat_total"
    };

    /// <summary>Kinds that never awarded anything: nothing to translate, and nothing the tenant could express.</summary>
    private static readonly IReadOnlySet<string> NotRuleKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "default", "unsupported"
    };

    /// <summary>
    /// Client HonorMaster rows (Colorful.HonorMasterData: honorType, name, title, sortOrder + id) built from the rule
    /// catalog. HonorType enum order from metadata: Invalid, Other, Battle, Kicker (1/2/3 assumed; medium confidence).
    /// Without this table the badge screen shows "0/0 collected".
    /// </summary>
    public static string BuildHonorMasterJson(string? catalogJson)
    {
        var rows = new List<object>();
        try
        {
            if (!string.IsNullOrWhiteSpace(catalogJson))
            {
                using var doc = JsonDocument.Parse(catalogJson);
                foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
                {
                    var id = e.GetProperty("id").GetInt32();
                    var kind = e.TryGetProperty("requirements", out var req) && req.TryGetProperty("kind", out var k)
                        ? k.GetString() : null;
                    var honorType = kind switch
                    {
                        "kicker_wins" => 3,
                        "default" or "registration_complete" or "login_days" or "disc_upgrades" => 1,
                        _ => 2,
                    };
                    var name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var sort = e.TryGetProperty("sortOrder", out var so) && so.TryGetInt32(out var sv) ? sv : id;
                    rows.Add(new { id, honorType, name, title = "", sortOrder = sort });
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            rows.Clear();
        }
        if (rows.Count == 0) rows.Add(new { id = DefaultHonorId, honorType = 1, name = "Kick-Flight", title = "", sortOrder = DefaultHonorId });
        return JsonSerializer.Serialize(rows);
    }

    /// <summary>The catalog's rule entries split into those the tenant documents express and those they do not.</summary>
    public sealed record Coverage(int Translated, IReadOnlyList<int> Unported, int NotRules);

    public static Coverage Measure(string? catalogJson)
    {
        var translated = 0;
        var notRules = 0;
        var unported = new List<int>();
        if (string.IsNullOrWhiteSpace(catalogJson)) return new Coverage(0, unported, 0);
        try
        {
            using var doc = JsonDocument.Parse(catalogJson);
            foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                var id = entry.GetProperty("id").GetInt32();
                var kind = entry.TryGetProperty("requirements", out var req) && req.TryGetProperty("kind", out var k)
                    ? k.GetString() : null;
                if (kind is null || NotRuleKinds.Contains(kind)) notRules++;
                else if (TranslatedKinds.Contains(kind)) translated++;
                else unported.Add(id);
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new Coverage(0, unported, 0);
        }
        return new Coverage(translated, unported, notRules);
    }

    /// <summary>Logs once, at startup, what the catalog asks for and what the tenant documents cannot yet express.</summary>
    public static void LogCoverage(ILogger logger, string? catalogJson)
    {
        var coverage = Measure(catalogJson);
        if (coverage.Unported.Count == 0)
        {
            logger.LogInformation(
                "Badges: {Translated} catalog rules are expressed as tenant documents (economy facts, lifetime tracks, honor items, " +
                "session display slot); none is unported; {NotRules} catalog entries are not rules. See docs/OTOMO_BADGES.md",
                coverage.Translated, coverage.NotRules);
            return;
        }
        logger.LogWarning(
            "Badges: {Translated} catalog rules are expressed as tenant documents; {Unported} are unported and will not award: " +
            "{Ids}. See docs/OTOMO_BADGES.md",
            coverage.Translated, coverage.Unported.Count, string.Join(", ", coverage.Unported));
    }
}
