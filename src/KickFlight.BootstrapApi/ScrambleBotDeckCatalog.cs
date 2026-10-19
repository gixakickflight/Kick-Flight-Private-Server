using System.Text.Json;

namespace KickFlight.BootstrapApi;

internal static class ScrambleBotDeckCatalog
{
    private const string RelativePath = "config/scramble_bot_disc_decks.json";

    internal static IReadOnlyList<ScrambleBotDeck> Load(string contentRoot, ILogger logger)
    {
        var path = Path.Combine(contentRoot, RelativePath);
        if (!File.Exists(path))
        {
            var outputPath = Path.Combine(AppContext.BaseDirectory, RelativePath);
            if (File.Exists(outputPath)) path = outputPath;
            else
            {
                logger.LogWarning("Scramble bot deck catalog is missing at {Path}; legacy bot decks remain active", path);
                return [];
            }
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("decks", out var deckRows)
                || deckRows.ValueKind != JsonValueKind.Array)
            {
                logger.LogWarning("Scramble bot deck catalog at {Path} has no decks array; legacy bot decks remain active", path);
                return [];
            }

            var decks = new List<ScrambleBotDeck>();
            var seenIds = new HashSet<int>();
            foreach (var row in deckRows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (!TryReadInt(row, "kickerId", out var kickerId)
                    || !TryReadInt(row, "deckId", out var deckId)
                    || !TryReadInt(row, "variant", out var variant)
                    || !TryReadInt(row, "roleType", out var roleType)
                    || !row.TryGetProperty("profile", out var profileValue)
                    || profileValue.ValueKind != JsonValueKind.String
                    || !row.TryGetProperty("discIds", out var discValues)
                    || discValues.ValueKind != JsonValueKind.Array)
                    continue;

                var profile = profileValue.GetString() ?? "";
                var discIds = discValues.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _))
                    .Select(value => value.GetInt32()).ToArray();
                if (kickerId is < 1 or > 14 || deckId <= 0 || variant is < 1 or > 4 || seenIds.Contains(deckId)
                    || roleType is < 0 or > 3 || roleType != ScrambleBotCompositionSelector.RoleForKicker(kickerId)
                    || profile is not ("carrier" or "hunter" or "objective_attacker" or "healer"
                        or "speed_support" or "trap_defender" or "damage_support" or "tank_defender")
                    || discIds.Length != 4 || discIds.Any(id => id <= 0) || discIds.Distinct().Count() != 4)
                    continue;

                if (!row.TryGetProperty("castTimes", out var castValues) || castValues.ValueKind != JsonValueKind.Object)
                    continue;
                var castTimes = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var category in castValues.EnumerateObject())
                {
                    if (category.Value.ValueKind != JsonValueKind.Number || !category.Value.TryGetDouble(out var seconds)
                        || seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
                    {
                        castTimes.Clear();
                        break;
                    }
                    castTimes[category.Name] = seconds;
                }
                if (castTimes.Count != 9) continue;
                seenIds.Add(deckId);
                decks.Add(new ScrambleBotDeck(kickerId, deckId, profile, variant, roleType, castTimes, discIds));
            }

            if (decks.Count == 0)
                logger.LogWarning("Scramble bot deck catalog at {Path} has no valid rows; legacy bot decks remain active", path);
            else
                logger.LogInformation("Loaded {DeckCount} validated Scramble bot deck variants from {Path}", decks.Count, path);
            return decks;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not load Scramble bot deck catalog at {Path}; legacy bot decks remain active", path);
            return [];
        }
    }

    private static bool TryReadInt(JsonElement row, string name, out int value)
    {
        value = 0;
        return row.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }
}
