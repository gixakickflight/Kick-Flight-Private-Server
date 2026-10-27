using System.Net;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The adapter's request path, with otomo as the only identity store: /auth/index signs a device in through otomo and
// hands back a token for it, that token is what every later request is attributed by, and a server that cannot ask
// otomo refuses instead of guessing.
public sealed class OtomoAdapterEndToEndTests : IClassFixture<ServerTestHostFixture>
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
    // These tests are about identity, not about the forced-update gate: a client version at the top of the range
    // keeps whatever update state another test left in the content root from answering first.
    private const string ClientVersion = "99.99.99";

    private static readonly byte[] Key = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");

    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeOtomoServer _otomo = OtomoTestEnvironment.Server;

    public OtomoAdapterEndToEndTests(ServerTestHostFixture fixture) => _factory = fixture.Factory;

    [Fact]
    public async Task A_second_launch_from_the_same_device_reuses_the_login_and_keeps_the_player()
    {
        var uuid = Guid.NewGuid().ToString("N");
        var otomoDevice = OtomoDeviceId.FromDeviceUuid(uuid);

        using var client = _factory.CreateClient();
        var first = await AuthenticateAsync(client, uuid);
        var second = await AuthenticateAsync(client, uuid);

        Assert.Equal(1, _otomo.LoginsFor(otomoDevice));
        Assert.True(long.TryParse(first.UserId, out var firstId), $"x-app-user-id was not numeric: {first.UserId}");
        Assert.Equal(first.UserId, second.UserId);

        // Both launches got a usable token: the second launch reused the otomo session, and either token is still
        // honoured because either one is signed by this server and names the same account.
        var startup = await PostAsync(client, first.Token, "/startup/index", null);
        await PostAsync(client, second.Token, "/startup/index", null);
        Assert.Equal(206, startup.GetProperty("tutorialProgressStatus").GetInt32());
    }

    [Fact]
    public async Task A_token_this_server_did_not_sign_is_rejected_with_the_game_status_header()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/startup/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes("{}"), Key, new byte[16]))
        };
        request.Headers.Host = Host;
        request.Headers.Add("x-app-application-version", ClientVersion);
        request.Headers.Add("x-app-access-token", "demo-access-token-0000000000000000000000000000");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("1", response.Headers.GetValues("x-app-status-code").Single());
    }

    [Fact]
    public async Task With_otomo_unreachable_the_request_fails_closed_in_the_game_error_contract()
    {
        // A host configured with otomo switched on but pointing at a port nothing listens on: what a deployment sees
        // when the auth service is down or misaddressed.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Otomo:Enabled"] = "true",
                    ["Otomo:BaseUrl"] = "http://127.0.0.1:1/",
                    ["Otomo:Timeout"] = "00:00:02",
                    ["Otomo:TokenSecret"] = "unreachable-otomo-test"
                }));
        });
        using var client = factory.CreateClient();

        using var request = AuthRequest(Guid.NewGuid().ToString("N"));
        using var response = await client.SendAsync(request);

        // Not a 500: the client gets a status code it can act on and a body it can parse, and the operator gets the
        // log line that says otomo could not be reached.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("1", response.Headers.GetValues("x-app-status-code").Single());
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("identity-unavailable", body.GetProperty("error").GetString());
        Assert.Equal("Unavailable", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task With_the_switch_off_a_player_request_is_refused_instead_of_guessed()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Otomo:Enabled"] = "false",
                    ["Otomo:TokenSecret"] = "disabled-otomo-test"
                }));
        });
        using var client = factory.CreateClient();

        using var auth = await client.SendAsync(AuthRequest(Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, auth.StatusCode);
        Assert.Equal("1", auth.Headers.GetValues("x-app-status-code").Single());

        // And a token-bearing request is refused too, rather than served from a cache or a fallback identity.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/startup/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes("{}"), Key, new byte[16]))
        };
        request.Headers.Host = Host;
        request.Headers.Add("x-app-application-version", ClientVersion);
        request.Headers.Add("x-app-access-token", "anything-at-all");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed record Session(string Token, string UserId);

    private static HttpRequestMessage AuthRequest(string uuid)
    {
        var payload = JsonSerializer.Serialize(new { hash = Encoding.ASCII.GetString(Key), uuid });
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(payload),
                Encoding.ASCII.GetBytes(CommonCode), new byte[16])),
            Headers = { Host = Host }
        };
        request.Headers.Add("x-app-application-version", ClientVersion);
        return request;
    }

    private static async Task<Session> AuthenticateAsync(HttpClient client, string uuid)
    {
        using var response = await client.SendAsync(AuthRequest(uuid));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"auth answered {(int)response.StatusCode}: {body}");
        Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
        return new Session(
            response.Headers.GetValues("x-app-access-token").Single(),
            response.Headers.GetValues("x-app-user-id").Single());
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string token, string path, object? body)
    {
        var json = body is null ? "{}" : JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(json), Key, new byte[16])),
            Headers = { Host = Host }
        };
        request.Headers.Add("x-app-access-token", token);
        request.Headers.Add("x-app-application-version", ClientVersion);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
        var decrypted = D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), Key);
        using var document = JsonDocument.Parse(decrypted);
        return document.RootElement.Clone();
    }
}
