namespace TraderAi.Services;

public sealed class ConcentrationCapOptions
{
    public const string SectionName = "ConcentrationCap";

    // Opt-in: the concentration cap stays off unless this is set to true.
    public bool Enabled { get; set; }

    // A single company worth more than this percent of total market capitalisation is over-concentrated and
    // has its price cut. The real-world Nasdaq-100 individual cap is 24%.
    public decimal MaxSingleCompanyWeightPercent { get; set; } = 20m;

    // How much an over-concentrated company's price is cut each cycle it stays over the cap. Kept at or under
    // the volatility halt's down band so a cut does not itself trip the halt.
    public decimal PriceCutPercent { get; set; } = 25m;
}
