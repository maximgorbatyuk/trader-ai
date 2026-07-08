namespace TraderAi.Services;

public sealed class VolatilityHaltOptions
{
    public const string SectionName = "VolatilityHalt";

    // Opt-in: the volatility halt stays off unless this is set to true.
    public bool Enabled { get; set; }

    // The rolling window, in cycles, over which the price move is measured (the cycle analogue of LULD's
    // 15-second persistence check).
    public int ObservationWindowCycles { get; set; } = 3;

    // How many cycles a triggered company stays frozen (the cycle analogue of LULD's 5-minute pause).
    public int HaltDurationCycles { get; set; } = 15;

    // A rise of at least this percent over the observation window halts the company.
    public decimal UpBandPercent { get; set; } = 25m;

    // A fall of at least this percent over the observation window halts the company. Kept looser than the up
    // band so a single deliberate price cut (an auditor Extra downgrade or a concentration cut) does not trip
    // the halt on its own.
    public decimal DownBandPercent { get; set; } = 40m;
}
