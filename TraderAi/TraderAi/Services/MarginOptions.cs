namespace TraderAi.Services;

public sealed class MarginOptions
{
    public const string SectionName = "Margin";
    public bool Enabled { get; set; } = true;
    public decimal InitialMarginRate { get; set; } = 0.50m;
    public decimal MaintenanceMarginRate { get; set; } = 0.25m;
    public decimal DailyInterestRate { get; set; } = 0.001m;
    public decimal MaintenanceBufferRate { get; set; } = 0.02m;
    public decimal ForcedSaleDiscountRate { get; set; } = 0.05m;
}
