namespace TraderAi.Services;

public sealed class PrimaryIssuanceOptions
{
    public const string SectionName = "PrimaryIssuance";

    public bool Enabled { get; set; }

    public decimal FloatScarcityThresholdPercent { get; set; } = 10m;

    public decimal MaximumDailyIssuancePercent { get; set; } = 25m;
}
