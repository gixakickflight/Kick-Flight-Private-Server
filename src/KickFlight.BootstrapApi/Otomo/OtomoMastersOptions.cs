namespace KickFlight.BootstrapApi.Otomo;

// Configuration for the master tables this adapter loads from otomo: appsettings "Otomo:Masters", or
// Otomo__Masters__* environment variables.
//
// Masters used to be files: config/masters_*.json, read once at boot, optionally overridden from a directory the
// balance UI wrote. They are now data an operator edits in otomo's admin UI and publishes as a release, and this
// section is how a running server follows that release without a rebuild.
//
// The mapping is a setting rather than a constant in code because the namespace names are data this game owns
// (masters/otomo/namespaces.json in this repository) and otomo's copy of them is just rows. Each entry is
// "<master table name>=<otomo namespace>"; every table named here comes from otomo and from nowhere else, which
// is what makes deleting the file path for a table a one-line change rather than a refactor.
public sealed class OtomoMastersOptions
{
    public const string SectionName = "Otomo:Masters";

    // Hard switch, and off by default: with it off the adapter reads config/masters_*.json exactly as before.
    public bool Enabled { get; set; }

    // Base URL of otomo Config's internal API listener, e.g. http://otomo-config:8081. Not the gateway: this
    // route is for game servers, is keyed by a service key, and no gateway routes to it.
    public string BaseUrl { get; set; } = "";

    // Which release channel to follow. A server that wants a pin instead would name a release id; the adapter
    // follows a channel because an operator publishing to it is the whole point of the port.
    public string Channel { get; set; } = "staging";

    // File holding the gameserver service key. Read on every fetch, so rotating the key needs no restart. Key is
    // the inline alternative, for a test or a local run; a deployment uses the file.
    public string KeyFile { get; set; } = "";
    public string Key { get; set; } = "";

    // How often to poll. A poll that changes nothing costs one conditional request answered 304.
    public TimeSpan Refresh { get; set; } = TimeSpan.FromSeconds(30);

    // Where the last good bundle is kept, so a restart while otomo is unreachable still serves the masters it
    // last loaded instead of refusing to start. Must be writable; the default sits under .local, which a
    // deployment may mount read-only.
    public string CacheDir { get; set; } = ".local/otomo-masters";

    // "<master>=<namespace>" pairs. A table listed here is loaded from otomo only: if the release does not carry
    // it, the adapter fails at startup rather than silently serving the file it was meant to replace.
    public string[] Tables { get; set; } = Array.Empty<string>();

    // Bound on one fetch. The bundle is small (all master tables together are under a megabyte) but a stalled
    // connection must not hold the refresh loop forever.
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    // Parsed Tables. A malformed entry is a startup failure, because a silently ignored binding is a table the
    // operator believes is being served from otomo while the file is still the source.
    public IReadOnlyList<MasterBinding> Bindings()
    {
        var bindings = new List<MasterBinding>(Tables.Length);
        foreach (var entry in Tables)
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1)
            {
                throw new InvalidOperationException(
                    $"Otomo:Masters:Tables entry '{entry}' is not <master>=<namespace>");
            }
            bindings.Add(new MasterBinding(entry[..separator].Trim(), entry[(separator + 1)..].Trim()));
        }
        return bindings;
    }
}

// One master table and the otomo namespace that now holds it.
public sealed record MasterBinding(string Table, string Namespace);
