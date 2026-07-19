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
    public const double BigInvestmentHardMax = 0.50;

    // Per-cycle chance an eligible trader joins a collective fund.
    public double FundJoin { get; set; } = 0.05;

    // Per-cycle chance a trader opens a new collective fund.
    public double FundOpen { get; set; } = 0.03;

    // Base chance a tenure-eligible fund member exits independently before any switch roll.
    public double FundLeaveBase { get; set; } = 0.20;

    // Ceiling on the member leave chance.
    public double FundLeaveMax { get; set; } = 0.90;

    // Base chance an eligible member switches to a better-scoring fund.
    public double FundSwitchBase { get; set; } = 0.15;

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

    // Chance of each symmetric Extra outcome after a big recent price move.
    public double AuditorIssueOnBigMove { get; set; } = 0.10;

    // Chance of each symmetric Extra outcome when auditing a price-stable company.
    public double AuditorIssueOnStable { get; set; } = 0.02;

    // Chance an issue-free audit raises expectations instead of issuing the ordinary risk verdict.
    public double AuditorRaiseExpectationsChance { get; set; } = 0.08;

    // Base chance a trader revises a buy after a High rating, before personality deltas.
    public double AuditorHighRatingBuyRevision { get; set; } = 0.50;

    // Base chance a trader revises a buy after an Extra rating, before personality deltas.
    public double AuditorExtraRatingBuyRevision { get; set; } = 0.70;

    // Base per-cycle quit chance for a cash-starved trader.
    public double ExitStarvationBase { get; set; } = 0.25;

    // Base per-cycle quit chance for a trader after a fund loss.
    public double ExitFundLoss { get; set; } = 0.25;

    // Per-cycle chance a new trader appears while the active-trader roster is below its population cap.
    public double TraderAppearanceBase { get; set; } = 0.10;

    // Local-crisis trigger chance added per trading day once the local quiet window has passed.
    public double LocalCrisisStepPerTradingDay { get; set; } = 0.03;

    // Global-crisis trigger chance added per trading day once the global quiet window has passed.
    public double GlobalCrisisStepPerTradingDay { get; set; } = 0.01;

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

    // Base per-cycle chance an industry's sentiment direction is revised.
    public double IndustrySentimentRevisionBase { get; set; } = 0.25;

    // Chance an automated discretionary order is priced in a waiting outer segment rather than the active band.
    public double OutsideBandOrder { get; set; } = 0.10;

    // Chance an eligible rule-based Individual bids for a company with no remaining sell interest.
    public double NoSellOrderBuyChance { get; set; } = 0.80;

    // Chance a science investigation also pushes the affected industry's sentiment upward.
    public double ScienceSentimentPush { get; set; } = 0.50;

    // Generic per-cycle chance for a big investment and the base chance at the minimum cash threshold after an
    // Extra raised-expectations rating.
    public double BigInvestment { get; set; } = 0.15;

    // Ceiling on the cash-scaled investment chance following an Extra raised-expectations rating.
    public double BigInvestmentMax { get; set; } = BigInvestmentHardMax;
}

// Values that scale or shift a base chance rather than gating a roll on their own.
public sealed class ChanceModifiers
{
    // Multiplies every trader's bankruptcy chance while a crisis is active.
    public double CrisisBankruptcyMultiplier { get; set; } = 2.0;

    // Multiplies both symmetric auditor Extra-outcome chances while a crisis is active.
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

    // Added to a trader's fund-join chance when a fund whose net worth has grown recently is joinable, so a
    // fund on a hot streak draws more members.
    public double FundGrowthJoinBonus { get; set; } = 0.15;

    // Scales an industry's volatility contribution to its sentiment revision chance.
    public double IndustrySentimentVolatilityFactor { get; set; } = 0.10;

    // Chance a crisis forces an affected industry's sentiment revision downward.
    public double CrisisSentimentForcedDown { get; set; } = 0.50;

    // Added revision chance when company-specific news affects an industry member.
    public double CompanyNewsSentimentBonus { get; set; } = 0.10;
}

// Bounds of a random draw's magnitude once an event has fired.
public sealed class RandomMagnitudeBands
{
    // Lower bound of the random dividend rate applied to a paying company's capitalisation.
    public decimal DividendRateMin { get; set; } = 0.0003m;

    // Upper bound of the random dividend rate.
    public decimal DividendRateMax { get; set; } = 0.015m;

    public double PrimaryIssuanceRateMin { get; set; } = 0.02;

    public double PrimaryIssuanceRateMax { get; set; } = 0.20;

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

    // Fraction of the target company's capitalisation a big-investment deal spends, drawn between these bounds.
    // The lower bound is also the eligibility floor: an investor must be able to fund at least this share.
    public double BigInvestmentFractionMin { get; set; } = 0.40;

    // May exceed 1.0 so a deal can be several times the company's current capitalisation; the actual spend is still
    // capped by the investor's spendable cash, so a bigger max only bites when the investor can afford it.
    public double BigInvestmentFractionMax { get; set; } = 5.00;

    // Lower bound of the fraction of sectors a global crisis hits.
    public double GlobalCrisisIndustryShareMin { get; set; } = 0.30;

    // Upper bound of the global-crisis sector fraction.
    public double GlobalCrisisIndustryShareMax { get; set; } = 0.70;
}
