namespace KickFlight.BootstrapApi.ChatTranslation;

/// <summary>
/// The adapter's side of chat translation (docs/CHAT_TRANSLATION.md). The translating itself is otomo chat's: each
/// message it answers may carry <c>translations</c> (language -> text, for the languages its <c>chat.rules</c> declares)
/// and a <c>translation</c> state. The adapter only picks which text a reader gets (reader language from the client IP,
/// the author's own connection always gets the original) and holds a live push back while chat says a translation is
/// pending. Plain environment variables (KF_GEOIP_DB, KF_CHAT_FORCE_LANGUAGE, KF_CHAT_FORCE_COUNTRY) take precedence over
/// the Chat:Translation / Chat:GeoIp appsettings sections.
/// </summary>
public sealed class ChatTranslationOptions
{
    /// <summary>Adapter switch for the read-side selection and the hold-back. Off: every reader gets the original text
    /// and messages are pushed as soon as they are read, whatever chat says about their translation.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Longest a live push is held back while chat reports the message's translation as pending, counted from
    /// the message's sent_at; after that the message is pushed with whatever translations it has.</summary>
    public int MaxBroadcastDelayMs { get; init; } = 10000;

    public string GeoIpDatabasePath { get; init; } = "";
    public string[] TrustedProxies { get; init; } = ClientIpResolver.DefaultTrusted;
    /// <summary>Dev override: always serve this language (pt/es/ja/en), ignoring the client IP.</summary>
    public string ForceLanguage { get; init; } = "";
    /// <summary>Dev override: pretend every client is in this ISO country (ignored when ForceLanguage is set).</summary>
    public string ForceCountry { get; init; } = "";

    public static ChatTranslationOptions FromConfiguration(IConfiguration c)
    {
        string Pick(string env, string section, string fallback)
        {
            var v = env.Length > 0 ? c[env] : null;
            if (string.IsNullOrWhiteSpace(v)) v = c[section];
            return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
        }
        int PickInt(string section, int fallback, int min, int max) =>
            int.TryParse(Pick("", section, ""), out var n) ? Math.Clamp(n, min, max) : fallback;

        var proxies = c.GetSection("Chat:GeoIp:TrustedProxies").Get<string[]>();
        return new ChatTranslationOptions
        {
            Enabled = !bool.TryParse(Pick("", "Chat:Translation:Enabled", "true"), out var enabled) || enabled,
            MaxBroadcastDelayMs = PickInt("Chat:Translation:MaxBroadcastDelayMs", 10000, 0, 30000),
            GeoIpDatabasePath = Pick("KF_GEOIP_DB", "Chat:GeoIp:DatabasePath", ""),
            TrustedProxies = proxies is { Length: > 0 } ? proxies : ClientIpResolver.DefaultTrusted,
            ForceLanguage = Pick("KF_CHAT_FORCE_LANGUAGE", "Chat:GeoIp:ForceLanguage", "").ToLowerInvariant(),
            ForceCountry = Pick("KF_CHAT_FORCE_COUNTRY", "Chat:GeoIp:ForceCountry", "").ToUpperInvariant(),
        };
    }
}
