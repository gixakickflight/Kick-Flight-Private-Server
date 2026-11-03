namespace KickFlight.BootstrapApi.Push;

// Section "Push". Disabled unless explicitly enabled and a credentials file is present.
public sealed class PushOptions
{
    public const string SectionName = "Push";

    public bool Enabled { get; set; }
    // Path to the Firebase service-account JSON. Only the path is configuration; the file is read by FcmSender.
    public string CredentialsPath { get; set; } = "";
    public string Title { get; set; } = "Kick Flight";
    public string Body { get; set; } = "¡Alguien está buscando partida ranked! Entra ahora para jugar.";
    public int GlobalCooldownSeconds { get; set; } = 240;
    public int PerPlayerCooldownSeconds { get; set; } = 240;
    public int SkipRecentlyActiveSeconds { get; set; } = 120;
    // Ranked search lasts about a minute, so a push older than that is useless.
    public int TtlSeconds { get; set; } = 60;
    public int MaxConcurrentSends { get; set; } = 8;
}
