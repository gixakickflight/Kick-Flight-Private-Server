using System.Collections.Concurrent;
using KickFlight.BootstrapApi.Otomo;
using KickFlight.BootstrapApi.PlayerStore;

namespace KickFlight.BootstrapApi;

/// <summary>What the client's chat DTO shows for a sender: the numeric id the client speaks, the name, the honor.</summary>
public sealed record ChatSender(string UserId, string Name, int HonorId);

/// <summary>Resolves a chat message's sender (an otomo player id; chat carries nothing else) to the client's DTO fields.</summary>
public interface IChatSenders
{
    Task<ChatSender> ResolveAsync(string otomoPlayerId, CancellationToken cancellationToken);
}

/// <summary>
/// The name is session's (<c>GET /internal/session/players/{id}</c>, display_name), read through <see cref="IOtomoProfile"/>
/// and cached for a short while so a busy room is not one session call per line; the adapter's own copy of the name is
/// never used. The numeric id is the adapter's id mapping, and the honor is `display.honor` of the same card. When session cannot answer, the line shows the placeholder "Player &lt;id&gt;" and the miss is not cached.
/// </summary>
public sealed class ChatSenders(IPlayerStore players, IOtomoProfile profile, TimeProvider time, ILogger<ChatSenders> logger)
    : IChatSenders
{
    private static readonly TimeSpan NameTtl = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, (string Name, int Honor, DateTimeOffset Expires)> _names = new(StringComparer.Ordinal);

    public async Task<ChatSender> ResolveAsync(string otomoPlayerId, CancellationToken cancellationToken)
    {
        var row = players.FindOtomoPlayers([otomoPlayerId]).FirstOrDefault();
        var userId = row is null ? otomoPlayerId : row.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (name, honor) = await CardOfAsync(otomoPlayerId, cancellationToken).ConfigureAwait(false);
        return new ChatSender(userId, string.IsNullOrWhiteSpace(name) ? $"Player {userId}" : name, honor);
    }

    // The name and the equipped badge are one read of the sender's card: `display_name` and `display.honor`.
    private async Task<(string? Name, int Honor)> CardOfAsync(string otomoPlayerId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (_names.TryGetValue(otomoPlayerId, out var hit) && hit.Expires > now) return (hit.Name, hit.Honor);
        if (!profile.Enabled) return (null, BadgeCatalog.DefaultHonorId);
        try
        {
            var card = await profile.GetPlayerProfileAsync(otomoPlayerId, cancellationToken).ConfigureAwait(false);
            var name = card.DisplayName ?? "";
            var honor = DemoSessionApi.HonorOfDisplay(card.Display);
            _names[otomoPlayerId] = (name, honor, now + NameTtl);
            return (name, honor);
        }
        catch (OtomoRankException exception)
        {
            logger.LogWarning("chat: no card for {Player} from session ({Failure})", otomoPlayerId, exception.Failure);
            return (null, BadgeCatalog.DefaultHonorId);
        }
    }
}
