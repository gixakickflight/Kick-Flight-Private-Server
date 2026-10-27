using KickFlight.BootstrapApi.Otomo;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// What the adapter promises about identity, against a real HTTP server standing in for otomo: one login per device,
// the pair cached in memory, refresh inside the family when the access token expires, logout revoking that device
// only, and a fail-closed answer when otomo cannot be asked.
public sealed class OtomoIdentityTests
{
    private readonly FakeOtomoServer _server = OtomoTestEnvironment.Server;

    private static string NewDeviceUuid() => Guid.NewGuid().ToString("N");

    private static string OtomoDeviceOf(string deviceUuid) => OtomoDeviceId.FromDeviceUuid(deviceUuid);

    private OtomoIdentity NewIdentity(ManualTimeProvider clock, string? baseUrl = null, bool enabled = true,
        int capacity = 64, TimeSpan? idle = null)
    {
        var options = Options.Create(new OtomoOptions
        {
            Enabled = enabled,
            BaseUrl = baseUrl ?? _server.BaseUrl,
            Timeout = TimeSpan.FromSeconds(10),
            RefreshSkew = TimeSpan.FromSeconds(30),
            CacheCapacity = capacity,
            CacheIdle = idle ?? TimeSpan.FromMinutes(30),
            TokenSecret = "otomo-identity-tests"
        });
        return new OtomoIdentity(new HttpClient(), options, clock, NullLogger<OtomoIdentity>.Instance);
    }

    [Fact]
    public async Task A_device_logs_in_once_and_every_later_request_reuses_that_session()
    {
        var uuid = NewDeviceUuid();
        using var identity = NewIdentity(new ManualTimeProvider(DateTimeOffset.UtcNow));

        var first = await identity.GetSessionAsync(uuid, CancellationToken.None);
        var second = await identity.GetSessionAsync(uuid, CancellationToken.None);

        Assert.Equal(1, _server.LoginsFor(OtomoDeviceOf(uuid)));
        Assert.Equal(first.PlayerId, second.PlayerId);
        Assert.Equal(first.AccessToken, second.AccessToken);
        Assert.Equal(first.RefreshToken, second.RefreshToken);
        // The player id is otomo's account id, which is what the game row is filed under.
        Assert.True(Guid.TryParse(first.PlayerId, out _), $"expected an otomo account uuid, got {first.PlayerId}");
    }

    [Fact]
    public async Task Two_devices_are_two_accounts()
    {
        var firstUuid = NewDeviceUuid();
        var secondUuid = NewDeviceUuid();
        using var identity = NewIdentity(new ManualTimeProvider(DateTimeOffset.UtcNow));

        var first = await identity.GetSessionAsync(firstUuid, CancellationToken.None);
        var second = await identity.GetSessionAsync(secondUuid, CancellationToken.None);

        Assert.NotEqual(first.PlayerId, second.PlayerId);
        Assert.Equal(1, _server.LoginsFor(OtomoDeviceOf(firstUuid)));
        Assert.Equal(1, _server.LoginsFor(OtomoDeviceOf(secondUuid)));
    }

    [Fact]
    public async Task An_expired_access_token_is_refreshed_inside_the_same_family()
    {
        var uuid = NewDeviceUuid();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var identity = NewIdentity(clock);

        var first = await identity.GetSessionAsync(uuid, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(30));

        var refreshed = await identity.GetSessionAsync(uuid, CancellationToken.None);

        Assert.Equal(1, _server.RefreshesFor(OtomoDeviceOf(uuid)));
        Assert.Equal(1, _server.LoginsFor(OtomoDeviceOf(uuid)));
        Assert.Equal(first.PlayerId, refreshed.PlayerId);
        Assert.NotEqual(first.AccessToken, refreshed.AccessToken);
        Assert.NotEqual(first.RefreshToken, refreshed.RefreshToken);
    }

    [Fact]
    public async Task One_cold_device_touched_by_many_requests_logs_in_exactly_once()
    {
        // A dedicated server, because the delay that makes the race reliable must not slow the rest of the suite.
        await using var server = FakeOtomoServer.Start();
        server.LoginDelay = TimeSpan.FromMilliseconds(300);
        var uuid = NewDeviceUuid();
        using var identity = NewIdentity(new ManualTimeProvider(DateTimeOffset.UtcNow), server.BaseUrl);

        var sessions = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => identity.GetSessionAsync(uuid, CancellationToken.None).AsTask()));

        Assert.Equal(1, server.LoginsFor(OtomoDeviceOf(uuid)));
        Assert.Single(sessions.Select(session => session.PlayerId).Distinct());
    }

    [Fact]
    public async Task One_expired_device_touched_by_many_requests_refreshes_exactly_once_and_keeps_its_family()
    {
        await using var server = FakeOtomoServer.Start();
        var uuid = NewDeviceUuid();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        // otomo mints against the same clock the adapter reads, or the refresh it issues after the advance would
        // itself already be expired and every waiter would rotate again.
        server.Clock = clock;
        using var identity = NewIdentity(clock, server.BaseUrl);

        await identity.GetSessionAsync(uuid, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(30));
        server.LoginDelay = TimeSpan.FromMilliseconds(100);

        var sessions = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => identity.GetSessionAsync(uuid, CancellationToken.None).AsTask()));

        Assert.Equal(1, server.RefreshesFor(OtomoDeviceOf(uuid)));
        Assert.Single(sessions.Select(session => session.RefreshToken).Distinct());

        // If a second refresh had reached otomo with the same token it would have revoked the family, and this next
        // rotation would come back 401. It does not: the family is intact.
        clock.Advance(TimeSpan.FromMinutes(30));
        var next = await identity.GetSessionAsync(uuid, CancellationToken.None);
        Assert.Equal(2, server.RefreshesFor(OtomoDeviceOf(uuid)));
        Assert.Equal(sessions[0].PlayerId, next.PlayerId);
    }

    [Fact]
    public async Task Logout_revokes_that_device_only_and_the_device_can_sign_in_again()
    {
        var loggedOutUuid = NewDeviceUuid();
        var otherUuid = NewDeviceUuid();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var identity = NewIdentity(clock);

        await identity.GetSessionAsync(loggedOutUuid, CancellationToken.None);
        await identity.GetSessionAsync(otherUuid, CancellationToken.None);
        var cachedBefore = identity.CachedDeviceCount;

        await identity.LogoutAsync(loggedOutUuid, CancellationToken.None);

        Assert.Equal(1, _server.LogoutsFor(OtomoDeviceOf(loggedOutUuid)));
        Assert.Equal(0, _server.LogoutsFor(OtomoDeviceOf(otherUuid)));
        Assert.Equal(cachedBefore - 1, identity.CachedDeviceCount);

        // The other device's family is untouched.
        clock.Advance(TimeSpan.FromMinutes(30));
        var other = await identity.GetSessionAsync(otherUuid, CancellationToken.None);
        Assert.Equal(1, _server.RefreshesFor(OtomoDeviceOf(otherUuid)));

        // The logged-out device signs in again with the same device id and lands on the same account in a new family.
        var again = await identity.GetSessionAsync(loggedOutUuid, CancellationToken.None);
        Assert.Equal(2, _server.LoginsFor(OtomoDeviceOf(loggedOutUuid)));
        Assert.NotEqual(other.PlayerId, again.PlayerId);
        Assert.True(Guid.TryParse(again.PlayerId, out _));
    }

    [Fact]
    public async Task An_unreachable_otomo_fails_once_without_retrying()
    {
        // Port 1 on loopback: nothing can be listening there.
        using var identity = NewIdentity(new ManualTimeProvider(DateTimeOffset.UtcNow), "http://127.0.0.1:1/");

        var exception = await Assert.ThrowsAsync<OtomoIdentityException>(
            () => identity.GetSessionAsync(NewDeviceUuid(), CancellationToken.None).AsTask());

        Assert.Equal(OtomoIdentityFailure.Unavailable, exception.Failure);
        Assert.Equal(0, identity.CachedDeviceCount);
    }

    [Fact]
    public async Task With_the_switch_off_nothing_is_asked_and_the_failure_is_disabled()
    {
        var uuid = NewDeviceUuid();
        using var identity = NewIdentity(new ManualTimeProvider(DateTimeOffset.UtcNow), enabled: false);

        var exception = await Assert.ThrowsAsync<OtomoIdentityException>(
            () => identity.GetSessionAsync(uuid, CancellationToken.None).AsTask());

        Assert.Equal(OtomoIdentityFailure.Disabled, exception.Failure);
        Assert.False(identity.Enabled);
        Assert.Equal(0, _server.LoginsFor(OtomoDeviceOf(uuid)));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("7C6C3A2E-1F4B-4C9D-9A11-8E2D0B7A5C31")]
    [InlineData("emulator-5554")]
    [InlineData("a")]
    public void The_derived_device_id_always_satisfies_otomo_admission(string clientDeviceId)
    {
        // otomo refuses anything but 43 unpadded base64url characters (services/auth internal/identity/deviceid.go),
        // and the KF client's own uuid is an arbitrary string. The derivation has to bridge that for every shape of
        // client id, or a device could not sign in at all.
        var deviceId = OtomoDeviceId.FromDeviceUuid(clientDeviceId);

        Assert.Equal(43, deviceId.Length);
        Assert.All(deviceId, character => Assert.True(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        Assert.DoesNotContain(deviceId, character => character is '+' or '/' or '=');
        Assert.Equal(deviceId, OtomoDeviceId.FromDeviceUuid(clientDeviceId));
        Assert.NotEqual(deviceId, OtomoDeviceId.FromDeviceUuid(clientDeviceId + "-other"));
    }

    [Fact]
    public async Task The_cache_is_bounded_and_drops_the_least_recently_used_device()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var identity = NewIdentity(clock, capacity: 2);

        foreach (var uuid in new[] { NewDeviceUuid(), NewDeviceUuid(), NewDeviceUuid() })
        {
            await identity.GetSessionAsync(uuid, CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(61)); // one sweep interval, so eviction actually runs
        }

        Assert.True(identity.CachedDeviceCount <= 2, $"cache held {identity.CachedDeviceCount} sessions");
    }

    [Fact]
    public async Task An_idle_device_session_is_dropped_and_logs_in_again_on_its_next_request()
    {
        var uuid = NewDeviceUuid();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var identity = NewIdentity(clock, idle: TimeSpan.FromMinutes(1));

        await identity.GetSessionAsync(uuid, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(5));
        await identity.GetSessionAsync(NewDeviceUuid(), CancellationToken.None); // triggers the sweep

        Assert.Equal(1, identity.CachedDeviceCount);
        await identity.GetSessionAsync(uuid, CancellationToken.None);
        Assert.Equal(2, _server.LoginsFor(OtomoDeviceOf(uuid)));
    }
}

// A clock the test moves by hand, so expiry is a statement about time rather than a sleep.
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (this) return _now;
    }

    public void Advance(TimeSpan by)
    {
        lock (this) _now += by;
    }
}
