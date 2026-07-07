namespace TraderAi.Services;

// Single home for every chance/probability and random-magnitude value the per-cycle simulation draws
// against, so tuning the market's randomness never means hunting constants across a dozen services. Bound
// to the "RandomChanceRates" config section; the defaults below are the values these lived at as consts.
public sealed class RandomChanceRatesOptions
{
    public const string SectionName = "RandomChanceRates";

    public EventTriggerChances EventTriggerChances { get; set; } = new();

    public ChanceModifiers ChanceModifiers { get; set; } = new();

    public RandomMagnitudeBands RandomMagnitudeBands { get; set; } = new();
}

// Probabilities that gate whether a random event fires this cycle.
public sealed class EventTriggerChances
{
    // Per-cycle chance an eligible trader joins a collective fund.
    public double FundJoin { get; set; } = 0.05;

    // Per-cycle chance a trader opens a new collective fund.
    public double FundOpen { get; set; } = 0.03;

    // Base chance a fund member leaves; a temperament delta shifts it before it applies.
    public double FundLeaveBase { get; set; } = 0.20;

    // Ceiling on the member leave chance.
    public double FundLeaveMax { get; set; } = 0.90;

    // Base chance an eligible member switches to a better-scoring fund.
    public double FundSwitchBase { get; set; } = 0.25;

    // Ceiling on a trader's per-cycle bankruptcy chance from the wealth ramp.
    public double BankruptcyMax { get; set; } = 0.10;

    // Added bankruptcy chance per 1% of debt against total worth.
    public double BankruptcyPerDebtPercent { get; set; } = 0.0025;

    // New-company appearance chance while below the high-tier company count.
    public double CompanyAppearanceHigh { get; set; } = 0.10;

    // New-company appearance chance while below the mid-tier company count.
    public double CompanyAppearanceMid { get; set; } = 0.05;

    // New-company appearance chance once the company count is healthy.
    public double CompanyAppearanceLow { get; set; } = 0.01;

    // Discovery chance when auditing a company after a big recent price move.
    public double AuditorIssueOnBigMove { get; set; } = 0.10;

    // Discovery chance when auditing a price-stable company.
    public double AuditorIssueOnStable { get; set; } = 0.02;

    // Base chance a trader revises a buy after a High rating, before personality deltas.
    public double AuditorHighRatingBuyRevision { get; set; } = 0.50;

    // Base chance a trader revises a buy after an Extra rating, before personality deltas.
    public double AuditorExtraRatingBuyRevision { get; set; } = 0.70;

    // Base per-cycle quit chance for a cash-starved trader.
    public double ExitStarvationBase { get; set; } = 0.25;

    // Base per-cycle quit chance for a trader after a fund loss.
    public double ExitFundLoss { get; set; } = 0.25;

    // Local-crisis trigger chance added per cycle once the local quiet window has passed.
    public double LocalCrisisStepPerCycle { get; set; } = 0.03;

    // Global-crisis trigger chance added per cycle once the global quiet window has passed.
    public double GlobalCrisisStepPerCycle { get; set; } = 0.01;

    // Science-investigation trigger chance added per cycle once the quiet window has passed.
    public double ScienceStepPerCycle { get; set; } = 0.03;

    // Share-emission chance added per capitalisation band above the threshold.
    public double ShareEmissionPerBand { get; set; } = 0.05;

    // Cap on the accumulated share-emission chance so an emission never becomes a certainty.
    public double ShareEmissionMax { get; set; } = 1.0;

    // Per-company dividend-pay chance while its capitalisation is stable.
    public decimal DividendStableCapitalization { get; set; } = 0.75m;

    // Per-company dividend-pay chance once its capitalisation has moved sharply.
    public decimal DividendVolatileCapitalization { get; set; } = 0.25m;

    // Chance an automated news post carries market impact rather than being flavour only.
    public double NewsImpact { get; set; } = 0.6;

    // Chance an impactful automated post targets a single company rather than industries.
    public double NewsCompanyScope { get; set; } = 0.5;
}

// Values that scale or shift a base chance rather than gating a roll on their own.
public sealed class ChanceModifiers
{
    // Multiplies every trader's bankruptcy chance while a crisis is active.
    public double CrisisBankruptcyMultiplier { get; set; } = 2.0;

    // Multiplies the auditor discovery chance while a crisis is active.
    public double CrisisAuditorIssueMultiplier { get; set; } = 3.0;

    // Scales every trader's quit chance during a global crisis.
    public double GlobalCrisisExitMultiplier { get; set; } = 5.0;

    // Scales every trader's quit chance during a local crisis.
    public double LocalCrisisExitMultiplier { get; set; } = 2.0;

    // Scales the science-investigation trigger chance during a crisis, since a breakthrough is rarer then.
    public double CrisisScienceChanceFactor { get; set; } = 0.5;

    // Chance a would-be price-lifting automated post is suppressed during a crisis.
    public double CrisisNewsIncreaseSuppression { get; set; } = 0.5;

    // Buy drop per matching conservative/low-risk trait while a crisis is active; the traits stack.
    public double CrisisBuySuppression { get; set; } = 0.15;

    // Added to the base appearance chance per delisting since the last listing.
    public double CompanyClosureAppearanceBoost { get; set; } = 0.25;
}

// Bounds of a random draw's magnitude once an event has fired.
public sealed class RandomMagnitudeBands
{
    // Lower bound of the random dividend rate applied to a paying company's capitalisation.
    public decimal DividendRateMin { get; set; } = 0.0001m;

    // Upper bound of the random dividend rate.
    public decimal DividendRateMax { get; set; } = 0.005m;

    // Lower bound of the emission size as a fraction of the current share count.
    public double ShareEmissionRateMin { get; set; } = 0.01;

    // Upper bound of the emission size fraction.
    public double ShareEmissionRateMax { get; set; } = 0.10;

    // Lower bound of the per-industry price drop a crisis inflicts.
    public decimal CrisisIndustryDropMinPercent { get; set; } = 5m;

    // Upper bound of the per-industry crisis price drop.
    public decimal CrisisIndustryDropMaxPercent { get; set; } = 15m;

    // Lower bound of the per-industry price lift a science breakthrough gives.
    public decimal ScienceIndustryLiftMinPercent { get; set; } = 0.5m;

    // Upper bound of the per-industry science price lift.
    public decimal ScienceIndustryLiftMaxPercent { get; set; } = 5m;

    // Lower bound of the fraction of sectors a global crisis hits.
    public double GlobalCrisisIndustryShareMin { get; set; } = 0.30;

    // Upper bound of the global-crisis sector fraction.
    public double GlobalCrisisIndustryShareMax { get; set; } = 0.70;
}
