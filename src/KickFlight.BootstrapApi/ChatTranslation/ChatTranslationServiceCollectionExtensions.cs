namespace KickFlight.BootstrapApi.ChatTranslation;

public static class ChatTranslationServiceCollectionExtensions
{
    /// <summary>
    /// Registers options, client-IP/GeoIP language resolution and the translator (a no-op when GEMINI_API_KEY is unset).
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
        services.AddSingleton<IChatTranslator>(sp =>
        {
            var options = sp.GetRequiredService<ChatTranslationOptions>();
            if (!options.Enabled) return new DisabledChatTranslator();
            var client = GeminiInteractionsClient.Create(options, sp.GetRequiredService<ILogger<GeminiInteractionsClient>>());
            return new ChatTranslator(client, options, sp.GetRequiredService<ILogger<ChatTranslator>>());
        });
        return services;
    }
}
