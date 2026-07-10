namespace TraderAi.Services;

public sealed class IndustrySentimentOptions
{
    public const string SectionName = "IndustrySentiment";

    // Opt-in so deployments keep their prior market behavior until the feature is explicitly enabled.
    public bool Enabled { get; set; }

    public int SentimentValueMin { get; set; } = -300;

    public int SentimentValueMax { get; set; } = 300;

    public decimal SentimentVolatilityMin { get; set; }

    public decimal SentimentVolatilityMax { get; set; } = 3m;

    public decimal SectorBetaMin { get; set; } = 0.6m;

    public decimal SectorBetaMax { get; set; } = 1.5m;

    public int SentimentValueLimit { get; set; } = 1000;

    public int SentimentDecayPerCycle { get; set; } = 1;
}
