using Microsoft.Extensions.Options;

namespace TraderAi.Services;

public sealed record AiProviderDescriptor(string Id, string Label, Uri Endpoint, IReadOnlyList<string> Models);

// Read-only view over the configured providers. It normalises provider ids to their catalog key and answers
// whether a chosen model belongs to a provider, so the frontend can never persist an unknown provider or model.
public sealed class AiProviderCatalog
{
    private readonly Dictionary<string, AiProviderDescriptor> byId;

    public AiProviderCatalog(IOptions<AiTradingOptions> options)
    {
        byId = new Dictionary<string, AiProviderDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, provider) in options.Value.Providers)
        {
            var id = key.Trim().ToLowerInvariant();
            byId[id] = new AiProviderDescriptor(
                id,
                provider.DisplayName,
                new Uri(provider.Endpoint),
                provider.Models.ToList());
        }
    }

    public IReadOnlyList<AiProviderDescriptor> All => byId.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();

    public bool TryNormalizeProvider(string? providerId, out string normalizedId)
    {
        normalizedId = string.Empty;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return false;
        }

        var id = providerId.Trim().ToLowerInvariant();
        if (!byId.ContainsKey(id))
        {
            return false;
        }

        normalizedId = id;
        return true;
    }

    public AiProviderDescriptor? Find(string providerId)
        => byId.TryGetValue(providerId, out var descriptor) ? descriptor : null;

    public bool IsModelValid(string providerId, string model)
        => byId.TryGetValue(providerId, out var descriptor)
            && descriptor.Models.Any(candidate => string.Equals(candidate, model, StringComparison.OrdinalIgnoreCase));
}
