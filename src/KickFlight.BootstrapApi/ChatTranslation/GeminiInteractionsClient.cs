using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KickFlight.BootstrapApi.ChatTranslation;

/// <summary>
/// Gemini Interactions API client (POST {BaseUrl}/v1beta/interactions, header x-goog-api-key). Request/response shape
/// was taken from the AI Studio docs on 2026-11-06; error-code semantics beyond 429/5xx/4xx are not verified.
/// The key is sent only as a header and never logged.
/// </summary>
public sealed class GeminiInteractionsClient(HttpClient http, ChatTranslationOptions options, ILogger<GeminiInteractionsClient> logger)
    : ITextGenerationClient
{
    /// <summary>Long-lived client (handler connections recycled every 5 min for DNS changes).</summary>
    public static GeminiInteractionsClient Create(ChatTranslationOptions options, ILogger<GeminiInteractionsClient> logger) =>
        new(new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = TimeSpan.FromSeconds(60) }, options, logger);

    public async Task<GenerationResult> GenerateAsync(GenerationRequest request, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["system_instruction"] = request.SystemInstruction,
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "user_input",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = request.UserText })
            }),
            ["store"] = false,
            ["generation_config"] = new JsonObject
            {
                ["max_output_tokens"] = request.MaxOutputTokens,
                ["thinking_level"] = options.ThinkingLevel
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "text",
                ["mime_type"] = "application/json",
                ["schema"] = request.ResponseSchema.DeepClone()
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, options.BaseUrl + "/v1beta/interactions")
        {
            Content = JsonContent.Create(body)
        };
        message.Headers.Add("x-goog-api-key", options.ApiKey);
        if (!string.IsNullOrWhiteSpace(options.ApiRevision)) message.Headers.Add("Api-Revision", options.ApiRevision);

        using var response = await http.SendAsync(message, cancellationToken);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Gemini request returned HTTP {Status}", status);
            return new GenerationResult(
                ChatTranslator.IsRetryableStatus(response.StatusCode) ? GenerationOutcome.Retryable : GenerationOutcome.Permanent,
                HttpStatus: status, RetryAfter: response.Headers.RetryAfter?.Delta);
        }

        JsonNode? root;
        try { root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)); }
        catch (JsonException) { return new GenerationResult(GenerationOutcome.Permanent, HttpStatus: status); }
        return ParseResponse(root, status);
    }

    /// <summary>Extracts the text of the "model_output" step; status "incomplete" means the token limit was hit.</summary>
    public static GenerationResult ParseResponse(JsonNode? root, int httpStatus = 200)
    {
        try { return ParseResponseCore(root, httpStatus); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return new GenerationResult(GenerationOutcome.Permanent, HttpStatus: httpStatus); // unexpected node types
        }
    }

    private static GenerationResult ParseResponseCore(JsonNode? root, int httpStatus)
    {
        if (root is not JsonObject obj) return new GenerationResult(GenerationOutcome.Permanent, HttpStatus: httpStatus);
        if (string.Equals(obj["status"]?.GetValue<string>(), "incomplete", StringComparison.OrdinalIgnoreCase))
            return new GenerationResult(GenerationOutcome.Incomplete, HttpStatus: httpStatus);

        if (obj["steps"] is JsonArray steps)
        {
            foreach (var step in steps)
            {
                if (step?["type"]?.GetValue<string>() != "model_output" || step["content"] is not JsonArray content) continue;
                foreach (var part in content)
                    if (part?["type"]?.GetValue<string>() == "text" && part["text"]?.GetValue<string>() is { Length: > 0 } text)
                        return new GenerationResult(GenerationOutcome.Success, text, httpStatus);
            }
        }
        return new GenerationResult(GenerationOutcome.Permanent, HttpStatus: httpStatus);
    }
}
