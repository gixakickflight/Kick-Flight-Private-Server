namespace KickFlight.BootstrapApi.Otomo;

// Configuration for the adapter's chat client: appsettings "Otomo:Chat", or Otomo__Chat__* environment variables.
//
// The adapter keeps no message, no room and no retention. What may be said, how often, how long, in what words and
// with which stamps is otomo chat's (services/chat, policy in the chat.rules namespace); this section is only where
// that service lives, the key to call it with, and how often the adapter looks for new messages to push to the
// client's sockets.
public sealed class OtomoChatOptions
{
    public const string SectionName = "Otomo:Chat";

    // Hard switch, and off by default. With it off the client is told there is no chat (an empty chat connection id at
    // startup) and a write fails closed in the game's error contract.
    public bool Enabled { get; set; }

    // Base URL of chat's internal listener (CHAT_INTERNAL_ADDR, :8081), e.g. http://otomo-chat:8081. Chat has no player
    // listener; every route is /internal/chat/... behind chat_adapter.key (services/chat/README.md, "The API").
    public string BaseUrl { get; set; } = "";

    // Bound on one call. At most two attempts are made (see OtomoChat).
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    // How long to wait before the single retry of a transport failure or a 5xx. A 4xx is never retried.
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    // File holding chat_adapter.key, read on every call so rotating the key needs no restart. ServiceKey is the inline
    // alternative, for a test or a local run; a deployment uses the file. The text is presented as-is (trimmed) after
    // "Bearer ", which is what chat compares.
    public string ServiceKeyFile { get; set; } = "";
    public string ServiceKey { get; set; } = "";

    // How often the adapter reads the global channel after its last seen seq while at least one socket is listening.
    // Chat's only push is a Valkey publish on chat.<channel id> (internal/fanout/valkey.go) and this adapter has no
    // Valkey client, so the live relay is this poll; a write made through this adapter wakes it at once.
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    // The player id sent as X-Otomo-Player on the adapter's own reads (the history a socket's listen asks for and the
    // poll). A Firebase socket carries no player identity, and chat requires a player UUID on every route; the global
    // channel admits any. The default is the nil UUID. A chat that starts to require a known player here needs a
    // reader identity that is not a player (reported to the otomo side).
    public string ReaderPlayerId { get; set; } = "00000000-0000-0000-0000-000000000000";
}
