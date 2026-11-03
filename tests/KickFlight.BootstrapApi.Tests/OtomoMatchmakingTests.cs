using System.Net;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenMatch;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The otomo matchmaking path: the adapter as a client of otomo's four player routes, and the two things that path
// changes - what /battle/entry hands the client, and what GetAssignments streams. Everything here runs against
// FakeOtomoMatchmaking over real HTTP, so the paths, the bearer token, the empty bodies, the 409s and the retry are
// the adapter's own code, not a stub's.
[Collection("Matchmaking static state")]
public sealed class OtomoMatchmakingTests
{
    // --- the client ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ensure_party_creates_a_party_of_one_and_takes_the_one_otomo_already_has()
    {
        await using var fake = FakeOtomoMatchmaking.Start();
        using var client = ClientFor(fake);

        var created = await client.EnsurePartyAsync("token-a", default);
        Assert.False(string.IsNullOrWhiteSpace(created.PartyId));

        var call = Assert.Single(fake.CallsTo("/api/player/session/party"));
        Assert.Equal("POST", call.Method);
        Assert.Equal("Bearer token-a", call.Authorization);
        // The route takes an empty body: a `null` JSON body is a different request and would be refused.
        Assert.Null(call.Body);

        // The same player entering again: otomo answers 409 already_in_party with the party it already has.
        fake.AlreadyInParty = true;
        fake.ExistingPartyId = "party-otomo-had";
        var existing = await client.EnsurePartyAsync("token-a", default);
        Assert.Equal("party-otomo-had", existing.PartyId);
    }

    [Fact]
    public async Task Ensure_party_reports_a_conflict_that_does_not_name_the_party_as_a_refusal()
    {
        await using var fake = FakeOtomoMatchmaking.Start();
        using var client = ClientFor(fake);

        fake.AlreadyInParty = true;
        fake.AlreadyInPartyNamesTheParty = false;

        var refused = await Assert.ThrowsAsync<OtomoMatchmakingException>(
            () => client.EnsurePartyAsync("token-a", default).AsTask());

        // A refusal with otomo's own code, not an outage and not a party id this client invented.
        Assert.Equal(OtomoMatchmakingFailure.Refused, refused.Failure);
        Assert.Equal(409, refused.StatusCode);
        Assert.Equal("already_in_party", refused.Code);
    }

    [Fact]
    public async Task Enqueue_repeats_to_the_same_ticket_and_already_matched_is_a_refusal()
    {
        await using var fake = FakeOtomoMatchmaking.Start();
        using var client = ClientFor(fake);

        var party = await client.EnsurePartyAsync("token-a", default);
        var first = await client.EnqueueAsync(party.PartyId!, "token-a", default);
        var second = await client.EnqueueAsync(party.PartyId!, "token-a", default);

        Assert.StartsWith("ticket-", first.TicketId);
        Assert.Equal(first.TicketId, second.TicketId);

        // The party is named in the query, which is the only place the route takes it.
        var posts = fake.CallsTo("/api/player/match").Where(call => call.Method == "POST").ToArray();
        Assert.Equal(2, posts.Length);
        Assert.All(posts, call => Assert.Equal($"?party_id={party.PartyId}", call.Query));
        Assert.All(posts, call => Assert.Null(call.Body));

        fake.AlreadyMatched = true;
        var refused = await Assert.ThrowsAsync<OtomoMatchmakingException>(
            () => client.EnqueueAsync(party.PartyId!, "token-a", default).AsTask());
        Assert.Equal(OtomoMatchmakingFailure.Refused, refused.Failure);
        Assert.Equal(409, refused.StatusCode);
        Assert.Equal("already_matched", refused.Code);
    }

    [Fact]
    public async Task State_maps_queued_in_match_and_idle_and_carries_the_descriptors_ports()
    {
        await using var fake = FakeOtomoMatchmaking.Start();
        using var client = ClientFor(fake);

        fake.PollsBeforeMatch = 1;
        var party = await client.EnsurePartyAsync("token-a", default);
        await client.EnqueueAsync(party.PartyId!, "token-a", default);

        var queued = await client.StateAsync(party.PartyId!, "token-a", default);
        Assert.True(queued.IsQueued);
        Assert.False(queued.IsInMatch);
        Assert.Null(queued.Match);

        var matched = await client.StateAsync(party.PartyId!, "token-a", default);
        Assert.True(matched.IsInMatch);
        Assert.NotNull(matched.Match);
        Assert.Equal(fake.MatchId, matched.Match!.MatchId);

        var endpoint = OtomoJoinEndpoint.FromDescriptor(matched.Match.Descriptor);
        Assert.NotNull(endpoint);
        Assert.Equal("10.10.20.148", endpoint!.Host);
        // Handed on exactly as sent: the client rewrites 27000-27002 itself.
        Assert.Equal(27000, endpoint.MasterPort);
        Assert.Equal(27001, endpoint.GamePort);
        Assert.Equal(27002, endpoint.NamePort);
        Assert.Equal(fake.Region, endpoint.Region);
        Assert.False(endpoint.Proxied);

        var reads = fake.CallsTo("/api/player/match").Where(call => call.Method == "GET").ToArray();
        Assert.All(reads, call => Assert.Equal($"?party_id={party.PartyId}", call.Query));
        Assert.All(reads, call => Assert.Equal("Bearer token-a", call.Authorization));

        fake.ReportIdle = true;
        Assert.True((await client.StateAsync(party.PartyId!, "token-a", default)).IsIdle);
    }

    [Fact]
    public async Task Cancel_is_a_delete_and_a_match_in_progress_is_a_refusal()
    {
        await using var fake = FakeOtomoMatchmaking.Start();
        using var client = ClientFor(fake);

        var party = await client.EnsurePartyAsync("token-a", default);
        await client.CancelAsync(party.PartyId!, "token-a", default);

        var delete = Assert.Single(fake.CallsTo("/api/player/match"), call => call.Method == "DELETE");
        Assert.Equal($"?party_id={party.PartyId}", delete.Query);
        Assert.Equal("Bearer token-a", delete.Authorization);

        fake.CancelRefused = true;
        var refused = await Assert.ThrowsAsync<OtomoMatchmakingException>(
            () => client.CancelAsync(party.PartyId!, "token-a", default).AsTask());
        Assert.Equal(OtomoMatchmakingFailure.Refused, refused.Failure);
        Assert.Equal(409, refused.StatusCode);
        Assert.Equal("match_in_progress", refused.Code);
    }

    [Fact]
    public async Task A_refused_token_is_a_refusal_without_a_retry_and_an_unreachable_host_is_unavailability()
    {
        await using var fake = FakeOtomoMatchmaking.Start();
        using var client = ClientFor(fake);

        var refused = await Assert.ThrowsAsync<OtomoMatchmakingException>(
            () => client.EnsurePartyAsync(FakeOtomoMatchmaking.RefusedToken, default).AsTask());
        Assert.Equal(OtomoMatchmakingFailure.Refused, refused.Failure);
        Assert.Equal(401, refused.StatusCode);
        Assert.Equal("invalid_token", refused.Code);
        // A 4xx is an answer: exactly one attempt reached otomo.
        Assert.Single(fake.CallsTo("/api/player/session/party"));

        using var unreachable = ClientFor(fake, "http://127.0.0.1:1/");
        var unavailable = await Assert.ThrowsAsync<OtomoMatchmakingException>(
            () => unreachable.EnsurePartyAsync("token-a", default).AsTask());
        Assert.Equal(OtomoMatchmakingFailure.Unavailable, unavailable.Failure);
        Assert.Equal(0, unavailable.StatusCode);
    }

    [Fact]
    public async Task One_5xx_is_retried_once_and_two_in_a_row_are_unavailability()
    {
        await using var fake = FakeOtomoMatchmaking.Start();
        using var client = ClientFor(fake);

        fake.FailNextRequests = 1;
        var party = await client.EnsurePartyAsync("token-a", default);
        Assert.False(string.IsNullOrWhiteSpace(party.PartyId));
        Assert.Equal(2, fake.CallsTo("/api/player/session/party").Count);

        fake.ClearRequests();
        fake.FailNextRequests = 2;
        var unavailable = await Assert.ThrowsAsync<OtomoMatchmakingException>(
            () => client.EnsurePartyAsync("token-b", default).AsTask());
        Assert.Equal(OtomoMatchmakingFailure.Unavailable, unavailable.Failure);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.Equal(2, fake.CallsTo("/api/player/session/party").Count);
    }

    // --- the stream ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task While_queued_only_stage_one_goes_out()
    {
        await using var fake = FakeOtomoMatchmaking.Start();
        using var client = ClientFor(fake);
        var service = CreateService(client);
        // Long enough that the wait is otomo's answer, not a stopwatch: nothing here is timing out.
        service.MatchWindow = TimeSpan.FromSeconds(30);

        var (entryId, ticketId) = RegisterOtomoEntry(service, "1000001", "Queued player", "party-1");
        Assert.Equal(ticketId, entryId);

        fake.PollsBeforeMatch = 1000; // stays queued for the whole test

        using var cancellation = new CancellationTokenSource();
        var writer = new RecordingWriter();
        var stream = service.StreamAssignmentsAsync(ticketId, writer, cancellation.Token);

        await writer.WaitForWriteAsync(0);
        await Task.Delay(200); // ~10 polls at the 20 ms interval this client is configured with

        // Stage 1 only: no connection, no bots, and just the party this process registered.
        var responses = writer.Snapshot();
        Assert.Single(responses);
        Assert.Empty(responses[0].Assignment.Connection);
        var roster = Roster(responses[0].Assignment);
        var human = Assert.Single(roster);
        Assert.Equal("1000001", human.GetProperty("userId").GetString());
        Assert.Equal(0, human.GetProperty("kickerAiParameterId").GetInt32());

        Assert.True(fake.CallsTo("/api/player/match").Count(call => call.Method == "GET") >= 2);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
    }

    [Fact]
    public async Task In_match_sends_stage_two_then_stage_three_with_the_descriptors_ports_and_no_bots()
    {
        await using var fake = FakeOtomoMatchmaking.Start();
        using var client = ClientFor(fake);
        var service = CreateService(client);
        service.MatchWindow = TimeSpan.FromSeconds(30);

        var (_, ticketId) = RegisterOtomoEntry(service, "1000001", "Matched player", "party-1");

        fake.PollsBeforeMatch = 0; // matched on the first read

        using var cancellation = new CancellationTokenSource();
        var writer = new RecordingWriter();
        var stream = service.StreamAssignmentsAsync(ticketId, writer, cancellation.Token);

        var stageThree = await writer.WaitForWriteAsync(2, TimeSpan.FromSeconds(10));
        var responses = writer.Snapshot();

        // Stage 1 and Stage 2 carry no connection; Stage 3 carries otomo's match id, which is the battle id.
        Assert.Equal(3, responses.Count);
        Assert.Empty(responses[0].Assignment.Connection);
        Assert.Empty(responses[1].Assignment.Connection);
        Assert.Equal(fake.MatchId, stageThree.Assignment.Connection);

        // No bot fill on this path: the party is the whole roster, at every stage.
        foreach (var response in responses)
        {
            var roster = Roster(response.Assignment);
            Assert.Single(roster);
            Assert.Equal(0, Assert.Single(roster).GetProperty("kickerAiParameterId").GetInt32());
        }

        // Where to join came from the descriptor, not from PhotonServerManager: host and all three ports as sent.
        var endpoint = service.GetOtomoJoinEndpoint(fake.MatchId);
        Assert.NotNull(endpoint);
        Assert.Equal("10.10.20.148", endpoint!.Host);
        Assert.Equal(27000, endpoint.MasterPort);
        Assert.Equal(27001, endpoint.GamePort);
        Assert.Equal(27002, endpoint.NamePort);

        // And /battle/start still finds the rule the entry used.
        Assert.Equal(1, service.GetRoomBattleRuleId(fake.MatchId));
        Assert.NotNull(service.GetRoomFieldId(fake.MatchId));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
    }

    [Fact]
    public async Task With_the_switch_off_the_same_ticket_is_matched_in_memory_with_bots_and_otomo_is_never_called()
    {
        await using var fake = FakeOtomoMatchmaking.Start();
        using var off = ClientFor(fake, enabled: false);
        var service = CreateService(off);
        service.MatchWindow = TimeSpan.FromMilliseconds(50);

        Assert.False(service.OtomoPathEnabled);

        // The very same registration, answered by the other matchmaker: the flag is the whole difference.
        var (_, ticketId) = RegisterOtomoEntry(service, "1000001", "Legacy player", "party-1");

        using var cancellation = new CancellationTokenSource();
        var writer = new RecordingWriter();
        var stream = service.StreamAssignmentsAsync(ticketId, writer, cancellation.Token);

        var final = await writer.WaitForWriteAsync(2, TimeSpan.FromSeconds(10));
        var roster = Roster(final.Assignment);

        Assert.StartsWith("battle-", final.Assignment.Connection);
        Assert.Equal(8, roster.Count);
        Assert.Equal(7, roster.Count(entry => entry.GetProperty("kickerAiParameterId").GetInt32() > 0));
        Assert.Null(service.GetOtomoJoinEndpoint(final.Assignment.Connection));
        Assert.Empty(fake.Requests);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
    }

    // --- the request path ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Entry_hands_the_client_otomo_s_ticket_as_its_battle_entry_id_and_cancel_withdraws_the_party()
    {
        await using var fake = FakeOtomoMatchmaking.Start();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Otomo:Matchmaking:Enabled"] = "true",
                    ["Otomo:Matchmaking:BaseUrl"] = fake.BaseUrl,
                    ["Otomo:Matchmaking:PollInterval"] = "00:00:00.020",
                    ["Otomo:Matchmaking:Timeout"] = "00:00:05"
                }));
        });
        using var client = factory.CreateClient();

        var session = await AuthenticateAsync(client, Guid.NewGuid().ToString("N"));
        var entry = await PostAsync(client, session.Token, "/battle/entry", new { battleRuleId = 1 });

        // The response shape the client already knows: an entry id, a ticket id, and the same fields as before.
        var battleEntryId = entry.GetProperty("battleEntryId").GetString();
        var ticketId = entry.GetProperty("battleEntryTicketId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(battleEntryId));
        Assert.StartsWith("ticket-", battleEntryId!);
        Assert.Equal(battleEntryId, ticketId);
        Assert.Equal(5, entry.GetProperty("matchmakingCancelWaitingTimeSecond").GetInt32());
        Assert.False(entry.GetProperty("scf").GetBoolean());

        // It really went to otomo's queue: a party was created and that party was queued.
        Assert.Single(fake.CallsTo("/api/player/session/party"));
        var enqueue = Assert.Single(fake.CallsTo("/api/player/match"));
        Assert.Equal("POST", enqueue.Method);
        Assert.StartsWith("?party_id=party-", enqueue.Query);

        var cancel = await PostAsync(client, session.Token, "/battle/cancel", new { battleEntryId });
        Assert.Equal(JsonValueKind.Object, cancel.ValueKind);
        Assert.Single(fake.CallsTo("/api/player/match"), call => call.Method == "DELETE");
    }

    // --- helpers ---------------------------------------------------------------------------------------------------

    private static OtomoMatchmaking ClientFor(FakeOtomoMatchmaking fake, string? baseUrl = null, bool enabled = true) =>
        new(new HttpClient(),
            Options.Create(new OtomoMatchmakingOptions
            {
                Enabled = enabled,
                BaseUrl = baseUrl ?? fake.BaseUrl,
                Timeout = TimeSpan.FromSeconds(2),
                RetryDelay = TimeSpan.FromMilliseconds(1),
                PollInterval = TimeSpan.FromMilliseconds(20)
            }),
            Options.Create(new OtomoOptions()),
            NullLogger<OtomoMatchmaking>.Instance);

    private static BattleMatchmakingService CreateService(IOtomoMatchmaking matchmaking)
    {
        var photon = new PhotonServerManager(
            Options.Create(new PhotonServerOptions { Enabled = false }),
            NullLogger<PhotonServerManager>.Instance);
        var environment = new TestWebHostEnvironment(RepositoryPaths.FindRoot(AppContext.BaseDirectory));
        return new BattleMatchmakingService(NullLogger<BattleMatchmakingService>.Instance, photon,
            environment: environment, otomoMatchmaking: matchmaking);
    }

    private static (string EntryId, string TicketId) RegisterOtomoEntry(
        BattleMatchmakingService service, string userId, string userName, string partyId) =>
        service.RegisterOtomoEntry(userId, userName, 1, 2010101, 1, [3010001, 3010002, 3010003, 3010004],
            partyId, "token-a");

    private static List<JsonElement> Roster(Assignment assignment)
    {
        var battleJson = assignment.Properties.Fields["battle"].StructValue.ToString();
        using var document = JsonDocument.Parse(battleJson);
        return document.RootElement.GetProperty("battlePlayerList")
            .EnumerateArray()
            .Select(entry => entry.Clone())
            .ToList();
    }

    private sealed class TestWebHostEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "KickFlight.BootstrapApi.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    // Writes are counted, not awaited on a schedule: a stage that never arrives fails with what did.
    private sealed class RecordingWriter : IServerStreamWriter<GetAssignmentsResponse>
    {
        private readonly object _lock = new();
        private readonly List<GetAssignmentsResponse> _responses = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(GetAssignmentsResponse message)
        {
            lock (_lock) _responses.Add(message);
            return Task.CompletedTask;
        }

        public IReadOnlyList<GetAssignmentsResponse> Snapshot()
        {
            lock (_lock) return _responses.ToArray();
        }

        public async Task<GetAssignmentsResponse> WaitForWriteAsync(int index, TimeSpan? timeout = null)
        {
            var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
            while (DateTime.UtcNow < until)
            {
                lock (_lock)
                {
                    if (_responses.Count > index) return _responses[index];
                }
                await Task.Delay(10);
            }
            throw new TimeoutException($"write {index} never arrived ({Snapshot().Count} arrived)");
        }
    }

    // --- the client's own request path, as the game makes it ---------------------------------------------------------

    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
    private const string ClientVersion = "99.99.99";
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");

    private sealed record Session(string Token, string UserId);

    private static async Task<Session> AuthenticateAsync(HttpClient client, string uuid)
    {
        var payload = JsonSerializer.Serialize(new { hash = Encoding.ASCII.GetString(Key), uuid });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(payload),
                Encoding.ASCII.GetBytes(CommonCode), new byte[16])),
            Headers = { Host = Host }
        };
        request.Headers.Add("x-app-application-version", ClientVersion);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"auth answered {(int)response.StatusCode}: {body}");
        return new Session(
            response.Headers.GetValues("x-app-access-token").Single(),
            response.Headers.GetValues("x-app-user-id").Single());
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string token, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)), Key,
                new byte[16])),
            Headers = { Host }
        };
        request.Headers.Add("x-app-access-token", token);
        request.Headers.Add("x-app-application-version", ClientVersion);

        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
        var decrypted = D2CCodec.Decode(payload, Key);
        using var document = JsonDocument.Parse(decrypted);
        return document.RootElement.Clone();
    }
}
