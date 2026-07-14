using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AiProviderClientTests
{
    private static readonly AiProviderDescriptor Glm =
        new("glm", "GLM", new Uri("https://glm.test/v1/chat/completions"), new[] { "glm-4.6" });

    private static readonly AiProviderDescriptor MiniMax =
        new("minimax", "MiniMax", new Uri("https://minimax.test/v1/chat/completions"), new[] { "MiniMax-M2" });

    [Fact]
    public void PreparedRequestUsesEndpointAndModelAndDisablesThinkingForGlm()
    {
        var client = Client(out _);
        var prepared = client.Prepare(Glm, "glm-4.6", "system message", "user message");

        Assert.Equal(Glm.Endpoint, prepared.Endpoint);
        Assert.Equal("glm-4.6", prepared.Model);
        Assert.Equal("GLM", prepared.ProviderLabel);
        Assert.Contains("\"model\":\"glm-4.6\"", prepared.RequestJson);
        Assert.Contains("\"role\":\"system\"", prepared.RequestJson);
        Assert.Contains("\"role\":\"user\"", prepared.RequestJson);
        Assert.Contains("thinking", prepared.RequestJson);
        Assert.Contains("disabled", prepared.RequestJson);
    }

    [Fact]
    public void PreparedMiniMaxRequestHasNoThinkingFlag()
    {
        var client = Client(out _);
        var prepared = client.Prepare(MiniMax, "MiniMax-M2", "system message", "user message");

        Assert.Equal(MiniMax.Endpoint, prepared.Endpoint);
        Assert.DoesNotContain("thinking", prepared.RequestJson);
    }

    [Theory]
    [InlineData("glm")]
    [InlineData("minimax")]
    public async Task SendPlacesKeyOnlyInAuthorizationHeader(string providerId)
    {
        var provider = providerId == "glm" ? Glm : MiniMax;
        var model = provider.Models[0];
        var handler = new StubHandler((_, _) => Task.FromResult(Ok(Envelope("hello"))));
        var client = Client(handler);

        var prepared = client.Prepare(provider, model, "system", "user");
        var response = await client.SendAsync(prepared, "secret-key", CancellationToken.None);

        Assert.Equal(AiProviderCallOutcome.Success, response.Outcome);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("secret-key", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.DoesNotContain("secret-key", handler.LastBody);
        Assert.DoesNotContain("secret-key", prepared.RequestJson);
    }

    [Fact]
    public async Task SuccessReturnsContentTokensAndRawBody()
    {
        var body = Envelope("I am a model.");
        var handler = new StubHandler((_, _) => Task.FromResult(Ok(body)));
        var client = Client(handler);

        var prepared = client.Prepare(Glm, "glm-4.6", "system", "user");
        var response = await client.SendAsync(prepared, "key", CancellationToken.None);

        Assert.Equal(AiProviderCallOutcome.Success, response.Outcome);
        Assert.Equal("I am a model.", response.AssistantContent);
        Assert.Equal(11, response.PromptTokens);
        Assert.Equal(22, response.CompletionTokens);
        Assert.Equal(33, response.TotalTokens);
        Assert.Equal(body, response.RawBody);
    }

    [Fact]
    public async Task HiddenReasoningIsNotReturnedAsContent()
    {
        var body = Envelope("final answer", reasoning: "hidden chain of thought");
        var handler = new StubHandler((_, _) => Task.FromResult(Ok(body)));
        var client = Client(handler);

        var prepared = client.Prepare(Glm, "glm-4.6", "system", "user");
        var response = await client.SendAsync(prepared, "key", CancellationToken.None);

        Assert.Equal("final answer", response.AssistantContent);
        Assert.DoesNotContain("hidden chain of thought", response.AssistantContent);
    }

    [Fact]
    public async Task OnlySystemAndUserMessagesAreSent()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Ok(Envelope("hi"))));
        var client = Client(handler);

        var prepared = client.Prepare(Glm, "glm-4.6", "system", "user");
        await client.SendAsync(prepared, "key", CancellationToken.None);

        using var document = JsonDocument.Parse(handler.LastBody!);
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task NonSuccessHttpRetainsStatusAndBody()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("unauthorized") }));
        var client = Client(handler);

        var prepared = client.Prepare(Glm, "glm-4.6", "system", "user");
        var response = await client.SendAsync(prepared, "key", CancellationToken.None);

        Assert.Equal(AiProviderCallOutcome.HttpError, response.Outcome);
        Assert.Equal(401, response.HttpStatusCode);
        Assert.Equal("unauthorized", response.RawBody);
    }

    [Fact]
    public async Task MalformedEnvelopeIsDistinguished()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Ok("""{ "unexpected": true }""")));
        var client = Client(handler);

        var prepared = client.Prepare(Glm, "glm-4.6", "system", "user");
        var response = await client.SendAsync(prepared, "key", CancellationToken.None);

        Assert.Equal(AiProviderCallOutcome.MalformedResponse, response.Outcome);
        Assert.Equal("""{ "unexpected": true }""", response.RawBody);
    }

    [Fact]
    public async Task CancellationIsDistinguished()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Ok(Envelope("hi"))));
        var client = Client(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var prepared = client.Prepare(Glm, "glm-4.6", "system", "user");
        var response = await client.SendAsync(prepared, "key", cts.Token);

        Assert.Equal(AiProviderCallOutcome.Cancelled, response.Outcome);
    }

    [Fact]
    public async Task TimeoutIsDistinguished()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok(Envelope("hi"));
        });
        var client = Client(handler, timeoutSeconds: 1);

        var prepared = client.Prepare(Glm, "glm-4.6", "system", "user");
        var response = await client.SendAsync(prepared, "key", CancellationToken.None);

        Assert.Equal(AiProviderCallOutcome.TimedOut, response.Outcome);
    }

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string Envelope(string content, string? reasoning = null)
    {
        var messageFields = "\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(content);
        if (reasoning is not null)
        {
            messageFields += ",\"reasoning_content\":" + JsonSerializer.Serialize(reasoning);
        }

        return "{\"choices\":[{\"message\":{" + messageFields
            + "}}],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":22,\"total_tokens\":33}}";
    }

    private static AiProviderClient Client(int timeoutSeconds = 120)
        => Client(new StubHandler((_, _) => Task.FromResult(Ok("{}"))), timeoutSeconds);

    private static AiProviderClient Client(out StubHandler handler)
    {
        handler = new StubHandler((_, _) => Task.FromResult(Ok("{}")));
        return Client(handler);
    }

    private static AiProviderClient Client(StubHandler handler, int timeoutSeconds = 120)
        => new(
            new FakeHttpClientFactory(handler),
            Options.Create(new AiTradingOptions { RequestTimeoutSeconds = timeoutSeconds }));

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return await responder(request, cancellationToken);
        }
    }
}
