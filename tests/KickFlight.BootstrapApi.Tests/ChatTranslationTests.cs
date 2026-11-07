using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.ChatTranslation;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// Chat translation, adapter side (docs/CHAT_TRANSLATION.md): language map, client IP, read-path selection and the hold-back of
// a message otomo chat reports as pending. The translating is otomo chat's and is tested there.
public sealed class ChatTranslationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kf-chat-tr-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---- country -> language ----

    [Theory]
    [InlineData("BR", "pt")] [InlineData("PT", "pt")] [InlineData("AO", "pt")] [InlineData("MZ", "pt")] [InlineData("CV", "pt")]
    [InlineData("GW", "pt")] [InlineData("ST", "pt")] [InlineData("TL", "pt")] [InlineData("MO", "pt")]
    [InlineData("ES", "es")] [InlineData("MX", "es")] [InlineData("AR", "es")] [InlineData("CO", "es")] [InlineData("PE", "es")]
    [InlineData("VE", "es")] [InlineData("CL", "es")] [InlineData("EC", "es")] [InlineData("GT", "es")] [InlineData("CU", "es")]
    [InlineData("BO", "es")] [InlineData("DO", "es")] [InlineData("HN", "es")] [InlineData("PY", "es")] [InlineData("SV", "es")]
    [InlineData("NI", "es")] [InlineData("CR", "es")] [InlineData("PA", "es")] [InlineData("UY", "es")] [InlineData("PR", "es")]
    [InlineData("GQ", "es")]
    [InlineData("JP", "ja")] [InlineData("jp", "ja")] [InlineData(" br ", "pt")]
    [InlineData("US", "en")] [InlineData("FR", "en")] [InlineData("", "en")] [InlineData(null, "en")] [InlineData("ZZ", "en")]
    public void Country_maps_to_language(string? country, string expected) => Assert.Equal(expected, ChatLanguages.ForCountry(country));

    // ---- client IP ----

    private static readonly IPAddress Proxy = IPAddress.Parse("172.18.0.5");

    [Fact]
    public void Untrusted_peer_ignores_forwarding_headers()
    {
        var r = new ClientIpResolver();
        var peer = IPAddress.Parse("203.0.113.9");
        Assert.Equal(peer, r.Resolve(peer, "8.8.8.8", "1.1.1.1"));
    }

    [Fact]
    public void Trusted_proxy_uses_x_real_ip()
    {
        var r = new ClientIpResolver();
        Assert.Equal(IPAddress.Parse("198.51.100.7"), r.Resolve(Proxy, "198.51.100.7", "9.9.9.9, 172.18.0.1"));
    }

    [Fact]
    public void Trusted_proxy_falls_back_to_rightmost_untrusted_forwarded_for()
    {
        var r = new ClientIpResolver();
        // client-supplied leftmost hop (6.6.6.6) is spoofable; the rightmost non-proxy address wins.
        Assert.Equal(IPAddress.Parse("198.51.100.7"), r.Resolve(Proxy, null, "6.6.6.6, 198.51.100.7, 172.18.0.1"));
    }

    [Fact]
    public void Trusted_proxy_without_headers_or_with_garbage_returns_peer()
    {
        var r = new ClientIpResolver();
        Assert.Equal(Proxy, r.Resolve(Proxy, null, null));
        Assert.Equal(Proxy, r.Resolve(Proxy, "not-an-ip", "nope"));
        Assert.Null(r.Resolve(null, "1.1.1.1", null));
    }

    [Fact]
    public void Ipv4_mapped_peer_and_custom_trusted_list()
    {
        var r = new ClientIpResolver(["203.0.113.0/24"]);
        Assert.Equal(IPAddress.Parse("8.8.8.8"), r.Resolve(IPAddress.Parse("203.0.113.9").MapToIPv6(), "8.8.8.8", null));
        Assert.Equal(Proxy, r.Resolve(Proxy, "8.8.8.8", null)); // 172.18/16 no longer trusted
    }

    // ---- language resolver / geoip ----

    private sealed class FakeCountries(string? country) : ICountryLookup
    {
        public int Calls;
        public string? Lookup(IPAddress address) { Calls++; return country; }
    }

    [Fact]
    public void Resolver_uses_country_and_defaults_to_english()
    {
        var o = new ChatTranslationOptions();
        Assert.Equal("pt", new ChatLanguageResolver(o, new ClientIpResolver(), new FakeCountries("BR")).ForAddress(IPAddress.Parse("8.8.8.8")));
        Assert.Equal("en", new ChatLanguageResolver(o, new ClientIpResolver(), new FakeCountries(null)).ForAddress(IPAddress.Parse("8.8.8.8")));
        Assert.Equal("en", new ChatLanguageResolver(o, new ClientIpResolver(), new FakeCountries("JP")).ForAddress(null));
    }

    [Fact]
    public void Resolver_dev_overrides_win()
    {
        var jp = new FakeCountries("JP");
        Assert.Equal("es", new ChatLanguageResolver(new ChatTranslationOptions { ForceLanguage = "es" }, new ClientIpResolver(), jp).ForAddress(IPAddress.Loopback));
        Assert.Equal("ja", new ChatLanguageResolver(new ChatTranslationOptions { ForceCountry = "JP" }, new ClientIpResolver(), new FakeCountries(null)).ForAddress(IPAddress.Loopback));
        // unsupported forced language is ignored
        Assert.Equal("ja", new ChatLanguageResolver(new ChatTranslationOptions { ForceLanguage = "xx" }, new ClientIpResolver(), jp).ForAddress(IPAddress.Parse("8.8.8.8")));
    }

    [Theory]
    [InlineData("10.1.2.3", false)] [InlineData("192.168.1.5", false)] [InlineData("172.20.0.2", false)] [InlineData("127.0.0.1", false)]
    [InlineData("100.64.0.1", false)] [InlineData("169.254.1.1", false)] [InlineData("::1", false)] [InlineData("fd00::1", false)]
    [InlineData("8.8.8.8", true)] [InlineData("172.32.0.1", true)] [InlineData("2001:4860:4860::8888", true)]
    public void Public_address_detection(string ip, bool expected) => Assert.Equal(expected, GeoIpCountryLookup.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public void Missing_geoip_database_degrades_to_unknown()
    {
        using var lookup = new GeoIpCountryLookup(Path.Combine(_dir, "nope.mmdb"), TimeProvider.System, NullLogger<GeoIpCountryLookup>.Instance);
        Assert.Null(lookup.Lookup(IPAddress.Parse("8.8.8.8")));
        Assert.False(lookup.DatabaseLoaded);
        using var unset = new GeoIpCountryLookup("", TimeProvider.System, NullLogger<GeoIpCountryLookup>.Instance);
        Assert.Null(unset.Lookup(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void Corrupt_geoip_database_degrades_to_unknown()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "bad.mmdb");
        File.WriteAllText(file, "not a database");
        using var lookup = new GeoIpCountryLookup(file, TimeProvider.System, NullLogger<GeoIpCountryLookup>.Instance);
        Assert.Null(lookup.Lookup(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void Real_geoip_database_when_KF_GEOIP_DB_is_set()
    {
        var path = Environment.GetEnvironmentVariable("KF_GEOIP_DB");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return; // optional: needs the DB-IP file (scripts/update-geoip-db.sh)
        using var lookup = new GeoIpCountryLookup(path, TimeProvider.System, NullLogger<GeoIpCountryLookup>.Instance);
        Assert.Equal("US", lookup.Lookup(IPAddress.Parse("8.8.8.8")));
        Assert.Equal("US", lookup.Lookup(IPAddress.Parse("8.8.8.8"))); // cached path
        Assert.Null(lookup.Lookup(IPAddress.Parse("10.0.0.1")));
    }


    // ---- options ----

    [Fact]
    public void Options_defaults_and_env_overrides()
    {
        var d = ChatTranslationOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.True(d.Enabled);
        Assert.Equal(10000, d.MaxBroadcastDelayMs);
        Assert.Equal("", d.GeoIpDatabasePath);

        var c = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KF_GEOIP_DB"] = "/x.mmdb", ["KF_CHAT_FORCE_LANGUAGE"] = "JA", ["KF_CHAT_FORCE_COUNTRY"] = "br",
            ["Chat:GeoIp:DatabasePath"] = "ignored", ["Chat:Translation:Enabled"] = "false",
            ["Chat:Translation:MaxBroadcastDelayMs"] = "99999"
        }).Build();
        var o = ChatTranslationOptions.FromConfiguration(c);
        Assert.False(o.Enabled);
        Assert.Equal(30000, o.MaxBroadcastDelayMs); // clamped
        Assert.Equal("/x.mmdb", o.GeoIpDatabasePath);
        Assert.Equal("ja", o.ForceLanguage);
        Assert.Equal("BR", o.ForceCountry);
    }

    [Fact]
    public void Chat_translation_fields_are_read_from_chats_message_json_and_tolerate_absence()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var full = JsonSerializer.Deserialize<ChatMessageView>("""
            {"channel_id":"global","seq":4,"sender_id":"p","body":"hola","sent_at":"2026-11-08T10:00:00.123456789Z",
             "translations":{"en":"hello","ja":"こんにちは"},"translation":"done"}
            """, options)!;
        Assert.Equal("hello", full.Translations!["en"]);
        Assert.Equal("done", full.Translation);
        Assert.False(full.TranslationIsPending);

        var bare = JsonSerializer.Deserialize<ChatMessageView>("""
            {"channel_id":"global","seq":5,"sender_id":"p","body":"x","sent_at":"2026-11-08T10:00:00Z"}
            """, options)!;
        Assert.Null(bare.Translations);
        Assert.Null(bare.Translation);
        Assert.False(bare.TranslationIsPending);

        var pending = JsonSerializer.Deserialize<ChatMessageView>("""
            {"channel_id":"global","seq":6,"sender_id":"p","body":"x","sent_at":"2026-11-08T10:00:00Z","translations":{},"translation":"pending"}
            """, options)!;
        Assert.True(pending.TranslationIsPending);
        Assert.Empty(pending.Translations!);
    }

    // ---- hold-back rule ----

    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private static ChatMessageView View(long seq, string? state, DateTimeOffset? sentAt = null) =>
        new("global", seq, "p", "m" + seq, null, sentAt ?? T0, null, state);

    [Fact]
    public void Deliverable_stops_at_the_first_pending_message_inside_its_window()
    {
        var views = new[] { View(1, "done"), View(2, null), View(3, "pending"), View(4, "done"), View(5, "failed") };
        Assert.Equal(2, ChatService.Deliverable(views, T0.AddSeconds(1), 10000, holdBack: true));
        // The window is counted from sent_at: once it has passed the pending message goes out with what it has.
        Assert.Equal(5, ChatService.Deliverable(views, T0.AddSeconds(10), 10000, holdBack: true));
        // Hold-back off (adapter translation switch): nothing is ever held.
        Assert.Equal(5, ChatService.Deliverable(views, T0, 10000, holdBack: false));
        // failed / skipped / done / absent are never held.
        var settled = new[] { View(1, "failed"), View(2, "skipped"), View(3, "done"), View(4, null) };
        Assert.Equal(4, ChatService.Deliverable(settled, T0, 10000, holdBack: true));
        Assert.Equal(0, ChatService.Deliverable([View(1, "pending")], T0, 10000, holdBack: true));
    }

    // ---- read selection ----

    [Fact]
    public void Selection_picks_the_readers_language_else_the_original_and_the_author_always_gets_the_original()
    {
        var m = new ChatService.ChatMessage("1", "5", "N", "orig", 0, 0, "t", new Dictionary<string, string> { ["es"] = "hola" });
        Assert.Equal("hola", ChatService.SelectText(m, "es", false));
        Assert.Equal("orig", ChatService.SelectText(m, "pt", false));
        Assert.Equal("orig", ChatService.SelectText(m, "es", isAuthor: true));
        Assert.Equal("orig", ChatService.SelectText(m, null, false));
        Assert.Equal("orig", ChatService.SelectText(m with { Translations = new Dictionary<string, string> { ["es"] = "  " } }, "es", false));
        Assert.Equal("orig", ChatService.SelectText(m with { Translations = null }, "es", false));
    }

    [Fact]
    public void Client_node_never_exposes_the_translations_dictionary()
    {
        var m = new ChatService.ChatMessage("1", "5", "N", "orig", 0, 0, "t", new Dictionary<string, string> { ["es"] = "hola" });
        var node = ChatService.ToNode(m, "es", false).AsObject();
        Assert.Equal("hola", (string?)node["comment"]);
        Assert.False(node.ContainsKey("Translations") || node.ContainsKey("translations"));
        Assert.Equal(6, node.Count);
    }

    // ---- the service against a fake otomo chat ----

    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "t";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private long _ticks = now.UtcTicks;
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    }

    // otomo chat's internal read/send, in memory: ascending by seq, `after` exclusive, the newest `limit`. A test edits a
    // stored message (its translation state) the way chat's worker would.
    private sealed class FakeChat(Clock clock) : IOtomoChat
    {
        private readonly List<ChatMessageView> _messages = [];
        public bool Enabled => true;
        public int Reads;

        public Task<ChatChannelView> ResolveGlobalAsync(string playerId, CancellationToken ct) =>
            Task.FromResult(new ChatChannelView("global", "global", null, null));

        public Task<IReadOnlyList<ChatMessageView>> ReadAsync(string channelId, string playerId, long after, int limit, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            lock (_messages)
                return Task.FromResult<IReadOnlyList<ChatMessageView>>(_messages.Where(m => m.Seq > after).TakeLast(limit).ToArray());
        }

        public Task<ChatSendResult> SendAsync(string channelId, string playerId, string body, string attachment, string key, CancellationToken ct)
        {
            lock (_messages)
            {
                var view = new ChatMessageView("global", _messages.Count + 1, playerId, body, attachment, clock.GetUtcNow(), null, "pending");
                _messages.Add(view);
                return Task.FromResult(new ChatSendResult(view, false));
            }
        }

        public void Settle(long seq, string state, IReadOnlyDictionary<string, string>? translations)
        {
            lock (_messages)
            {
                var i = _messages.FindIndex(m => m.Seq == seq);
                _messages[i] = _messages[i] with { Translation = state, Translations = translations };
            }
        }
    }

    // otomo player "p-<n>" is game player <n>.
    private sealed class Senders : IChatSenders
    {
        public Task<ChatSender> ResolveAsync(string otomoPlayerId, CancellationToken ct) =>
            Task.FromResult(new ChatSender(otomoPlayerId["p-".Length..], "Player", 6010000));
    }

    private static ChatService NewService(FakeChat chat, Clock clock, bool translation = true, int maxDelayMs = 10000) =>
        new(chat, new Senders(), Options.Create(new OtomoChatOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(20) }),
            new Env(), new ConfigurationBuilder().Build(), NullLogger<ChatService>.Instance,
            new ChatTranslationOptions { Enabled = translation, MaxBroadcastDelayMs = maxDelayMs }, clock);

    private sealed class CapturingSocket : WebSocket
    {
        public List<string> Sent { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool e, CancellationToken c)
        {
            lock (Sent) Sent.Add(Encoding.UTF8.GetString(b.Array!, b.Offset, b.Count));
            return Task.CompletedTask;
        }
        // Every comment received, the snapshot's and the live pushes', in arrival order.
        public IReadOnlyList<string> Comments()
        {
            var result = new List<string>();
            lock (Sent)
                foreach (var frame in Sent)
                {
                    var data = JsonNode.Parse(frame)!["d"]?["b"]?["d"] as JsonObject;
                    if (data is null) continue;
                    if (data["comment"] is JsonValue c) result.Add((string)c!);
                    else foreach (var child in data) if (child.Value?["comment"] is JsonValue cc) result.Add((string)cc!); // snapshot
                }
            return result;
        }
    }

    private static async Task<(ChatConnection Conn, CapturingSocket Socket)> Listen(ChatService chat, string? language, long? authAs = null, string? address = null)
    {
        var socket = new CapturingSocket();
        var conn = chat.Register(socket, language, address);
        if (authAs is { } id)
            await chat.HandleFrameAsync(conn, """{"t":"d","d":{"r":1,"a":"auth","b":{"cred":"@@"}}}""".Replace("@@", chat.CustomTokenFor(id)));
        await chat.HandleFrameAsync(conn, """{"t":"d","d":{"r":2,"a":"q","b":{"p":"en/1","t":7}}}""");
        return (conn, socket);
    }

    private static async Task Eventually(Func<bool> condition, int ms = 3000)
    {
        for (var waited = 0; waited < ms && !condition(); waited += 20) await Task.Delay(20);
        Assert.True(condition());
    }

    private static readonly IReadOnlyDictionary<string, string> Hello =
        new Dictionary<string, string> { ["es"] = "es:hello", ["ja"] = "ja:hello", ["en"] = "en:hello" };

    [Fact]
    public async Task A_pending_message_is_held_until_chat_settles_it_then_pushed_in_each_readers_language()
    {
        var clock = new Clock(T0);
        var fake = new FakeChat(clock);
        using var chat = NewService(fake, clock);
        var (_, es) = await Listen(chat, "es");
        var (_, raw) = await Listen(chat, null);

        await chat.WriteAsync("p-5", "hello", 0, "k1", default);
        var reads = fake.Reads;
        await Eventually(() => fake.Reads >= reads + 3); // the relay has polled several times ...
        Assert.Empty(es.Comments());                      // ... and pushed nothing: the translation is pending

        fake.Settle(1, "done", Hello);
        await Eventually(() => es.Comments().Count == 1 && raw.Comments().Count == 1);
        Assert.Equal("es:hello", es.Comments().Single());
        Assert.Equal("hello", raw.Comments().Single()); // no language -> original
    }

    [Fact]
    public async Task A_held_message_keeps_the_ones_after_it_back_and_order_is_chats()
    {
        var clock = new Clock(T0);
        var fake = new FakeChat(clock);
        using var chat = NewService(fake, clock);
        var (_, es) = await Listen(chat, "es");

        await chat.WriteAsync("p-5", "first", 0, "k1", default);
        await chat.WriteAsync("p-6", "second", 0, "k2", default);
        fake.Settle(2, "done", new Dictionary<string, string> { ["es"] = "es:second" });
        var reads = fake.Reads;
        await Eventually(() => fake.Reads >= reads + 3);
        Assert.Empty(es.Comments()); // second is ready, but first is still pending ahead of it

        fake.Settle(1, "failed", null);
        await Eventually(() => es.Comments().Count == 2);
        Assert.Equal(["first", "es:second"], es.Comments()); // failed -> the original
    }

    [Fact]
    public async Task A_message_still_pending_after_the_window_is_pushed_with_the_original()
    {
        var clock = new Clock(T0);
        var fake = new FakeChat(clock);
        using var chat = NewService(fake, clock, maxDelayMs: 10000);
        var (_, es) = await Listen(chat, "es");

        await chat.WriteAsync("p-5", "slow", 0, "k1", default);
        var reads = fake.Reads;
        await Eventually(() => fake.Reads >= reads + 2);
        Assert.Empty(es.Comments());

        clock.Advance(TimeSpan.FromSeconds(10));
        await Eventually(() => es.Comments().Count == 1);
        Assert.Equal("slow", es.Comments().Single());

        // A late joiner's snapshot is never held, and shows a translation that arrived after the push.
        fake.Settle(1, "done", new Dictionary<string, string> { ["es"] = "es:slow" });
        var (_, late) = await Listen(chat, "es");
        Assert.Equal("es:slow", late.Comments().Single());
    }

    [Fact]
    public async Task Snapshots_include_pending_messages_with_whatever_translations_exist()
    {
        var clock = new Clock(T0);
        var fake = new FakeChat(clock);
        using var chat = NewService(fake, clock);
        await chat.WriteAsync("p-5", "one", 0, "k1", default);
        await chat.WriteAsync("p-5", "two", 0, "k2", default);
        fake.Settle(1, "done", new Dictionary<string, string> { ["ja"] = "ja:one" });

        var (_, ja) = await Listen(chat, "ja");
        Assert.Equal(["ja:one", "two"], ja.Comments());
    }

    [Fact]
    public async Task With_the_adapter_switch_off_nothing_is_held_and_everyone_gets_the_original()
    {
        var clock = new Clock(T0);
        var fake = new FakeChat(clock);
        using var chat = NewService(fake, clock, translation: false);
        Assert.False(chat.TranslationEnabled);
        Assert.Equal("kfchat-dummy-token", chat.CustomTokenFor(5));
        var (_, es) = await Listen(chat, "es");

        await chat.WriteAsync("p-5", "hello", 0, "k1", default); // pending at chat, but not held
        fake.Settle(1, "pending", Hello);
        await Eventually(() => es.Comments().Count == 1);
        Assert.Equal("hello", es.Comments().Single());
    }

    [Fact]
    public async Task Author_sees_own_original_via_auth_frame()
    {
        var clock = new Clock(T0);
        var fake = new FakeChat(clock);
        using var chat = NewService(fake, clock);
        var (author, mine) = await Listen(chat, "es", authAs: 1000027);
        var (_, other) = await Listen(chat, "es", authAs: 1000028);
        Assert.Equal(1000027, author.UserId);

        await chat.WriteAsync("p-1000027", "hello", 0, "k1", default);
        fake.Settle(1, "done", Hello);
        await Eventually(() => mine.Comments().Count == 1 && other.Comments().Count == 1);
        Assert.Equal("hello", mine.Comments().Single());
        Assert.Equal("es:hello", other.Comments().Single());
    }

    [Fact]
    public async Task Author_is_recognised_by_http_address_when_the_client_sends_no_auth_frame()
    {
        var clock = new Clock(T0);
        var fake = new FakeChat(clock);
        using var chat = NewService(fake, clock);
        chat.NoteClientAddress(1000027, "203.0.113.7");
        chat.NoteClientAddress(1000028, "198.51.100.9");
        var (_, mine) = await Listen(chat, "es", address: "203.0.113.7");
        var (_, other) = await Listen(chat, "es", address: "198.51.100.9");
        var (_, anonymous) = await Listen(chat, "es");

        await chat.WriteAsync("p-1000027", "hello", 0, "k1", default);
        fake.Settle(1, "done", Hello);
        await Eventually(() => mine.Comments().Count == 1 && other.Comments().Count == 1 && anonymous.Comments().Count == 1);
        Assert.Equal("hello", mine.Comments().Single());
        Assert.Equal("es:hello", other.Comments().Single());
        Assert.Equal("es:hello", anonymous.Comments().Single());

        // a late joiner from the author's address also gets the original in the snapshot
        var (_, late) = await Listen(chat, "es", address: "203.0.113.7");
        Assert.Equal("hello", late.Comments().Single());
    }

    [Fact]
    public void Address_match_is_skipped_when_translation_is_off_or_the_auth_frame_names_someone_else()
    {
        var clock = new Clock(T0);
        using var off = NewService(new FakeChat(clock), clock, translation: false);
        off.NoteClientAddress(1000027, "203.0.113.7");
        Assert.False(off.IsAuthor(new ChatService.ChatMessage("1", "1000027", "N", "x", 0, 0, "t"), off.Register(new CapturingSocket(), null, "203.0.113.7")));

        using var on = NewService(new FakeChat(clock), clock);
        on.NoteClientAddress(1000027, "203.0.113.7");
        var shared = on.Register(new CapturingSocket(), "es", "203.0.113.7");
        shared.UserId = 1000028;
        Assert.False(on.IsAuthor(new ChatService.ChatMessage("1", "1000027", "N", "x", 0, 0, "t"), shared));
        Assert.True(on.IsAuthor(new ChatService.ChatMessage("1", "1000027", "N", "x", 0, 0, "t"), on.Register(new CapturingSocket(), "es", "203.0.113.7")));
    }

    [Fact]
    public void Custom_token_carries_player_id_only_when_translation_is_on()
    {
        var clock = new Clock(T0);
        using var on = NewService(new FakeChat(clock), clock);
        Assert.Equal("kfchat-dummy-token.1000027", on.CustomTokenFor(1000027));
        Assert.Equal("kfchat-dummy-token", on.CustomTokenFor(0));
        Assert.True(ChatService.TryGetUserIdFromCredential("kfchat-dummy-token.42", out var id) && id == 42);
        Assert.False(ChatService.TryGetUserIdFromCredential("kfchat-dummy-token", out _));
        Assert.False(ChatService.TryGetUserIdFromCredential("other.42", out _));
    }
}

// Through Program.cs: the reader-language pieces are registered and read the final configuration; the adapter makes no
// translation call of its own (there is no translator to register).
public sealed class ChatTranslationRegistrationTests
{
    [Fact]
    public void Reader_language_resolution_is_registered_and_honours_the_dev_override()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseContentRoot(AppContext.BaseDirectory).ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:GeoIp:ForceLanguage"] = "es" })));
        var resolver = factory.Services.GetRequiredService<ChatLanguageResolver>();
        Assert.Equal("es", resolver.ForAddress(IPAddress.Parse("8.8.8.8")));
        Assert.NotNull(factory.Services.GetRequiredService<ClientIpResolver>());
        Assert.True(factory.Services.GetRequiredService<ChatTranslationOptions>().Enabled);
    }
}
