using System.Net;
using System.Text.Json.Nodes;

namespace KickFlight.BootstrapApi.ChatTranslation;

public enum GenerationOutcome { Success, Incomplete, Retryable, Permanent }

public sealed record GenerationRequest(string SystemInstruction, string UserText, int MaxOutputTokens, JsonObject ResponseSchema);

/// <summary>Outcome of one model call. HttpStatus is only for logging; Text is the model's JSON text on Success.</summary>
public sealed record GenerationResult(GenerationOutcome Outcome, string? Text = null, int? HttpStatus = null, TimeSpan? RetryAfter = null);

/// <summary>Isolates the concrete model API (currently the Gemini Interactions API) so it can be swapped or mocked.</summary>
public interface ITextGenerationClient
{
    Task<GenerationResult> GenerateAsync(GenerationRequest request, CancellationToken cancellationToken);
}

public interface IChatTranslator
{
    bool Enabled { get; }
    /// <summary>Translations keyed pt/es/ja/en, or null on any failure. Never throws.</summary>
    Task<IReadOnlyDictionary<string, string>?> TranslateAsync(ChatTranslationRequest request, CancellationToken cancellationToken = default);
}

public sealed class DisabledChatTranslator : IChatTranslator
{
    public bool Enabled => false;
    public Task<IReadOnlyDictionary<string, string>?> TranslateAsync(ChatTranslationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(null);
}

/// <summary>
/// Bounded translation pipeline: at most <c>MaxConcurrency</c> calls in flight, at most <c>MaxPending</c> waiting
/// (excess is dropped so readers get the original), a per-attempt timeout and one retry with backoff on
/// 429/5xx/transport errors. Permanent (other 4xx), incomplete and malformed outputs are not retried.
/// </summary>
public sealed class ChatTranslator : IChatTranslator
{
    private readonly ITextGenerationClient _client;
    private readonly ChatTranslationOptions _options;
    private readonly ILogger<ChatTranslator> _logger;
    private readonly SemaphoreSlim _slots;
    private int _pending;

    public ChatTranslator(ITextGenerationClient client, ChatTranslationOptions options, ILogger<ChatTranslator> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
        _slots = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));
    }

    public bool Enabled => true;

    public async Task<IReadOnlyDictionary<string, string>?> TranslateAsync(ChatTranslationRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            // Nothing to translate in digits/emoji/punctuation-only text: same text for everyone, no API call.
            if (!request.Text.Any(char.IsLetter))
                return ChatLanguages.All.ToDictionary(l => l, _ => request.Text, StringComparer.Ordinal);

            if (Interlocked.Increment(ref _pending) > _options.MaxPending)
            {
                _logger.LogWarning("Chat translation queue full; skipping translation");
                return null;
            }
            try
            {
                await _slots.WaitAsync(cancellationToken);
                try { return await RunAsync(request, cancellationToken); }
                finally { _slots.Release(); }
            }
            finally { Interlocked.Decrement(ref _pending); }
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat translation failed unexpectedly");
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>?> RunAsync(ChatTranslationRequest request, CancellationToken ct)
    {
        var generation = new GenerationRequest(ChatTranslationPrompt.SystemInstruction,
            ChatTranslationPrompt.BuildUserText(request, _options.ContextMessages), _options.MaxOutputTokens,
            ChatTranslationPrompt.ResponseSchema());

        for (var attempt = 0; attempt < 2; attempt++)
        {
            GenerationResult result;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
                try { result = await _client.GenerateAsync(generation, timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    result = new GenerationResult(GenerationOutcome.Retryable); // our per-attempt timeout
                }
                catch (HttpRequestException)
                {
                    result = new GenerationResult(GenerationOutcome.Retryable);
                }
            }

            switch (result.Outcome)
            {
                case GenerationOutcome.Success:
                {
                    var parsed = ChatTranslationParser.Parse(result.Text, out var error);
                    if (parsed is null) _logger.LogWarning("Chat translation output rejected: {Reason}", error);
                    return parsed;
                }
                case GenerationOutcome.Incomplete:
                    _logger.LogWarning("Chat translation incomplete (token limit)");
                    return null;
                case GenerationOutcome.Permanent:
                    _logger.LogWarning("Chat translation permanent failure, HTTP {Status}", result.HttpStatus);
                    return null;
                default:
                    if (attempt == 1)
                    {
                        _logger.LogWarning("Chat translation failed after retry, HTTP {Status}", result.HttpStatus);
                        return null;
                    }
                    var delay = result.RetryAfter is { } ra && ra < TimeSpan.FromSeconds(3)
                        ? ra : TimeSpan.FromMilliseconds(_options.RetryDelayMs);
                    await Task.Delay(delay, ct);
                    break;
            }
        }
        return null;
    }

    /// <summary>Retry classification shared with HTTP clients: 429 and 5xx are transient, other errors permanent.</summary>
    public static bool IsRetryableStatus(HttpStatusCode status) => (int)status == 429 || (int)status >= 500;
}
