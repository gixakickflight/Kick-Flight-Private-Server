namespace KickFlight.BootstrapApi.ChatTranslation;

public static class ChatTranslationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the options and the client-IP/GeoIP reader-language resolution. The adapter translates nothing itself
    /// (otomo chat does); this is only what picks a reader's language and recognises an author's own connection.
    /// Everything is built lazily from the final IConfiguration (not the builder's early snapshot) so host-level
    /// overrides, including test configuration, are honoured.
    /// </summary>
    public static IServiceCollection AddChatTranslation(this IServiceCollection services)
    {
        services.AddSingleton(sp => ChatTranslationOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => new ClientIpResolver(sp.GetRequiredService<ChatTranslationOptions>().TrustedProxies));
        services.AddSingleton<ICountryLookup>(sp => new GeoIpCountryLookup(sp.GetRequiredService<ChatTranslationOptions>().GeoIpDatabasePath,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<GeoIpCountryLookup>>()));
        services.AddSingleton<ChatLanguageResolver>();
        return services;
    }
}
