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
/// never used. The numeric id is the adapter's id mapping, and the honor is the one game-side fact (badges) chat does
/// not carry. When session cannot answer, the line shows the placeholder "Player &lt;id&gt;" and the miss is not cached.
/// </summary>
public sealed class ChatSenders(IPlayerStore players, IOtomoProfile profile, TimeProvider time, ILogger<ChatSenders> logger)
    : IChatSenders
{
    private static readonly TimeSpan NameTtl = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, (string Name, DateTimeOffset Expires)> _names = new(StringComparer.Ordinal);

    public async Task<ChatSender> ResolveAsync(string otomoPlayerId, CancellationToken cancellationToken)
    {
        var row = players.FindOtomoPlayers([otomoPlayerId]).FirstOrDefault();
        var userId = row is null ? otomoPlayerId : row.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Another player's equipped badge is not readable from session (its internal profile read carries no display
        // map), so a chat line shows the default honor. docs/OTOMO_BADGES.md names the gap.
        var honor = BadgeCatalog.DefaultHonorId;
        var name = await NameOfAsync(otomoPlayerId, cancellationToken).ConfigureAwait(false);
        return new ChatSender(userId, string.IsNullOrWhiteSpace(name) ? $"Player {userId}" : name, honor);
    }

    private async Task<string?> NameOfAsync(string otomoPlayerId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (_names.TryGetValue(otomoPlayerId, out var hit) && hit.Expires > now) return hit.Name;
        if (!profile.Enabled) return null;
        try
        {
            var card = await profile.GetPlayerProfileAsync(otomoPlayerId, cancellationToken).ConfigureAwait(false);
            var name = card.DisplayName ?? "";
            _names[otomoPlayerId] = (name, now + NameTtl);
            return name;
        }
        catch (OtomoRankException exception)
        {
            logger.LogWarning("chat: no name for {Player} from session ({Failure})", otomoPlayerId, exception.Failure);
            return null;
        }
    }
}
