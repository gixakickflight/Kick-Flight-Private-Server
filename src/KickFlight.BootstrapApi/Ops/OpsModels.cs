using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace KickFlight.BootstrapApi.Ops;

// Rows of the ops telemetry tables (see PostgresOpsStore for the schema). The same shapes are the NDJSON lines of
// the local fallback store, so they serialise with snake_case names (OpsJson.Rows) to match the SQL columns.
// Timestamps are always UTC.

public sealed record ServerRunRow
{
    public string Id { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public string ReleaseSha { get; init; } = "";
    public string MasterVersion { get; init; } = "";
    public string MinClientVersion { get; init; } = "";
}

public sealed record BattleRow
{
    public string BattleId { get; init; } = "";
    public string RunId { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public int RuleId { get; init; }
    public int? MatchType { get; init; }
    public int FieldId { get; init; }
    public int Humans { get; init; }
    public int Bots { get; init; }
    public bool Custom { get; init; }
    public DateTime? FirstStartAt { get; init; }
    public int BattleTimeSeconds { get; init; }
    /// <summary>clean / partial / abandoned / never_started, or null while the match is still open.</summary>
    public string? Outcome { get; init; }
}

public sealed record ParticipantRow
{
    public string BattleId { get; init; } = "";
    public string UserId { get; init; } = "";
    public string BattleEntryId { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public int KickerId { get; init; }
    public int CostumeId { get; init; }
    /// <summary>Colorful.TeamType of the final roster (0 Blue, 1 Red); -1 until the room is finalized.</summary>
    public int Team { get; init; } = -1;
    public bool IsDiag { get; init; }
    public string AppVersion { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public string Ip { get; init; } = "";
    public DateTime? StartAt { get; init; }
    public DateTime? EndAt { get; init; }
    public DateTime? ResultAt { get; init; }
    public int ReconnectBegins { get; init; }
    public string? ReconnectFailedCause { get; init; }
    public string? DisconnectCause { get; init; }
    public int ExceptionCount { get; init; }
    /// <summary>See OpsStatusRules: matching / assigned / playing while open, a final status afterwards.</summary>
    public string Status { get; init; } = OpsStatusRules.Matching;
}

public sealed record ClientEventRow
{
    public long Id { get; init; }
    public DateTime At { get; init; }
    public string RunId { get; init; } = "";
    public string? UserId { get; init; }
    public string? BattleId { get; init; }
    public string Kind { get; init; } = "";
    public string AppVersion { get; init; } = "";
    public string Device { get; init; } = "";
    public string Os { get; init; } = "";
    public string? Fingerprint { get; init; }
    public JsonObject Payload { get; init; } = new();
}

/// <summary>
/// The x-app-* headers every client request carries, plus the client address. X-Real-IP is what the cdn nginx sets
/// for the API; the socket address is only a fallback for local runs. Each value is capped so a hostile header
/// cannot bloat the tables.
/// </summary>
public sealed record OpsClientInfo(string AppVersion, string DeviceName, string OsVersion, string Platform,
    string UserAgent, string Ip)
{
    private const int MaxHeaderChars = 128;

    public static readonly OpsClientInfo Empty = new("", "", "", "", "", "");

    public static OpsClientInfo FromRequest(HttpContext context)
    {
        var headers = context.Request.Headers;
        var ip = Cap(headers["X-Real-IP"].ToString());
        if (ip.Length == 0)
        {
            var forwarded = headers["X-Forwarded-For"].ToString();
            ip = Cap(forwarded.Split(',', 2)[0]);
        }
        if (ip.Length == 0 && context.Connection.RemoteIpAddress is { } remote)
        {
            ip = (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();
        }
        return new OpsClientInfo(
            Cap(headers["x-app-application-version"].ToString()),
            Cap(headers["x-app-device-name"].ToString()),
            Cap(headers["x-app-os-version"].ToString()),
            Cap(headers["x-app-platform"].ToString()),
            Cap(headers["x-app-user-agent"].ToString()),
            ip);
    }

    private static string Cap(string value)
    {
        value = value.Trim();
        return value.Length <= MaxHeaderChars ? value : value[..MaxHeaderChars];
    }
}

/// <summary>A roster slot as the battle context of an event sees it (compact on purpose: it rides on every event).</summary>
public sealed record OpsRosterSlot(string UserId, int KickerId, int CostumeId, int Team, bool Bot);

internal static class OpsJson
{
    /// <summary>Row (de)serialisation: snake_case like the SQL columns, nulls kept so a later line can clear a value.</summary>
    public static readonly JsonSerializerOptions Rows = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}
