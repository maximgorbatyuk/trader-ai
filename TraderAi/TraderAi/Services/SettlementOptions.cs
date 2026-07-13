namespace TraderAi.Services;

public sealed class SettlementOptions
{
    public const string SectionName = "Settlement";

    public int SettlementLagTradingDays { get; set; } = 1;
}
