using System.Text;
using System.Text.Json.Serialization;

namespace KickFlight.BootstrapApi;

public sealed class FixtureDefinition
{
    public required string Id { get; init; }
    public bool Enabled { get; init; } = true;
    public required string Host { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    /// <summary>Optional direct-client discriminator for routes such as the platform-specific boot response.</summary>
    public int? ClientAssetPlatform { get; init; }
    public int StatusCode { get; init; } = 200;
    public string ContentType { get; init; } = "application/json; charset=utf-8";
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Body { get; init; }
    public string? BodyBase64 { get; init; }

    [JsonIgnore]
    public string SourceFile { get; set; } = "";

    public byte[] GetBodyBytes()
    {
        if (Body is not null && BodyBase64 is not null)
            throw new InvalidDataException($"Fixture '{Id}' defines both body and bodyBase64.");
        return BodyBase64 is not null ? Convert.FromBase64String(BodyBase64) : Encoding.UTF8.GetBytes(Body ?? "");
    }
}

public sealed record FixtureSnapshot(IReadOnlyList<FixtureDefinition> Fixtures, IReadOnlyList<string> Errors);
