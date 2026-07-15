namespace TraderAi.Services;

// Coordinator-wide AI trading configuration bound from the "AiTrading" section. API keys are never held here;
// only the provider catalog, timing, concurrency, and retry envelope live in configuration.
public sealed class AiTradingOptions
{
    public const string SectionName = "AiTrading";

    public bool Enabled { get; set; }

    public string DocumentationRoot { get; set; } = "../../docs";

    public int ScanIntervalMilliseconds { get; set; } = 500;

    public int RequestTimeoutSeconds { get; set; } = 120;

    public int MaxConcurrentRequests { get; set; } = 4;

    public int MaxOrdersPerDecision { get; set; } = 10;

    // A malformed provider reply wastes a whole scheduled decision. Resending the same request a bounded number of
    // times within the cycle recovers most of them because provider sampling usually returns valid JSON on retry.
    public int MaxInvalidJsonRetries { get; set; } = 1;

    public int HistoryCycles { get; set; } = 30;

    public int RetryBaseDelaySeconds { get; set; } = 5;

    public int RetryMaxDelaySeconds { get; set; } = 300;

    public int AuthErrorRetrySeconds { get; set; } = 900;

    public Dictionary<string, AiProviderOptions> Providers { get; set; } = new();
}

public sealed class AiProviderOptions
{
    public string DisplayName { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    public List<string> Models { get; set; } = new();
}
