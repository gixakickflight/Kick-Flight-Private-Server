namespace KickFlight.BootstrapApi.ChatTranslation;

/// <summary>
/// Chat translation + reader-language settings. Plain environment variables (GEMINI_API_KEY, KF_CHAT_TRANSLATE_MODEL,
/// KF_CHAT_TRANSLATE_CONTEXT_MESSAGES, KF_GEOIP_DB, KF_CHAT_FORCE_LANGUAGE, KF_CHAT_FORCE_COUNTRY) take precedence over
/// the Chat:Translation / Chat:GeoIp appsettings sections. The API key is never logged or serialized.
/// </summary>
public sealed class ChatTranslationOptions
{
    public const string DefaultModel = "gemini-3.5-flash-lite";

    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = DefaultModel;
    public int ContextMessages { get; init; } = 20;
    public string BaseUrl { get; init; } = "https://generativelanguage.googleapis.com";
    public string ApiRevision { get; init; } = "2026-05-20";
    public int RequestTimeoutSeconds { get; init; } = 8;
    /// <summary>Longest a live push is held back waiting for a translation; after that the original is pushed.</summary>
    public int MaxBroadcastDelayMs { get; init; } = 10000;
    public int MaxPending { get; init; } = 64;
    public int MaxConcurrency { get; init; } = 4;
    public int RetryDelayMs { get; init; } = 500;
    public int MaxOutputTokens { get; init; } = 1024;
    public string ThinkingLevel { get; init; } = "low";

    public string GeoIpDatabasePath { get; init; } = "";
    public string[] TrustedProxies { get; init; } = ClientIpResolver.DefaultTrusted;
    /// <summary>Dev override: always serve this language (pt/es/ja/en), ignoring the client IP.</summary>
    public string ForceLanguage { get; init; } = "";
    /// <summary>Dev override: pretend every client is in this ISO country (ignored when ForceLanguage is set).</summary>
    public string ForceCountry { get; init; } = "";

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);

    public static ChatTranslationOptions FromConfiguration(IConfiguration c)
    {
        string Pick(string env, string section, string fallback)
        {
            var v = c[env];
            if (string.IsNullOrWhiteSpace(v)) v = c[section];
            return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
        }
        int PickInt(string env, string section, int fallback, int min, int max) =>
            int.TryParse(Pick(env, section, ""), out var n) ? Math.Clamp(n, min, max) : fallback;

        var proxies = c.GetSection("Chat:GeoIp:TrustedProxies").Get<string[]>();
        return new ChatTranslationOptions
        {
            ApiKey = Pick("GEMINI_API_KEY", "Chat:Translation:ApiKey", ""),
            Model = Pick("KF_CHAT_TRANSLATE_MODEL", "Chat:Translation:Model", DefaultModel),
            ContextMessages = PickInt("KF_CHAT_TRANSLATE_CONTEXT_MESSAGES", "Chat:Translation:ContextMessages", 20, 0, 100),
            BaseUrl = Pick("KF_CHAT_TRANSLATE_BASE_URL", "Chat:Translation:BaseUrl", "https://generativelanguage.googleapis.com").TrimEnd('/'),
            ApiRevision = Pick("KF_CHAT_TRANSLATE_API_REVISION", "Chat:Translation:ApiRevision", "2026-05-20"),
            RequestTimeoutSeconds = PickInt("", "Chat:Translation:RequestTimeoutSeconds", 8, 1, 60),
            MaxBroadcastDelayMs = PickInt("", "Chat:Translation:MaxBroadcastDelayMs", 10000, 0, 30000),
            MaxPending = PickInt("", "Chat:Translation:MaxPending", 64, 1, 10000),
            MaxConcurrency = PickInt("", "Chat:Translation:MaxConcurrency", 4, 1, 32),
            RetryDelayMs = PickInt("", "Chat:Translation:RetryDelayMs", 500, 0, 10000),
            MaxOutputTokens = PickInt("", "Chat:Translation:MaxOutputTokens", 1024, 128, 8192),
            ThinkingLevel = Pick("", "Chat:Translation:ThinkingLevel", "low"),
            GeoIpDatabasePath = Pick("KF_GEOIP_DB", "Chat:GeoIp:DatabasePath", ""),
            TrustedProxies = proxies is { Length: > 0 } ? proxies : ClientIpResolver.DefaultTrusted,
            ForceLanguage = Pick("KF_CHAT_FORCE_LANGUAGE", "Chat:GeoIp:ForceLanguage", "").ToLowerInvariant(),
            ForceCountry = Pick("KF_CHAT_FORCE_COUNTRY", "Chat:GeoIp:ForceCountry", "").ToUpperInvariant(),
        };
    }
}
