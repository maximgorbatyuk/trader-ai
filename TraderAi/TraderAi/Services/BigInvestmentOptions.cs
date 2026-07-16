namespace TraderAi.Services;

public sealed class BigInvestmentOptions
{
    public const string SectionName = "BigInvestment";

    // Opt-in: the big-investment deal mechanism stays off unless this is set to true.
    public bool Enabled { get; set; }
}
