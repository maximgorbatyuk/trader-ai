using System.Text;
using Microsoft.Extensions.Configuration;

namespace TraderAi.Services;

public enum GameSettingValueType
{
    Boolean,
    Integer,
    Decimal,
    Text,
    Url,
    StringList,
    MultilineText,
    Secret,
}

public sealed record GameSettingDefinition(
    string Key,
    string Section,
    string? Subsection,
    string Name,
    string Description,
    GameSettingValueType ValueType);

public static class GameSettingsCatalog
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedLeafNamesBySection =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["MarketLoop"] = ["Enabled", "IntervalSeconds"],
            ["TradingClock"] = ["TradingCyclesPerDay", "TradingCycleSeconds", "BreakDurationSeconds"],
            ["Settlement"] = ["SettlementLagTradingDays"],
            ["Margin"] =
            [
                "Enabled", "InitialMarginRate", "MaintenanceMarginRate", "DailyInterestRate",
                "MaintenanceBufferRate", "ForcedSaleDiscountRate",
            ],
            ["AutomatedTrading"] =
            [
                "LowMinimumExposurePercent", "LowMaximumExposurePercent", "LowMaximumOrderNotionalPercent",
                "MediumMinimumExposurePercent", "MediumMaximumExposurePercent", "MediumMaximumOrderNotionalPercent",
                "HighMinimumExposurePercent", "HighMaximumExposurePercent", "HighMaximumOrderNotionalPercent",
                "MaximumIssuedSharesPerOrderPercent", "MaximumPassiveBidIssuedSharesPercent",
                "MinimumMeaningfulQuantityPercent", "MaximumHighRiskMarginLiabilityPercent",
                "PassiveBuyPremiumMinPercent", "PassiveBuyPremiumMaxPercent",
                "BuyOrdersPerCycleMin", "BuyOrdersPerCycleMax",
            ],
            ["News"] = ["Enabled", "CyclesBetweenPosts"],
            ["Crisis"] = ["Enabled"],
            ["ScienceInvestigation"] = ["Enabled"],
            ["Bankruptcy"] = ["Enabled"],
            ["CollectiveFund"] =
            [
                "Enabled", "MinimumMembershipTradingDays", "CashBufferFraction", "PreLeaveCashBufferFraction",
                "JoinBalanceCeiling", "FundGrowthWindowCycles", "MaxMembers", "SoftCloseMembers",
                "ManagerProfitFeeEnabled", "ManagerProfitFeeShare",
            ],
            ["MarketExit"] = ["Enabled"],
            ["StockSplit"] = ["Enabled"],
            ["Auditor"] = ["Enabled"],
            ["ShareEmission"] = ["Enabled"],
            ["BigInvestment"] = ["Enabled"],
            ["PrimaryIssuance"] = ["Enabled", "FloatScarcityThresholdPercent", "MaximumDailyIssuancePercent"],
            ["CompanyLifecycle"] = ["Enabled"],
            ["Loan"] = ["Enabled"],
            ["TradeFee"] = ["Enabled", "FeeRate"],
            ["VolatilityHalt"] =
            [
                "Enabled", "ReferenceWindowSeconds", "LimitStateDurationSeconds", "TradingPauseDurationSeconds",
                "UpperBandPercent", "LowerBandPercent", "AllowedOrderLowerPercent", "AllowedOrderUpperPercent",
                "DemandRatchetStepPercent",
            ],
            ["ConcentrationCap"] = ["Enabled", "MaxSingleCompanyWeightPercent", "PriceCutPercent"],
            ["IndustrySentiment"] =
            [
                "Enabled", "SentimentValueMin", "SentimentValueMax", "SentimentVolatilityMin",
                "SentimentVolatilityMax", "SectorBetaMin", "SectorBetaMax", "SentimentValueLimit",
                "SentimentDecayPerCycle",
            ],
            ["AiTrading"] =
            [
                "Enabled", "ScanIntervalMilliseconds", "RequestTimeoutSeconds", "MaxConcurrentRequests",
                "MaxOrdersPerDecision", "PredictionHorizonCycles", "MaxPredictionsPerDecision", "HistoryCycles", "RetryBaseDelaySeconds", "RetryMaxDelaySeconds",
                "AuthErrorRetrySeconds", "SystemPromptTemplate", "FinalDecisionInstruction",
            ],
            ["RandomChanceRates"] = [],
        };

    private static readonly HashSet<string> AllowedProviderLeafNames =
    [
        "DisplayName", "Endpoint", "ApiKey", "Models",
    ];

    private static readonly HashSet<string> AllowedRandomChanceRateKeys =
    [
        "EventTriggerChances:FundJoin",
        "EventTriggerChances:FundOpen",
        "EventTriggerChances:FundLeaveBase",
        "EventTriggerChances:FundLeaveMax",
        "EventTriggerChances:FundSwitchBase",
        "EventTriggerChances:BankruptcyMax",
        "EventTriggerChances:BankruptcyPerDebtPercent",
        "EventTriggerChances:CompanyAppearanceHigh",
        "EventTriggerChances:CompanyAppearanceMid",
        "EventTriggerChances:CompanyAppearanceLow",
        "EventTriggerChances:AuditorIssueOnBigMove",
        "EventTriggerChances:AuditorIssueOnStable",
        "EventTriggerChances:AuditorRaiseExpectationsChance",
        "EventTriggerChances:AuditorHighRatingBuyRevision",
        "EventTriggerChances:AuditorExtraRatingBuyRevision",
        "EventTriggerChances:ExitStarvationBase",
        "EventTriggerChances:ExitFundLoss",
        "EventTriggerChances:TraderAppearanceBase",
        "EventTriggerChances:LocalCrisisStepPerTradingDay",
        "EventTriggerChances:GlobalCrisisStepPerTradingDay",
        "EventTriggerChances:ScienceStepPerCycle",
        "EventTriggerChances:ShareEmissionPerBand",
        "EventTriggerChances:ShareEmissionMax",
        "EventTriggerChances:DividendStableCapitalization",
        "EventTriggerChances:DividendVolatileCapitalization",
        "EventTriggerChances:NewsImpact",
        "EventTriggerChances:NewsCompanyScope",
        "EventTriggerChances:IndustrySentimentRevisionBase",
        "EventTriggerChances:ScienceSentimentPush",
        "EventTriggerChances:OutsideBandOrder",
        "EventTriggerChances:NoSellOrderBuyChance",
        "EventTriggerChances:BigInvestment",
        "EventTriggerChances:BigInvestmentMax",
        "ChanceModifiers:CrisisBankruptcyMultiplier",
        "ChanceModifiers:CrisisAuditorIssueMultiplier",
        "ChanceModifiers:GlobalCrisisExitMultiplier",
        "ChanceModifiers:LocalCrisisExitMultiplier",
        "ChanceModifiers:CrisisScienceChanceFactor",
        "ChanceModifiers:CrisisNewsIncreaseSuppression",
        "ChanceModifiers:CrisisBuySuppression",
        "ChanceModifiers:CompanyClosureAppearanceBoost",
        "ChanceModifiers:FundGrowthJoinBonus",
        "ChanceModifiers:IndustrySentimentVolatilityFactor",
        "ChanceModifiers:CrisisSentimentForcedDown",
        "ChanceModifiers:CompanyNewsSentimentBonus",
        "RandomMagnitudeBands:DividendRateMin",
        "RandomMagnitudeBands:DividendRateMax",
        "RandomMagnitudeBands:PrimaryIssuanceRateMin",
        "RandomMagnitudeBands:PrimaryIssuanceRateMax",
        "RandomMagnitudeBands:ShareEmissionRateMin",
        "RandomMagnitudeBands:ShareEmissionRateMax",
        "RandomMagnitudeBands:CrisisIndustryDropMinPercent",
        "RandomMagnitudeBands:CrisisIndustryDropMaxPercent",
        "RandomMagnitudeBands:ScienceIndustryLiftMinPercent",
        "RandomMagnitudeBands:ScienceIndustryLiftMaxPercent",
        "RandomMagnitudeBands:GlobalCrisisIndustryShareMin",
        "RandomMagnitudeBands:GlobalCrisisIndustryShareMax",
        "RandomMagnitudeBands:BigInvestmentFractionMin",
        "RandomMagnitudeBands:BigInvestmentFractionMax",
    ];

    private static readonly HashSet<string> IntegerKeys =
    [
        "MarketLoop:IntervalSeconds",
        "TradingClock:TradingCyclesPerDay",
        "TradingClock:TradingCycleSeconds",
        "TradingClock:BreakDurationSeconds",
        "Settlement:SettlementLagTradingDays",
        "News:CyclesBetweenPosts",
        "CollectiveFund:MinimumMembershipTradingDays",
        "CollectiveFund:FundGrowthWindowCycles",
        "CollectiveFund:MaxMembers",
        "CollectiveFund:SoftCloseMembers",
        "VolatilityHalt:ReferenceWindowSeconds",
        "VolatilityHalt:LimitStateDurationSeconds",
        "VolatilityHalt:TradingPauseDurationSeconds",
        "IndustrySentiment:SentimentValueMin",
        "IndustrySentiment:SentimentValueMax",
        "IndustrySentiment:SentimentValueLimit",
        "IndustrySentiment:SentimentDecayPerCycle",
        "AiTrading:ScanIntervalMilliseconds",
        "AiTrading:RequestTimeoutSeconds",
        "AiTrading:MaxConcurrentRequests",
        "AiTrading:MaxOrdersPerDecision",
        "AiTrading:PredictionHorizonCycles",
        "AiTrading:MaxPredictionsPerDecision",
        "AiTrading:HistoryCycles",
        "AiTrading:RetryBaseDelaySeconds",
        "AiTrading:RetryMaxDelaySeconds",
        "AiTrading:AuthErrorRetrySeconds",
        "AutomatedTrading:BuyOrdersPerCycleMin",
        "AutomatedTrading:BuyOrdersPerCycleMax",
    ];

    private static readonly HashSet<string> MultilineTextKeys =
    [
        "AiTrading:SystemPromptTemplate",
        "AiTrading:FinalDecisionInstruction",
    ];

    private static readonly IReadOnlyDictionary<string, string> Descriptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MarketLoop:Enabled"] = "Controls whether the background market loop is allowed to advance a running market.",
            ["MarketLoop:IntervalSeconds"] = "Sets the delay between automatic market-cycle attempts.",
            ["TradingClock:TradingCyclesPerDay"] = "Sets how many trading cycles make up one simulated trading day.",
            ["TradingClock:TradingCycleSeconds"] = "Sets the simulated duration and loop cadence of one trading cycle.",
            ["TradingClock:BreakDurationSeconds"] = "Sets the non-trading break between simulated trading days.",
            ["Settlement:SettlementLagTradingDays"] = "Sets how many trading days pass before executed cash and shares become settled.",
            ["Margin:Enabled"] = "Controls whether participants can use margin accounts.",
            ["Margin:InitialMarginRate"] = "Sets the portion of a margin purchase that must be covered by the participant's own equity.",
            ["Margin:MaintenanceMarginRate"] = "Sets the minimum equity ratio required to avoid a margin call.",
            ["Margin:DailyInterestRate"] = "Sets the interest charged on outstanding margin debit each trading day.",
            ["Margin:MaintenanceBufferRate"] = "Adds a safety buffer when calculating the amount a margin call must restore.",
            ["Margin:ForcedSaleDiscountRate"] = "Sets the discount used when placing forced-sale orders for a margin call.",
            ["News:Enabled"] = "Controls whether the simulation publishes generated market news.",
            ["News:CyclesBetweenPosts"] = "Sets the minimum cycle cadence between generated news posts.",
            ["CollectiveFund:MinimumMembershipTradingDays"] = "Sets how long a member must remain in a fund before leaving voluntarily.",
            ["CollectiveFund:CashBufferFraction"] = "Sets the share of fund worth kept liquid during ordinary automated trading.",
            ["CollectiveFund:PreLeaveCashBufferFraction"] = "Sets the higher liquid reserve used before a member becomes eligible to leave.",
            ["CollectiveFund:JoinBalanceCeiling"] = "Limits fund-joining candidates to traders below this cash balance.",
            ["CollectiveFund:FundGrowthWindowCycles"] = "Sets the history window used to measure fund growth.",
            ["CollectiveFund:MaxMembers"] = "Sets the hard maximum number of members in a collective fund.",
            ["CollectiveFund:SoftCloseMembers"] = "Sets the membership level at which a fund stops accepting new members.",
            ["CollectiveFund:ManagerProfitFeeShare"] = "Sets the manager's share of eligible member profit when the fee is enabled.",
            ["PrimaryIssuance:FloatScarcityThresholdPercent"] = "Defines when remaining issuer float is scarce enough to permit primary issuance.",
            ["PrimaryIssuance:MaximumDailyIssuancePercent"] = "Caps primary issuance as a percentage of issued shares per trading day.",
            ["TradeFee:FeeRate"] = "Sets the fee charged on eligible secondary-market trades.",
            ["VolatilityHalt:ReferenceWindowSeconds"] = "Sets the rolling time window used to establish the LULD reference price.",
            ["VolatilityHalt:LimitStateDurationSeconds"] = "Sets how long a security may remain in limit state before trading pauses.",
            ["VolatilityHalt:TradingPauseDurationSeconds"] = "Sets the duration of a LULD trading pause.",
            ["VolatilityHalt:UpperBandPercent"] = "Sets the allowed percentage above the LULD reference price.",
            ["VolatilityHalt:LowerBandPercent"] = "Sets the allowed percentage below the LULD reference price.",
            ["VolatilityHalt:AllowedOrderLowerPercent"] = "Sets the lowest order price allowed relative to the current market price.",
            ["VolatilityHalt:AllowedOrderUpperPercent"] = "Sets the highest order price allowed relative to the current market price.",
            ["VolatilityHalt:DemandRatchetStepPercent"] = "Sets the price-reference step used when excess demand persists at the upper band.",
            ["ConcentrationCap:MaxSingleCompanyWeightPercent"] = "Sets the maximum share of total market capitalization one company may represent.",
            ["ConcentrationCap:PriceCutPercent"] = "Sets the price reduction applied to a company above the concentration cap.",
            ["IndustrySentiment:SentimentValueMin"] = "Sets the lowest sentiment value assigned when an industry is created.",
            ["IndustrySentiment:SentimentValueMax"] = "Sets the highest sentiment value assigned when an industry is created.",
            ["IndustrySentiment:SentimentVolatilityMin"] = "Sets the lowest sentiment volatility assigned to an industry.",
            ["IndustrySentiment:SentimentVolatilityMax"] = "Sets the highest sentiment volatility assigned to an industry.",
            ["IndustrySentiment:SectorBetaMin"] = "Sets the lowest sensitivity of an industry to external market events.",
            ["IndustrySentiment:SectorBetaMax"] = "Sets the highest sensitivity of an industry to external market events.",
            ["IndustrySentiment:SentimentValueLimit"] = "Caps the absolute sentiment value an industry can reach.",
            ["IndustrySentiment:SentimentDecayPerCycle"] = "Sets how quickly industry sentiment moves back toward neutral each cycle.",
            ["AiTrading:Enabled"] = "Controls whether configured AI traders can request provider decisions.",
            ["AiTrading:ScanIntervalMilliseconds"] = "Sets how frequently the AI coordinator scans for traders that are ready to act.",
            ["AiTrading:RequestTimeoutSeconds"] = "Sets the maximum duration of one provider request.",
            ["AiTrading:MaxConcurrentRequests"] = "Caps the number of AI provider requests in flight at the same time.",
            ["AiTrading:MaxOrdersPerDecision"] = "Caps the number of orders an AI trader may return in one decision.",
            ["AiTrading:PredictionHorizonCycles"] = "Sets the required forecast horizon for every AI prediction.",
            ["AiTrading:MaxPredictionsPerDecision"] = "Caps the number of forecasts an AI trader may return in one decision.",
            ["AiTrading:HistoryCycles"] = "Sets how much recent market history is included in an AI decision snapshot.",
            ["AiTrading:RetryBaseDelaySeconds"] = "Sets the initial delay before retrying a failed AI provider request.",
            ["AiTrading:RetryMaxDelaySeconds"] = "Caps the retry delay for repeated AI provider failures.",
            ["AiTrading:AuthErrorRetrySeconds"] = "Sets the retry delay after an AI provider authentication error.",
            ["AiTrading:SystemPromptTemplate"] = "Sets the shared base instructions every AI trader receives; \"{maxOrders}\" is replaced with the per-decision order cap.",
            ["AiTrading:FinalDecisionInstruction"] = "Sets the extra guidance appended to an AI trader's end-of-day planning decision.",
        };

    public static IReadOnlyList<GameSettingDefinition> Create(IConfiguration configuration)
    {
        var definitions = new List<GameSettingDefinition>();
        foreach (var sectionName in AllowedLeafNamesBySection.Keys)
        {
            Walk(configuration.GetSection(sectionName), sectionName, definitions, configuration);
        }

        return definitions
            .OrderBy(definition => definition.Section, StringComparer.Ordinal)
            .ThenBy(definition => definition.Subsection, StringComparer.Ordinal)
            .ThenBy(definition => definition.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static void Walk(
        IConfigurationSection section,
        string path,
        ICollection<GameSettingDefinition> definitions,
        IConfiguration configuration)
    {
        var children = section.GetChildren().ToList();
        if (path.EndsWith(":Models", StringComparison.Ordinal) && children.Count > 0)
        {
            AddDefinition(path, GameSettingValueType.StringList, definitions, configuration);
            return;
        }

        if (children.Count > 0)
        {
            foreach (var child in children)
            {
                Walk(child, $"{path}:{child.Key}", definitions, configuration);
            }

            return;
        }

        if (section.Value is not null)
        {
            AddDefinition(path, ValueType(path, section.Value), definitions, configuration);
        }
    }

    private static void AddDefinition(
        string key,
        GameSettingValueType valueType,
        ICollection<GameSettingDefinition> definitions,
        IConfiguration configuration)
    {
        var segments = key.Split(':');
        if (!IsAllowedKey(segments))
        {
            return;
        }

        var section = Humanize(segments[0]);
        var subsection = segments.Length > 2 ? Humanize(segments[^2]) : null;
        var name = Humanize(segments[^1]);

        if (segments.Length >= 4 && segments[0] == "AiTrading" && segments[1] == "Providers")
        {
            var providerName = configuration[$"AiTrading:Providers:{segments[2]}:DisplayName"] ?? segments[2];
            name = $"{providerName} {name.ToLowerInvariant()}";
            subsection = "Providers";
        }

        definitions.Add(new GameSettingDefinition(
            key,
            section,
            subsection,
            name,
            Description(key, section, name, segments),
            valueType));
    }

    private static bool IsAllowedKey(IReadOnlyList<string> segments)
    {
        if (segments.Count == 4
            && segments[0] == "AiTrading"
            && segments[1] == "Providers")
        {
            return AllowedProviderLeafNames.Contains(segments[3]);
        }

        if (segments.Count == 3 && segments[0] == "RandomChanceRates")
        {
            return AllowedRandomChanceRateKeys.Contains($"{segments[1]}:{segments[2]}");
        }

        return segments.Count == 2
            && AllowedLeafNamesBySection.TryGetValue(segments[0], out var leafNames)
            && leafNames.Contains(segments[1]);
    }

    private static GameSettingValueType ValueType(string key, string value)
    {
        if (key.EndsWith(":Endpoint", StringComparison.Ordinal))
        {
            return GameSettingValueType.Url;
        }

        if (key.EndsWith(":ApiKey", StringComparison.Ordinal))
        {
            return GameSettingValueType.Secret;
        }

        if (MultilineTextKeys.Contains(key))
        {
            return GameSettingValueType.MultilineText;
        }

        if (bool.TryParse(value, out _))
        {
            return GameSettingValueType.Boolean;
        }

        if (IntegerKeys.Contains(key))
        {
            return GameSettingValueType.Integer;
        }

        return decimal.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _)
            ? GameSettingValueType.Decimal
            : GameSettingValueType.Text;
    }

    private static string Description(string key, string section, string name, IReadOnlyList<string> segments)
    {
        if (Descriptions.TryGetValue(key, out var description))
        {
            return description;
        }

        if (segments.Count >= 4 && segments[0] == "AiTrading" && segments[1] == "Providers")
        {
            return segments[^1] switch
            {
                "DisplayName" => "Sets the provider name shown when configuring an AI trader.",
                "Endpoint" => "Sets the HTTPS chat-completions endpoint used for this AI provider.",
                "ApiKey" => "Sets the API key sent as the bearer token for this provider; it is never displayed once saved.",
                "Models" => "Lists the models offered as suggestions when configuring an AI trader.",
                _ => $"Configures {name.ToLowerInvariant()} for this AI provider.",
            };
        }

        if (segments.Count >= 3 && segments[0] == "RandomChanceRates")
        {
            return segments[1] switch
            {
                "EventTriggerChances" => $"Sets the probability used for {name.ToLowerInvariant()} when its market condition is eligible.",
                "ChanceModifiers" => $"Adjusts the probability effect represented by {name.ToLowerInvariant()}.",
                "RandomMagnitudeBands" => $"Sets the random magnitude boundary represented by {name.ToLowerInvariant()}.",
                _ => $"Configures {name.ToLowerInvariant()} for randomized market behavior.",
            };
        }

        if (segments[^1] == "Enabled")
        {
            return $"Controls whether {section.ToLowerInvariant()} behavior participates in the simulation.";
        }

        return $"Configures {name.ToLowerInvariant()} for {section.ToLowerInvariant()} behavior in the simulation.";
    }

    private static string Humanize(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character) && !char.IsUpper(value[index - 1]))
            {
                builder.Append(' ');
                character = char.ToLowerInvariant(character);
            }

            builder.Append(index == 0 ? char.ToUpperInvariant(character) : character);
        }

        return builder.ToString().Replace("Ai ", "AI ", StringComparison.Ordinal);
    }
}
