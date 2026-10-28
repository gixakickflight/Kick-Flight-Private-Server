namespace KickFlight.BootstrapApi.Otomo;

// A logged-in device, as otomo describes it.
//
// PlayerId is otomo's account id (a uuid): the only player identity there is. AccessToken/RefreshToken are
// otomo's own pair, cached in memory for this device and never written anywhere. ExpiresAt is when the access
// token stops being accepted, read from the token's own exp claim.
public sealed record OtomoSession(string PlayerId, string AccessToken, DateTimeOffset ExpiresAt, string RefreshToken);

// Why an identity call failed. The adapter turns this into the game's error contract, so the distinction has to
// survive all the way out: a refusal is a 401 for that client, while an unreachable otomo is a 503 for everyone.
public enum OtomoIdentityFailure
{
    // Otomo:Enabled is false, so there is no identity provider to ask.
    Disabled,
    // Otomo could not be reached, timed out, or answered 5xx: the session is unknown, not refused.
    Unavailable,
    // Otomo refused the credential (400/401/403), e.g. a device id it will not admit, or a session it revoked.
    Rejected
}

public sealed class OtomoIdentityException : Exception
{
    public OtomoIdentityException(OtomoIdentityFailure failure, string message, Exception? innerException = null)
        : base(message, innerException) => Failure = failure;

    public OtomoIdentityFailure Failure { get; }
}

// The adapter's only view of player identity.
//
// One implementation (OtomoIdentity) talks to otomo's auth service over HTTP. Callers pass the device id the KF
// client minted; the adapter derives the otomo device id from it, so the same client device always reaches the
// same otomo account.
public interface IOtomoIdentity
{
    // False when Otomo:Enabled is off. Callers fail closed rather than guess who the caller is.
    bool Enabled { get; }

    // A live session for this device: from the cache when it is still valid, otherwise one login, or a refresh
    // when only the access token has expired. Concurrent calls for one device share a single login or refresh.
    ValueTask<OtomoSession> GetSessionAsync(string deviceUuid, CancellationToken cancellationToken);

    // The same session, for an otomo device id this process already derived (KfIdentityTicket.DeviceId). A later
    // request carries that id rather than the client's original one, so re-deriving it would reach a different
    // account; the economy client needs the player's own access token and has nothing but the ticket.
    //
    // It is the same cache and the same gate, so a warm entry costs nothing. A cold one is a single login with that
    // device id, which otomo answers with the same account: an evicted cache or a restart costs a login, never a
    // different identity.
    ValueTask<OtomoSession> GetSessionForDeviceIdAsync(string otomoDeviceId, CancellationToken cancellationToken);

    // Revokes this device's refresh family at otomo and drops it from the cache. Other devices of the same
    // account keep their own families: otomo starts one per login.
    ValueTask LogoutAsync(string deviceUuid, CancellationToken cancellationToken);

    // Devices currently cached. For logging and tests; not part of the request path.
    int CachedDeviceCount { get; }
}
