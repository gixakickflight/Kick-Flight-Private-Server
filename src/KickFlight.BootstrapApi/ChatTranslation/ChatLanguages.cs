namespace KickFlight.BootstrapApi.ChatTranslation;

/// <summary>The four chat display languages and the country -> language policy (docs/CHAT_TRANSLATION.md).</summary>
public static class ChatLanguages
{
    public const string Portuguese = "pt";
    public const string Spanish = "es";
    public const string Japanese = "ja";
    public const string English = "en";

    /// <summary>The reader languages this adapter can select (otomo chat.rules declares what is actually translated).</summary>
    public static readonly IReadOnlyList<string> All = [Portuguese, Spanish, Japanese, English];

    private static readonly HashSet<string> PortugueseCountries = new(StringComparer.Ordinal)
    { "BR", "PT", "AO", "MZ", "CV", "GW", "ST", "TL", "MO" };

    private static readonly HashSet<string> SpanishCountries = new(StringComparer.Ordinal)
    {
        "ES", "MX", "AR", "CO", "PE", "VE", "CL", "EC", "GT", "CU", "BO", "DO", "HN", "PY", "SV", "NI", "CR", "PA",
        "UY", "PR", "GQ"
    };

    public static bool IsSupported(string? language) => language is not null && All.Contains(language);

    /// <summary>ISO 3166-1 alpha-2 (case-insensitive) -> language. Unknown/empty/other -> English.</summary>
    public static string ForCountry(string? isoCode)
    {
        if (string.IsNullOrWhiteSpace(isoCode)) return English;
        var code = isoCode.Trim().ToUpperInvariant();
        if (PortugueseCountries.Contains(code)) return Portuguese;
        if (SpanishCountries.Contains(code)) return Spanish;
        if (code == "JP") return Japanese;
        return English;
    }
}
