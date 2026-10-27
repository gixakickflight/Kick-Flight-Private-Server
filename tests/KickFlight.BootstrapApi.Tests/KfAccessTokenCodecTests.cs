using System.Security.Cryptography;
using System.Text;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The token the KF client carries between requests. It is the whole of the adapter's memory of a session - there is
// no table behind it - so what it carries, and what it refuses, is worth pinning.
public sealed class KfAccessTokenCodecTests
{
    private static readonly byte[] Key = Enumerable.Range(0, D2CCodec.KeySizeBytes).Select(value => (byte)value).ToArray();

    private static KfAccessTokenCodec NewCodec(string? secret = "test-secret") =>
        new(secret, NullLogger<KfAccessTokenCodec>.Instance);

    private static KfIdentityTicket NewTicket() =>
        new(OtomoDeviceId.FromDeviceUuid("device-1"), "8b1a1f0e-6c2b-4d3a-9c55-6f0d0f0c9a11", 1000042, Key);

    [Fact]
    public void A_token_round_trips_everything_a_request_needs()
    {
        var codec = NewCodec();
        var ticket = NewTicket();

        Assert.True(codec.TryRead(codec.Issue(ticket), out var read));

        Assert.Equal(ticket.DeviceId, read.DeviceId);
        Assert.Equal(ticket.OtomoPlayerId, read.OtomoPlayerId);
        Assert.Equal(ticket.GamePlayerId, read.GamePlayerId);
        // The D2C key is the client's own, and the reply body is encrypted with it: losing it here would leave the
        // client unable to read a single response.
        Assert.Equal(ticket.Key, read.Key);
    }

    [Fact]
    public void A_token_signed_with_another_secret_is_rejected()
    {
        var issued = NewCodec("server-a").Issue(NewTicket());

        Assert.False(NewCodec("server-b").TryRead(issued, out _));
    }

    [Fact]
    public void A_payload_edited_after_signing_is_rejected()
    {
        var codec = NewCodec();
        var parts = codec.Issue(NewTicket()).Split('.');
        var payload = Encoding.UTF8.GetString(UnBase64Url(parts[1])).Replace("1000042", "1000001");
        var forged = $"{parts[0]}.{Base64Url(Encoding.UTF8.GetBytes(payload))}.{parts[2]}";

        Assert.False(codec.TryRead(forged, out _));
    }

    [Fact]
    public void An_opaque_token_the_old_store_would_have_held_is_rejected()
    {
        var codec = NewCodec();

        // The pre-otomo server accepted this fixed token for every player; nothing about it is signed, so nothing
        // about it may be trusted now.
        Assert.False(codec.TryRead("demo-access-token-0000000000000000000000000000", out _));
        Assert.False(codec.TryRead("not-a-token-this-server-issued", out _));
        Assert.False(codec.TryRead("kf1.only-two-parts", out _));
        Assert.False(codec.TryRead("", out _));
    }

    [Fact]
    public void A_restart_with_the_configured_secret_still_accepts_the_tokens_it_issued()
    {
        var issued = NewCodec("deployment-secret").Issue(NewTicket());

        // A new process, the same configuration: the client keeps playing instead of being signed out by a redeploy.
        Assert.True(NewCodec("deployment-secret").TryRead(issued, out var read));
        Assert.Equal(1000042, read.GamePlayerId);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
