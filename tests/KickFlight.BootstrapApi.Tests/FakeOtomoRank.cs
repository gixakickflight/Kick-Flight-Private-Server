using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KickFlight.BootstrapApi.Tests;

// A stand-in for otomo's session rank service: the two player-plane routes and the bodies their handlers answer
// (services/session/internal/api/rank.go), on loopback over real HTTP. Nothing is faked at the HttpClient level, so
// a test that turns Otomo:Rank on exercises the adapter's own client: the path, the bearer token, the retry and the
// translation from otomo's points to the client's ladder.
//
// State is per account, keyed by the `sub` of the access token the adapter presents - the same account the shared
// FakeOtomoServer minted for that device - so two test sessions never see each other's standing, exactly as the real
// service derives the player from the token.
public sealed class FakeOtomoRank : IAsyncDisposable
{
    // The one rule type a deployment has until it publishes session.rank, and the lowest tier of the default ladder.
    private const string DefaultRuleType = "ranked";
    private const string DefaultTier = "bronze";

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _pointsByAccount = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Call> _calls = new();
    private int _failures;

    private FakeOtomoRank(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; private set; }

    // How many of the next requests answer 503 before the service behaves again. One 503 is invisible through the
    // adapter's retry; two in a row is the Unavailable it has to report.
    public int FailNextRequests
    {
        get => Volatile.Read(ref _failures);
        set => Volatile.Write(ref _failures, value);
    }

    // Held before the answer, so a test can outlast the adapter's own timeout without sleeping by accident.
    public TimeSpan Delay { get; set; }

    public IReadOnlyList<Call> Calls => _calls.ToArray();

    public void ClearCalls() => _calls.Clear();

    public static FakeOtomoRank Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FakeOtomoRank(app, "");
        server.Map();
        app.StartAsync().GetAwaiter().GetResult();

        var address = app.Urls.FirstOrDefault(url => !url.EndsWith(":0", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Kestrel did not report a bound address (urls: {string.Join(", ", app.Urls)})");
        server.BaseUrl = address.TrimEnd('/');
        return server;
    }

    // The points this account has been awarded in a rule type. Seeding none is a player with no awards, which otomo
    // answers as zero in the lowest tier rather than as a miss.
    public void Seed(string accountId, string ruleType, int points) =>
        _pointsByAccount.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, int>(StringComparer.Ordinal))[ruleType] = points;

    public int PointsOf(string accountId, string ruleType = DefaultRuleType) =>
        _pointsByAccount.TryGetValue(accountId, out var byType) ? byType.GetValueOrDefault(ruleType) : 0;

    private void Map()
    {
        // GET /api/player/session/rank - every rule type the policy names, which for a deployment with no published
        // session.rank is just "ranked". A player with no row reads zero in the lowest tier.
        _app.MapGet("/api/player/session/rank", async (HttpContext context) =>
        {
            Record(context);
            if (!await AnswerAsync(context)) return;
            var account = AccountOf(context);
            var ranks = new List<object> { Entry(DefaultRuleType, PointsOf(account)) };
            await WriteJsonAsync(context, new { ranks });
        });

        // GET /api/player/session/rank/{rule_type} - a rule type the policy does not name is 404 unknown_rule_type,
        // which is "this game does not rank that battle", not a failure.
        _app.MapGet("/api/player/session/rank/{ruleType}", async (HttpContext context, string ruleType) =>
        {
            Record(context);
            if (ruleType != DefaultRuleType)
            {
                if (!await AnswerAsync(context)) return;
                await WriteErrorAsync(context, 404, "unknown_rule_type", "no rank is kept for that battle rule type");
                return;
            }
            if (!await AnswerAsync(context)) return;
            await WriteJsonAsync(context, Entry(DefaultRuleType, PointsOf(AccountOf(context))));
        });
    }

    private void Record(HttpContext context) =>
        _calls.Enqueue(new Call(context.Request.Method, context.Request.Path.Value ?? "", context.Request.Headers.Authorization.ToString()));

    // False when this request was answered with a 503 (nothing else to write).
    private async Task<bool> AnswerAsync(HttpContext context)
    {
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, context.RequestAborted);

        if (Interlocked.Decrement(ref _failures) >= 0)
        {
            await WriteErrorAsync(context, 503, "unavailable", "session rank is unavailable");
            return false;
        }
        return true;
    }

    private static object Entry(string ruleType, int points) => new
    {
        rule_type = ruleType,
        points,
        tier = DefaultTier,
        updated_at = (DateTimeOffset?)null
    };

    private static string AccountOf(HttpContext context)
    {
        var account = FakeOtomoEconomy.AccountOf(context.Request.Headers.Authorization.ToString());
        return account ?? "";
    }

    private static async Task WriteJsonAsync(HttpContext context, object body)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = new { code, message } }));
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    public sealed record Call(string Method, string Path, string? Authorization);
}
