using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using KickFlight.Transport;
using KickFlight.Bootstrap;

var checks = new List<(string Name, Action Run)>
{
    ("cross-language AES-256-CBC/PKCS7 vector decodes", DecodeKnownVector),
    ("cross-language vector encodes byte-for-byte", EncodeKnownVector),
    ("ASCII auth key validation", ValidateAsciiKey),
    ("D2C rejects malformed envelope/key/IV", RejectMalformedInputs),
    ("HTTP transport encrypts request and decrypts successful response", TransportSuccess),
    ("auth bootstrap supports separate request and session response keys", AuthKeyTransition),
    ("manifest URLs stay on origin and redirect responses are rejected", SafeMasterUrlHandling),
    ("callers cannot override the access token with an extra header", RejectDuplicateAccessToken),
    ("API and raw-download responses obey independent byte limits", EnforceResponseLimits),
    ("application-level startup errors are surfaced before decrypt", SurfaceApplicationError),
    ("post-auth bootstrap orders auth, manifest, masters, startup and preserves opaque JSON", BootstrapSuccess),
    ("post-auth bootstrap rejects master hash/size drift", BootstrapRejectsIntegrityDrift),
    ("post-auth bootstrap enforces the server's zero-IV master encoding", BootstrapRejectsNonzeroMasterIv),
    ("post-auth bootstrap propagates cancellation without retrying auth", BootstrapCancellation),
    ("HTTPS is required unless local HTTP is opted in", ValidateTransportOrigin),
};

var failed = 0;
foreach (var check in checks)
{
    try
    {
        check.Run();
        Console.WriteLine("PASS " + check.Name);
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine("FAIL " + check.Name + ": " + exception.Message);
    }
}

Console.WriteLine($"{checks.Count - failed}/{checks.Count} checks passed");
return failed == 0 ? 0 : 1;

static byte[] SyntheticKey() => Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
static byte[] SyntheticIv() => Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();
static byte[] ExpectedEnvelope() => Convert.FromHexString(
    "101112131415161718191a1b1c1d1e1f" +
    "96ba20b0347601822f0fcc77f426fa06e7185e9c82ef1db8da040c1eb03b41b" +
    "024fe43e243f5f80706a6c1dddbf166066015ed4fdaaaca6e56e14535a61c708" +
    "25cf9f71ca0a49b94e4a10ec01e2c7e4d");
static byte[] ExpectedJson() => Encoding.UTF8.GetBytes(
    "{\"assetVersion\":12345,\"smartBeatAvailableFlag\":false,\"rebateUrl\":\"\"}");

static void DecodeKnownVector()
{
    var actual = D2cCodec.Decode(ExpectedEnvelope(), SyntheticKey());
    Equal(ExpectedJson(), actual, "Decoded JSON differs from independent Python cryptography vector.");
}

static void EncodeKnownVector()
{
    var actual = D2cCodec.Encode(ExpectedJson(), SyntheticKey(), SyntheticIv());
    Equal(ExpectedEnvelope(), actual, "Encoded bytes differ from independent Python cryptography vector.");
}

static void ValidateAsciiKey()
{
    var asciiKey = "0123456789ABCDEFGHIJKLMNOPQRSTUV";
    Equal(Encoding.ASCII.GetBytes(asciiKey), D2cCodec.KeyFromAscii32(asciiKey),
        "ASCII key conversion must preserve each ASCII byte.");
    Throws<ArgumentException>(() => D2cCodec.KeyFromAscii32("short"));
    Throws<ArgumentException>(() => D2cCodec.KeyFromAscii32("0123456789ABCDEFGHIJKLMNOPQRSTUé"));
}

static void RejectMalformedInputs()
{
    Throws<ArgumentException>(() => D2cCodec.Encode(Array.Empty<byte>(), new byte[31], new byte[16]));
    Throws<ArgumentException>(() => D2cCodec.Encode(Array.Empty<byte>(), new byte[32], new byte[15]));
    Throws<FormatException>(() => D2cCodec.Decode(new byte[16], new byte[32]));
    Throws<FormatException>(() => D2cCodec.Decode(new byte[33], new byte[32]));
    Throws<ArgumentException>(() => D2cCodec.Decode(new byte[32], new byte[31]));
}

static void TransportSuccess()
{
    var key = SyntheticKey();
    var handler = new TestHandler(async request =>
    {
        Equal(HttpMethod.Post, request.Method, "Expected POST.");
        Equal("https://api.example.invalid/startup/index", request.RequestUri!.AbsoluteUri,
            "Unexpected request URI.");
        Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType,
            "D2C request must be octet-stream.");
        Equal("test-token", request.Headers.GetValues("x-app-access-token").Single(),
            "Access token header was not sent.");
        var decoded = D2cCodec.Decode(await request.Content.ReadAsByteArrayAsync(), key);
        Equal("{\"probe\":7}", Encoding.UTF8.GetString(decoded), "Request JSON mismatch.");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(D2cCodec.Encode(
                Encoding.UTF8.GetBytes("{\"assetVersion\":12345}"), key, SyntheticIv()))
        };
        response.Headers.TryAddWithoutValidation("x-app-status-code", "0");
        response.Headers.TryAddWithoutValidation("x-app-user-id", "synthetic-user");
        response.Headers.TryAddWithoutValidation("x-app-access-token", "next-synthetic-token");
        response.Headers.TryAddWithoutValidation("x-app-master-hash", "synthetic-master-hash");
        return response;
    });

    using var client = new KickFlightApiClient(handler, new Uri("https://api.example.invalid"));
    var result = client.PostD2cJsonAsync("startup/index", "{\"probe\":7}", key, "test-token")
        .GetAwaiter().GetResult();
    Equal("{\"assetVersion\":12345}", result, "Response JSON mismatch.");
    Equal("synthetic-user", handler.LastResponseUserId, "Response metadata mismatch.");
}

static void SurfaceApplicationError()
{
    var handler = new TestHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        // Forced-update and other application errors can be plaintext; status must be checked before D2C decode.
        Content = new StringContent("{\"error\":{\"code\":\"1400\"}}", Encoding.UTF8, "application/json")
    }), addAppStatus: "1");
    using var client = new KickFlightApiClient(handler, new Uri("https://api.example.invalid"));
    try
    {
        client.PostD2cJsonAsync("boot/index", "{}", SyntheticKey()).GetAwaiter().GetResult();
        throw new Exception("Expected an application error.");
    }
    catch (KickFlightApiException exception)
    {
        Equal("boot/index", exception.Path, "Error path missing.");
        Equal(HttpStatusCode.OK, exception.HttpStatus, "HTTP status missing.");
        Equal("1", exception.ApplicationStatus, "Application status missing.");
        Equal(25, exception.ResponseBodyLength, "Response body length mismatch.");
    }
}

static void BootstrapSuccess()
{
    var commonKey = D2cCodec.KeyFromAscii32("synthetic-common-key-for-tests!!");
    var sessionKeyText = "synthetic-session-key-for-tests!";
    var sessionKey = D2cCodec.KeyFromAscii32(sessionKeyText);
    const string tableJson = "[{\"id\":1,\"futureColumn\":{\"unknown\":[1,2]}}]";
    const string startupJson = "{\"userKickerList\":[],\"tutorialProgressStatus\":206,\"futureField\":{\"opaque\":true}}";
    var encryptedMaster = D2cCodec.Encode(Encoding.UTF8.GetBytes(tableJson), commonKey, new byte[16]);
    Equal(new byte[16], encryptedMaster.Take(16).ToArray(), "Server master IV fixture must be all zero.");
    var masterHash = Sha256Hex(encryptedMaster);
    var manifest = JsonSerializer.Serialize(new
    {
        masterDownloadList = new[]
        {
            new { name = "Kicker", hash = masterHash, url = "https://api.example.invalid/demo-master/Kicker", size = encryptedMaster.Length }
        }
    });
    var sequence = new List<string>();
    var handler = new TestHandler(async request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        sequence.Add(request.Method.Method + " " + path);
        if (path == "/auth/index")
        {
            var requestJson = Encoding.UTF8.GetString(D2cCodec.Decode(
                await request.Content!.ReadAsByteArrayAsync(), commonKey));
            using var authRequest = JsonDocument.Parse(requestJson);
            Equal(sessionKeyText, authRequest.RootElement.GetProperty("hash").GetString(), "Auth hash mismatch.");
            Equal("synthetic-device", authRequest.RootElement.GetProperty("uuid").GetString(), "Auth UUID mismatch.");
            var response = EncryptedJson("{}", sessionKey, appStatus: "0");
            response.Headers.TryAddWithoutValidation("x-app-access-token", "synthetic-session-token");
            response.Headers.TryAddWithoutValidation("x-app-user-id", "1000007");
            return response;
        }
        if (path == "/download/master")
        {
            Equal("synthetic-session-token", request.Headers.GetValues("x-app-access-token").Single(),
                "Manifest request missed session token.");
            Equal("{}", Encoding.UTF8.GetString(D2cCodec.Decode(
                await request.Content!.ReadAsByteArrayAsync(), sessionKey)), "Manifest request body mismatch.");
            var response = EncryptedJson(manifest, sessionKey, appStatus: "0");
            response.Headers.TryAddWithoutValidation("x-app-master-hash", "synthetic-master-version");
            return response;
        }
        if (path == "/demo-master/Kicker")
        {
            if (request.Headers.Contains("x-app-access-token"))
                throw new Exception("Master GET must not carry an access token.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(encryptedMaster) };
        }
        if (path == "/startup/index")
        {
            Equal("synthetic-session-token", request.Headers.GetValues("x-app-access-token").Single(),
                "Startup request missed session token.");
            return EncryptedJson(startupJson, sessionKey, appStatus: "0");
        }
        throw new Exception("Unexpected bootstrap request: " + path);
    });

    using var api = new KickFlightApiClient(handler, new Uri("https://api.example.invalid"));
    var bootstrapper = new AuthenticatedBootstrapper(api, new TestBootstrapJson());
    var result = bootstrapper.RunAsync(new AuthenticatedBootstrapInput(commonKey, sessionKeyText,
        "synthetic-device")).GetAwaiter().GetResult();

    Equal(new[] { "POST /auth/index", "POST /download/master", "GET /demo-master/Kicker", "POST /startup/index" },
        sequence.ToArray(), "Bootstrap request order mismatch.");
    Equal("1000007", result.UserId, "User id response header missing.");
    Equal("synthetic-session-token", result.AccessToken, "Access token response header missing.");
    Equal("synthetic-master-version", result.MasterHash, "Master version header missing.");
    Equal(tableJson, result.MasterJsonByName["Kicker"], "Master JSON was not preserved after verified decode.");
    Equal(startupJson, result.StartupResponseJson, "Startup JSON with unknown fields was changed.");
}

static void BootstrapRejectsIntegrityDrift()
{
    var commonKey = D2cCodec.KeyFromAscii32("synthetic-common-key-for-tests!!");
    var sessionKeyText = "synthetic-session-key-for-tests!";
    var sessionKey = D2cCodec.KeyFromAscii32(sessionKeyText);
    var encryptedMaster = D2cCodec.Encode(Encoding.UTF8.GetBytes("[]"), commonKey, new byte[16]);
    var manifest = JsonSerializer.Serialize(new
    {
        masterDownloadList = new[]
        {
            new { name = "Kicker", hash = new string('0', 64), url = "https://api.example.invalid/demo-master/Kicker", size = encryptedMaster.Length }
        }
    });
    var calls = new List<string>();
    var handler = new TestHandler(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        calls.Add(path);
        if (path == "/auth/index")
        {
            var response = EncryptedJson("{}", sessionKey, "0");
            response.Headers.TryAddWithoutValidation("x-app-access-token", "synthetic-token");
            response.Headers.TryAddWithoutValidation("x-app-user-id", "1000007");
            return Task.FromResult(response);
        }
        if (path == "/download/master") return Task.FromResult(EncryptedJson(manifest, sessionKey, "0"));
        if (path == "/demo-master/Kicker")
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(encryptedMaster) });
        throw new Exception("Startup must not run after bad master integrity.");
    });
    using var api = new KickFlightApiClient(handler, new Uri("https://api.example.invalid"));
    var bootstrapper = new AuthenticatedBootstrapper(api, new TestBootstrapJson());
    try
    {
        bootstrapper.RunAsync(new AuthenticatedBootstrapInput(commonKey, sessionKeyText, "synthetic-device"))
            .GetAwaiter().GetResult();
        throw new Exception("Expected master integrity rejection.");
    }
    catch (BootstrapIntegrityException exception)
    {
        Equal("Kicker", exception.MasterName, "Integrity error should identify the table.");
        Equal(new[] { "/auth/index", "/download/master", "/demo-master/Kicker" }, calls.ToArray(),
            "A later startup request ran after integrity failure.");
    }

    var sizeManifest = JsonSerializer.Serialize(new
    {
        masterDownloadList = new[]
        {
            new { name = "Kicker", hash = Sha256Hex(encryptedMaster), url = "https://api.example.invalid/demo-master/Kicker", size = encryptedMaster.Length + 1 }
        }
    });
    var sizeHandler = new TestHandler(request =>
    {
        if (request.RequestUri!.AbsolutePath == "/auth/index")
        {
            var response = EncryptedJson("{}", sessionKey, "0");
            response.Headers.TryAddWithoutValidation("x-app-access-token", "synthetic-token");
            response.Headers.TryAddWithoutValidation("x-app-user-id", "1000007");
            return Task.FromResult(response);
        }
        if (request.RequestUri.AbsolutePath == "/download/master")
            return Task.FromResult(EncryptedJson(sizeManifest, sessionKey, "0"));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(encryptedMaster) });
    });
    using var sizeApi = new KickFlightApiClient(sizeHandler, new Uri("https://api.example.invalid"));
    try
    {
        new AuthenticatedBootstrapper(sizeApi, new TestBootstrapJson())
            .RunAsync(new AuthenticatedBootstrapInput(commonKey, sessionKeyText, "synthetic-device"))
            .GetAwaiter().GetResult();
        throw new Exception("Expected master size rejection.");
    }
    catch (BootstrapIntegrityException exception)
    {
        Equal("Kicker", exception.MasterName, "Size error should identify the table.");
    }
}

static void BootstrapCancellation()
{
    var commonKey = D2cCodec.KeyFromAscii32("synthetic-common-key-for-tests!!");
    var sessionKeyText = "synthetic-session-key-for-tests!";
    var sessionKey = D2cCodec.KeyFromAscii32(sessionKeyText);
    var handler = new BootstrapCancellationHandler(sessionKey);
    using var api = new KickFlightApiClient(handler, new Uri("https://api.example.invalid"));
    using var cancellation = new CancellationTokenSource();
    var bootstrap = new AuthenticatedBootstrapper(api, new TestBootstrapJson())
        .RunAsync(new AuthenticatedBootstrapInput(commonKey, sessionKeyText, "synthetic-device"), cancellation.Token);
    handler.ManifestStarted.Task.GetAwaiter().GetResult();
    cancellation.Cancel();
    try
    {
        bootstrap.GetAwaiter().GetResult();
        throw new Exception("Expected cancellation.");
    }
    catch (OperationCanceledException) { }
    Equal(1, handler.AuthCalls, "Auth was retried after cancellation.");
    Equal(1, handler.ManifestCalls, "Manifest request was retried after cancellation.");
    Equal(0, handler.StartupCalls, "Startup should not run after cancellation.");
}

static void BootstrapRejectsNonzeroMasterIv()
{
    var commonKey = D2cCodec.KeyFromAscii32("synthetic-common-key-for-tests!!");
    var sessionKeyText = "synthetic-session-key-for-tests!";
    var sessionKey = D2cCodec.KeyFromAscii32(sessionKeyText);
    var encryptedMaster = D2cCodec.Encode(Encoding.UTF8.GetBytes("[]"), commonKey, SyntheticIv());
    var manifest = JsonSerializer.Serialize(new
    {
        masterDownloadList = new[]
        {
            new { name = "Kicker", hash = Sha256Hex(encryptedMaster), url = "https://api.example.invalid/demo-master/Kicker", size = encryptedMaster.Length }
        }
    });
    var handler = new TestHandler(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/auth/index")
        {
            var response = EncryptedJson("{}", sessionKey, "0");
            response.Headers.TryAddWithoutValidation("x-app-access-token", "synthetic-token");
            response.Headers.TryAddWithoutValidation("x-app-user-id", "1000007");
            return Task.FromResult(response);
        }
        if (path == "/download/master") return Task.FromResult(EncryptedJson(manifest, sessionKey, "0"));
        if (path == "/demo-master/Kicker")
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(encryptedMaster) });
        throw new Exception("Startup must not run after master IV mismatch.");
    });
    using var api = new KickFlightApiClient(handler, new Uri("https://api.example.invalid"));
    try
    {
        new AuthenticatedBootstrapper(api, new TestBootstrapJson())
            .RunAsync(new AuthenticatedBootstrapInput(commonKey, sessionKeyText, "synthetic-device"))
            .GetAwaiter().GetResult();
        throw new Exception("Expected zero-IV contract rejection.");
    }
    catch (BootstrapProtocolException exception)
    {
        if (!exception.Message.Contains("zero-IV", StringComparison.Ordinal))
            throw new Exception("Unexpected protocol failure: " + exception.Message);
    }
}

static HttpResponseMessage EncryptedJson(string json, byte[] key, string appStatus)
{
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(D2cCodec.Encode(Encoding.UTF8.GetBytes(json), key, SyntheticIv()))
    };
    response.Headers.TryAddWithoutValidation("x-app-status-code", appStatus);
    return response;
}

static string Sha256Hex(byte[] bytes)
{
    using var sha = SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
}

static void SafeMasterUrlHandling()
{
    var handler = new TestHandler(request =>
    {
        Equal("https://api.example.invalid/demo-master/Kicker", request.RequestUri!.AbsoluteUri,
            "Manifest URL was not resolved as expected.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x01, 0x02, 0x03 })
        });
    });
    using (var client = new KickFlightApiClient(handler, new Uri("https://api.example.invalid")))
    {
        var bytes = client.GetSameOriginBytesAsync(new Uri("https://api.example.invalid/demo-master/Kicker"))
            .GetAwaiter().GetResult();
        Equal(new byte[] { 0x01, 0x02, 0x03 }, bytes, "Master bytes were changed.");
        Throws<ArgumentException>(() => client.GetSameOriginBytesAsync(
            new Uri("https://other.example.invalid/demo-master/Kicker")).GetAwaiter().GetResult());
        Throws<ArgumentException>(() => client.GetSameOriginBytesAsync(
            new Uri("https://user@api.example.invalid/demo-master/Kicker")).GetAwaiter().GetResult());
    }

    var redirect = new TestHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
    {
        Headers = { Location = new Uri("https://other.example.invalid/collect") }
    }));
    using var redirectClient = new KickFlightApiClient(redirect, new Uri("https://api.example.invalid"));
    try
    {
        redirectClient.GetBytesAsync("demo-master/Kicker").GetAwaiter().GetResult();
        throw new Exception("Expected redirect rejection.");
    }
    catch (KickFlightProtocolException) { }
}

static void RejectDuplicateAccessToken()
{
    var handler = new TestHandler(_ => throw new Exception("Request must be rejected before sending."));
    using var client = new KickFlightApiClient(handler, new Uri("https://api.example.invalid"));
    Throws<ArgumentException>(() => client.PostD2cJsonAsync("home/index", "{}", SyntheticKey(),
        additionalHeaders: new Dictionary<string, string> { ["X-App-Access-Token"] = "duplicate" })
        .GetAwaiter().GetResult());
}

static void EnforceResponseLimits()
{
    var limits = new KickFlightTransportLimits(maxApiResponseBytes: 4, maxDownloadResponseBytes: 4);
    var apiHandler = new TestHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(new byte[5])
    }));
    apiHandler.ResponseHeadersForNextResponse = "0";
    using (var client = new KickFlightApiClient(apiHandler, new Uri("https://api.example.invalid"), limits: limits))
    {
        try
        {
            client.PostD2cAsync("startup/index", D2cCodec.Encode(new byte[] { 0x7b, 0x7d }, SyntheticKey(), SyntheticIv()), SyntheticKey())
                .GetAwaiter().GetResult();
            throw new Exception("Expected API response limit rejection.");
        }
        catch (KickFlightProtocolException) { }
    }

    var downloadHandler = new TestHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new NonSeekableStream(new byte[5]))
    }));
    using var downloadClient = new KickFlightApiClient(downloadHandler,
        new Uri("https://api.example.invalid"), limits: limits);
    try
    {
        downloadClient.GetBytesAsync("cdn/object").GetAwaiter().GetResult();
        throw new Exception("Expected chunked download limit rejection.");
    }
    catch (KickFlightProtocolException) { }
}

static void AuthKeyTransition()
{
    var commonKey = D2cCodec.KeyFromAscii32("synthetic-common-key-for-tests!!");
    var sessionKey = D2cCodec.KeyFromAscii32("synthetic-session-key-for-tests!");
    var handler = new TestHandler(async request =>
    {
        var requestJson = Encoding.UTF8.GetString(
            D2cCodec.Decode(await request.Content!.ReadAsByteArrayAsync(), commonKey));
        Equal("{\"hash\":\"synthetic-session-key-for-tests!\",\"uuid\":\"test-device\"}",
            requestJson, "Auth request must use the common key.");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(D2cCodec.Encode(Encoding.UTF8.GetBytes("{}"), sessionKey, SyntheticIv()))
        };
        response.Headers.TryAddWithoutValidation("x-app-status-code", "0");
        response.Headers.TryAddWithoutValidation("x-app-access-token", "synthetic-issued-token");
        return response;
    });
    using var client = new KickFlightApiClient(handler, new Uri("https://api.example.invalid"));
    var result = client.PostD2cJsonWithKeysAsync("auth/index",
        "{\"hash\":\"synthetic-session-key-for-tests!\",\"uuid\":\"test-device\"}",
        commonKey, sessionKey).GetAwaiter().GetResult();
    Equal("{}", result.GetJsonText(), "Auth response must decrypt with the session key.");
    Equal("synthetic-issued-token", result.AccessToken, "Auth response token header missing.");
}

static void ValidateTransportOrigin()
{
    Throws<ArgumentException>(() => new KickFlightApiClient(new Uri("http://api.example.invalid")));
    using var client = new KickFlightApiClient(new Uri("http://127.0.0.1:18080"), allowInsecureHttpForDevelopment: true);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (expected is byte[] expectedBytes && actual is byte[] actualBytes)
    {
        if (!expectedBytes.SequenceEqual(actualBytes)) throw new Exception(message);
        return;
    }
    if (expected is string[] expectedStrings && actual is string[] actualStrings)
    {
        if (!expectedStrings.SequenceEqual(actualStrings)) throw new Exception(message);
        return;
    }
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(message);
}

static void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new Exception("Expected " + typeof(TException).Name + ".");
}

sealed class TestHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _response;
    private readonly string? _appStatus;
    public string? LastResponseUserId { get; private set; }
    public string? ResponseHeadersForNextResponse { get; set; }

    public TestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response, string? addAppStatus = null)
    {
        _response = response;
        _appStatus = addAppStatus;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await _response(request);
        var appStatus = ResponseHeadersForNextResponse ?? _appStatus;
        if (appStatus != null) response.Headers.TryAddWithoutValidation("x-app-status-code", appStatus);
        if (response.Headers.TryGetValues("x-app-user-id", out var ids)) LastResponseUserId = ids.Single();
        return response;
    }
}

sealed class TestBootstrapJson : IKickFlightBootstrapJson
{
    public string CreateAuthIndexRequest(string sessionKeyAscii32, string deviceUuid) =>
        JsonSerializer.Serialize(new { hash = sessionKeyAscii32, uuid = deviceUuid });

    public IReadOnlyList<MasterDownloadEntry> ParseMasterManifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.GetProperty("masterDownloadList");
        var result = new List<MasterDownloadEntry>();
        foreach (var row in rows.EnumerateArray())
        {
            result.Add(new MasterDownloadEntry(
                row.GetProperty("name").GetString()!,
                row.GetProperty("hash").GetString()!,
                new Uri(row.GetProperty("url").GetString()!, UriKind.Absolute),
                row.GetProperty("size").GetInt64()));
        }
        return result;
    }
}

sealed class BootstrapCancellationHandler : HttpMessageHandler
{
    private readonly byte[] _sessionKey;
    public TaskCompletionSource<bool> ManifestStarted { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    public int AuthCalls { get; private set; }
    public int ManifestCalls { get; private set; }
    public int StartupCalls { get; private set; }

    public BootstrapCancellationHandler(byte[] sessionKey) => _sessionKey = sessionKey;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        switch (request.RequestUri!.AbsolutePath)
        {
            case "/auth/index":
                AuthCalls++;
                var auth = EncryptedResponse("{}", _sessionKey);
                auth.Headers.TryAddWithoutValidation("x-app-access-token", "synthetic-token");
                auth.Headers.TryAddWithoutValidation("x-app-user-id", "1000007");
                return auth;
            case "/download/master":
                ManifestCalls++;
                ManifestStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("Unreachable after cancellation.");
            case "/startup/index":
                StartupCalls++;
                throw new Exception("Startup ran after cancelled manifest request.");
            default:
                throw new Exception("Unexpected cancellation test route.");
        }
    }

    private static HttpResponseMessage EncryptedResponse(string json, byte[] key)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(D2cCodec.Encode(Encoding.UTF8.GetBytes(json), key,
                Enumerable.Range(16, 16).Select(i => (byte)i).ToArray()))
        };
        response.Headers.TryAddWithoutValidation("x-app-status-code", "0");
        return response;
    }
}

sealed class NonSeekableStream : Stream
{
    private readonly MemoryStream _inner;
    public NonSeekableStream(byte[] bytes) => _inner = new MemoryStream(bytes);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
