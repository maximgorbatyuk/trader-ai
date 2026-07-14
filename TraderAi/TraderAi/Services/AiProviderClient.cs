using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TraderAi.Services;

// One OpenAI-compatible chat client for every provider. Preparation is split from sending so the exact
// credential-free body is captured before the key is ever touched; the key appears only in the Authorization
// header at send time. Both GLM and MiniMax return the same OpenAI-shaped envelope, so only request shaping
// (disabling provider "thinking") differs. Provider request/response details must be confirmed against live
// provider documentation; the shaping here is validated only against a fake transport in tests.
public sealed class AiProviderClient(IHttpClientFactory httpClientFactory, IOptions<AiTradingOptions> options)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General);

    public PreparedAiProviderRequest Prepare(
        AiProviderDescriptor provider,
        string model,
        string systemMessage,
        string userMessage)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = false,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemMessage },
                new { role = "user", content = userMessage },
            },
        };

        // GLM exposes an explicit switch to suppress chain-of-thought; MiniMax returns reasoning in a separate
        // field we simply never read, so no request flag is needed there.
        if (provider.Id == "glm")
        {
            payload["thinking"] = new { type = "disabled" };
        }

        var requestJson = JsonSerializer.Serialize(payload, SerializerOptions);
        return new PreparedAiProviderRequest(provider.Id, provider.Label, model, provider.Endpoint, requestJson);
    }

    public Task<AiProviderResponse> SendTestAsync(
        AiProviderDescriptor provider,
        string model,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var prepared = Prepare(provider, model, "You are a helpful assistant.", "Who are you");
        return SendAsync(prepared, apiKey, cancellationToken);
    }

    public async Task<AiProviderResponse> SendAsync(
        PreparedAiProviderRequest prepared,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("ai-provider");
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.RequestTimeoutSeconds)));

        using var request = new HttpRequestMessage(HttpMethod.Post, prepared.Endpoint)
        {
            Content = new StringContent(prepared.RequestJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(AiProviderCallOutcome.Cancelled, null, null, "The request was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return Failure(AiProviderCallOutcome.TimedOut, null, null, "The request timed out.");
        }
        catch (HttpRequestException exception)
        {
            return Failure(AiProviderCallOutcome.HttpError, null, null, exception.Message);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Failure(AiProviderCallOutcome.HttpError, status, body, $"Provider returned HTTP {status}.", RetryAfterOf(response));
            }

            return ParseSuccess(status, body);
        }
    }

    private static AiProviderResponse ParseSuccess(int status, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            string? content = null;
            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String)
            {
                content = contentElement.GetString();
            }

            int? promptTokens = null;
            int? completionTokens = null;
            int? totalTokens = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                promptTokens = ReadInt(usage, "prompt_tokens");
                completionTokens = ReadInt(usage, "completion_tokens");
                totalTokens = ReadInt(usage, "total_tokens");
            }

            if (string.IsNullOrEmpty(content))
            {
                return new AiProviderResponse(
                    AiProviderCallOutcome.MalformedResponse, status, body, null,
                    promptTokens, completionTokens, totalTokens, null,
                    "The provider response contained no assistant content.");
            }

            return new AiProviderResponse(
                AiProviderCallOutcome.Success, status, body, content,
                promptTokens, completionTokens, totalTokens, null, null);
        }
        catch (JsonException exception)
        {
            return new AiProviderResponse(
                AiProviderCallOutcome.MalformedResponse, status, body, null, null, null, null, null, exception.Message);
        }
    }

    private static int? ReadInt(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
                ? parsed
                : null;

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retryAfter)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }

        return retryAfter.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    private static AiProviderResponse Failure(
        AiProviderCallOutcome outcome,
        int? status,
        string? body,
        string error,
        TimeSpan? retryAfter = null)
        => new(outcome, status, body, null, null, null, null, retryAfter, error);
}
