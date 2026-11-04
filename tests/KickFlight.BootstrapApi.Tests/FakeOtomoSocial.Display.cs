using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace KickFlight.BootstrapApi.Tests;

// The display half of the session fake (services/session/internal/api/display.go):
//
//   GET    /api/player/session/display        every slot the caller has set, as {slot: item_id}; {} when none
//   PUT    /api/player/session/display/{slot} {"item_id"}: the whole map afterwards. 400 slot_unknown (a slot the
//                                             tenant's session.rules does not declare), 400 invalid_body, 404
//                                             item_unknown (economy's catalogue lacks it), 422 item_not_displayable
//                                             (the item's display_slot is absent or another slot), 403 item_not_owned
//   DELETE /api/player/session/display/{slot} the whole map afterwards; clearing an empty slot is a 200
//
// The slots are session.rules display.slots of the Kick-Flight tenant (the seed's title and frame, plus honor). The
// catalogue and the ownership read are economy's: `Items` answers whether an id is in the catalogue and which slot it
// may fill, `Owns` whether the account holds it, and the suite wires Owns to the economy fake like the chat fake's.
public sealed partial class FakeOtomoSocial
{
    public static readonly string[] DisplaySlots = ["title", "frame", "honor"];

    // Whether the account holds the item. Wired by OtomoTestEnvironment to the economy fake's inventory.
    public Func<string, string, bool> Holds { get; set; } = (_, _) => false;

    // The catalogue: null when the id is not in it, otherwise the slot the item may be shown in ("" for none). The
    // honor:<id> items are displayable in honor; the other kinds this suite grants exist but are never displayed.
    public static string? DisplaySlotOf(string item)
    {
        var kind = item.Split(':', 2)[0];
        return kind switch
        {
            "honor" => "honor",
            "disc" or "skin" or "stamp" => "",
            _ => null
        };
    }

    // The map session holds for an account.
    public IReadOnlyDictionary<string, string> DisplayOf(string accountId)
    {
        var account = Ensure(accountId);
        lock (_lock) return new Dictionary<string, string>(account.Display);
    }

    // Puts an item in an account's slot directly (not through the ownership check), for a test that needs another player
    // to be showing something.
    public void SeedDisplay(string accountId, string slot, string item)
    {
        var account = Ensure(accountId);
        lock (_lock) account.Display[slot] = item;
    }

    // session.rules `names` of the Kick-Flight tenant (kf-otomo session.rules.json): 1 to 13 characters, none of them a
    // control character, a zero-width joiner or beyond the BMP, and unique among players (case-insensitive, NFC).
    // Applied after trimming, as session applies it.
    public const int NameMaxLength = 13;
    public const bool NameUnique = true;

    // The comparison key session uses for `unique`: NFC, then case-folded.
    public static string NameKey(string name) => name.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();

    public static bool NameAllowed(string trimmed) =>
        trimmed.Length >= 1 && trimmed.Length <= NameMaxLength
        && !trimmed.Any(c => char.IsControl(c) || c == '\u200d' || char.IsSurrogate(c));

    private void MapDisplay()
    {
        _app.MapGet("/api/player/session/display", (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            lock (_lock) return Results.Json(new Dictionary<string, string>(account.Display));
        });

        _app.MapPut("/api/player/session/display/{slot}", async (HttpContext context, string slot) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            if (!DisplaySlots.Contains(slot))
                return Error(context, StatusCodes.Status400BadRequest, "slot_unknown", "that slot is not one this instance declares");
            using var body = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
            var item = body.RootElement.TryGetProperty("item_id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String
                ? id.GetString() ?? "" : "";
            if (item.Length == 0)
                return Error(context, StatusCodes.Status400BadRequest, "invalid_body", "item_id is required");
            var displayable = DisplaySlotOf(item);
            if (displayable is null)
                return Error(context, StatusCodes.Status404NotFound, "item_unknown", "that item is not in the catalogue");
            if (displayable != slot)
                return Error(context, StatusCodes.Status422UnprocessableEntity, "item_not_displayable", "that item cannot be shown in this slot");
            if (!Holds(account.PlayerId, item))
                return Error(context, StatusCodes.Status403Forbidden, "item_not_owned", "you do not own that item");
            lock (_lock)
            {
                account.Display[slot] = item;
                return Results.Json(new Dictionary<string, string>(account.Display));
            }
        });

        _app.MapDelete("/api/player/session/display/{slot}", (HttpContext context, string slot) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            if (!DisplaySlots.Contains(slot))
                return Error(context, StatusCodes.Status400BadRequest, "slot_unknown", "that slot is not one this instance declares");
            lock (_lock)
            {
                account.Display.Remove(slot);
                return Results.Json(new Dictionary<string, string>(account.Display));
            }
        });
    }
}
