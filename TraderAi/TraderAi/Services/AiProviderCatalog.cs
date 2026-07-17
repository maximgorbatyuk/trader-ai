using Microsoft.Extensions.Options;

namespace TraderAi.Services;

public sealed record AiProviderDescriptor(string Id, string Label, Uri Endpoint, IReadOnlyList<string> Models);

// Read-only view over the configured providers. It normalises provider ids to their catalog key so the frontend
// can never persist an unknown provider; the model is free-form text and the per-provider model list is only
// suggestion metadata for the UI.
public sealed class AiProviderCatalog
{
    private readonly IOptions<AiTradingOptions> options;

    public AiProviderCatalog(IOptions<AiTradingOptions> options)
    {
        this.options = options;
    }

    public IReadOnlyList<AiProviderDescriptor> All => Build()
        .Values
        .OrderBy(provider => provider.Id, StringComparer.Ordinal)
        .ToList();

    private Dictionary<string, AiProviderDescriptor> Build()
    {
        var byId = new Dictionary<string, AiProviderDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, provider) in options.Value.Providers)
        {
            var id = key.Trim().ToLowerInvariant();
            byId[id] = new AiProviderDescriptor(
                id,
                provider.DisplayName,
                new Uri(provider.Endpoint),
                provider.Models.ToList());
        }

        return byId;
    }

    public bool TryNormalizeProvider(string? providerId, out string normalizedId)
    {
        normalizedId = string.Empty;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return false;
        }

        var id = providerId.Trim().ToLowerInvariant();
        if (!Build().ContainsKey(id))
        {
            return false;
        }

        normalizedId = id;
        return true;
    }

    public AiProviderDescriptor? Find(string providerId)
    {
        var byId = Build();
        return byId.TryGetValue(providerId, out var descriptor) ? descriptor : null;
    }

    // Resolves the provider's configured API key from the live settings snapshot. It is deliberately kept off
    // AiProviderDescriptor so the provider catalog exposed to clients can never carry a credential.
    public string? FindApiKey(string providerId)
    {
        var id = providerId.Trim().ToLowerInvariant();
        foreach (var (key, provider) in options.Value.Providers)
        {
            if (string.Equals(key.Trim().ToLowerInvariant(), id, StringComparison.Ordinal))
            {
                return provider.ApiKey;
            }
        }

        return null;
    }
}
