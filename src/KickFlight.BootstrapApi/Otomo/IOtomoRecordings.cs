using System.Text.Json;
using System.Text.Json.Serialization;

namespace KickFlight.BootstrapApi.Otomo;

// Why a patch (recordings) call failed. The same three-way split as IOtomoRank's:
//
//   Disabled     Otomo:Recordings is off, or an upload has no game-server key: there is nobody to ask.
//   Unavailable  patch could not be reached, timed out, or answered 5xx. Which replays exist is unknown, not none.
//   Refused      patch answered a 4xx. StatusCode and Code say which (recording_conflict, validation_failed, ...).
public enum OtomoRecordingsFailure
{
    Disabled,
    Unavailable,
    Refused
}

public sealed class OtomoRecordingsException : Exception
{
    public OtomoRecordingsException(OtomoRecordingsFailure failure, string message, int statusCode = 0,
        string? code = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        StatusCode = statusCode;
        Code = code;
    }

    public OtomoRecordingsFailure Failure { get; }
    public int StatusCode { get; }
    public string? Code { get; }
}

// --- the wire types: patch's own JSON keys (otomo staging de8325e services/patch/internal/recording/recording.go:109-118) ----------------

// One recording as patch lists it. `meta` is the producer's own object, stored verbatim and never interpreted by patch.
public sealed record PatchRecording(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("meta")] JsonElement Meta,
    [property: JsonPropertyName("players")] IReadOnlyList<string>? Players,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

// GET /patch/v1/recordings (services/patch/internal/api/recording.go:617-619).
public sealed record PatchRecordingList(
    [property: JsonPropertyName("recordings")] IReadOnlyList<PatchRecording>? Recordings);

// One recording to upload. The id is the path; the rest are the multipart fields
// (services/patch/internal/api/recording.go:229-297).
public sealed record RecordingUpload(
    string Kind,
    byte[] Bytes,
    IReadOnlyList<string> Players,
    IReadOnlyList<string> Tags,
    string MetaJson);

// The adapter's only view of recorded content.
public interface IOtomoRecordings
{
    // False when Otomo:Recordings is off. Callers fail closed.
    bool Enabled { get; }

    // PUT /internal/patch/recording/{id} with the game-server key. True when patch created the recording (201), false
    // when it already held this exact upload (200). A different upload under a taken id is Refused with code
    // recording_conflict (409): the first upload of a battle stays.
    Task<bool> PutAsync(string recordingId, RecordingUpload upload, CancellationToken cancellationToken);

    // GET /patch/v1/recordings?tag=<tag>&limit=<n> with the player's token: the newest N tagged `tag`, newest first.
    // A tag nobody used is an empty list.
    Task<IReadOnlyList<PatchRecording>> ListByTagAsync(string accessToken, string tag, int limit,
        CancellationToken cancellationToken);

    // GET /patch/v1/recordings?list=<name>: a curated list in its published order, or null for a name patch has never
    // been given (404). Entries whose recording is gone are already omitted by patch.
    Task<IReadOnlyList<PatchRecording>?> ListCuratedAsync(string accessToken, string list,
        CancellationToken cancellationToken);

    // Whether the player's token can fetch this recording's bytes: a one-byte ranged read of
    // GET /patch/v1/recordings/{id}/blob. Patch answers 404 both for no such recording and for one the caller was not in.
    Task<bool> CanFetchAsync(string accessToken, string recordingId, CancellationToken cancellationToken);

    // The bytes, or null when patch answers 404.
    Task<byte[]?> GetBlobAsync(string accessToken, string recordingId, CancellationToken cancellationToken);
}
