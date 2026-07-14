using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TraderAi.Services;

public sealed record AiPromptDocument(string SourcePath, string Content);

// Loads the small, fixed allowlist of project documents that seed the AI system prompt, caching each with a
// five-minute absolute expiration so an operator can edit the rules without a restart while the provider is not
// re-read on every cycle. Only allowlisted paths resolve, and every resolved path is verified to stay beneath
// the configured documentation root. A missing root or required file throws before any provider call.
public sealed class AiPromptDocumentationProvider
{
    private static readonly string[] CoreDocuments =
    {
        "docs/roles/ai-agent.md",
        "docs/roles/individual.md",
        "docs/rules/share-price-formation.md",
        "docs/rules/trading-days.md",
        "docs/rules/luld.md",
        "docs/logic/settlement.md",
        "docs/logic/margin.md",
        "docs/logic/bank-loans.md",
        "docs/logic/sector-sentiment.md",
    };

    private static readonly string[] FundDocuments =
    {
        "docs/roles/fund-member.md",
        "docs/roles/collective-fund.md",
    };

    private static readonly HashSet<string> Allowlist =
        new(CoreDocuments.Concat(FundDocuments), StringComparer.Ordinal);

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly string documentationRoot;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AiPromptDocumentationProvider> logger;
    private readonly object gate = new();
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);

    public AiPromptDocumentationProvider(
        IOptions<AiTradingOptions> options,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        ILogger<AiPromptDocumentationProvider> logger)
    {
        documentationRoot = Path.GetFullPath(
            Path.Combine(environment.ContentRootPath, options.Value.DocumentationRoot));
        this.timeProvider = timeProvider;
        this.logger = logger;

        if (!Directory.Exists(documentationRoot))
        {
            logger.LogWarning(
                "AI prompt documentation root '{Root}' does not exist; AI prompt construction will fail until it is present.",
                documentationRoot);
        }
    }

    public IReadOnlyList<AiPromptDocument> GetDocuments(bool includeFundDocuments)
    {
        var selected = includeFundDocuments ? CoreDocuments.Concat(FundDocuments) : CoreDocuments;
        return selected.Select(path => new AiPromptDocument(path, GetDocument(path))).ToList();
    }

    public string GetDocument(string relativePath)
    {
        if (!Allowlist.Contains(relativePath))
        {
            throw new InvalidOperationException($"Document '{relativePath}' is not on the AI prompt allowlist.");
        }

        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            if (cache.TryGetValue(relativePath, out var entry) && now < entry.ExpiresAt)
            {
                return entry.Content;
            }

            if (!Directory.Exists(documentationRoot))
            {
                logger.LogError("AI prompt documentation root '{Root}' does not exist.", documentationRoot);
                throw new InvalidOperationException(
                    $"AI prompt documentation root '{documentationRoot}' does not exist.");
            }

            var fullPath = ResolveWithinRoot(relativePath);
            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException(
                    $"Required AI prompt document '{relativePath}' was not found at '{fullPath}'.");
            }

            var content = File.ReadAllText(fullPath);
            cache[relativePath] = new CacheEntry(content, now + CacheDuration);
            return content;
        }
    }

    // The allowlist keys are repo-relative and begin with "docs/", but the configured root already points at the
    // docs directory, so the prefix is dropped before combining. The result must remain beneath the root.
    private string ResolveWithinRoot(string relativePath)
    {
        var relativeToRoot = relativePath.StartsWith("docs/", StringComparison.Ordinal)
            ? relativePath["docs/".Length..]
            : relativePath;
        var fullPath = Path.GetFullPath(Path.Combine(documentationRoot, relativeToRoot));

        if (fullPath != documentationRoot
            && !fullPath.StartsWith(documentationRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Document '{relativePath}' resolves outside the documentation root.");
        }

        return fullPath;
    }

    private sealed record CacheEntry(string Content, DateTimeOffset ExpiresAt);
}
