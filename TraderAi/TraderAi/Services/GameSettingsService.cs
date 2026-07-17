using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record GameSettingValue(GameSettingDefinition Definition, JsonElement Value);

public sealed class GameSettingsValidationException(
    IReadOnlyDictionary<string, string[]> errors) : Exception("One or more settings are invalid.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

public sealed class GameSettingsService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IConfiguration defaultConfiguration)
{
    private readonly IReadOnlyList<GameSettingDefinition> definitions = GameSettingsCatalog.Create(defaultConfiguration);
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile SettingsSnapshot? snapshot;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var existingKeys = await dbContext.GameSettings
                .Select(setting => setting.Key)
                .ToHashSetAsync(StringComparer.Ordinal, cancellationToken);

            foreach (var definition in definitions.Where(definition => !existingKeys.Contains(definition.Key)))
            {
                dbContext.GameSettings.Add(new GameSetting
                {
                    Key = definition.Key,
                    ValueJson = DefaultValueJson(definition),
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            var loaded = await LoadSnapshotAsync(dbContext, cancellationToken);
            ValidateSnapshot(loaded);
            snapshot = loaded;
        }
        finally
        {
            gate.Release();
        }
    }

    public TOptions GetOptions<TOptions>(string sectionName)
        where TOptions : class, new()
    {
        var current = snapshot ?? throw new InvalidOperationException("Game settings have not been initialized.");

        // Options are read on the hot per-cycle path; bind once per snapshot and reuse the result instead of
        // reflecting over configuration on every access. The immutable snapshot is swapped on update, so its
        // cache is discarded with it.
        return (TOptions)current.BoundOptions.GetOrAdd(
            sectionName,
            static (key, configuration) => configuration.GetSection(key).Get<TOptions>() ?? new TOptions(),
            current.Configuration);
    }

    public IReadOnlyList<GameSettingValue> GetAll()
        => snapshot?.Values ?? throw new InvalidOperationException("Game settings have not been initialized.");

    public async Task UpdateAsync(
        IReadOnlyDictionary<string, JsonElement> changes,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var knownDefinitions = definitions.ToDictionary(definition => definition.Key, StringComparer.Ordinal);
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var effectiveChanges = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (key, value) in changes)
            {
                if (!knownDefinitions.TryGetValue(key, out var definition))
                {
                    errors[key] = ["Unknown game setting."];
                    continue;
                }

                // A blank secret means "keep the stored value" rather than clearing it, so it is neither validated
                // nor persisted; a client only submits a secret when the operator types a replacement.
                if (definition.ValueType == GameSettingValueType.Secret && IsBlankValue(value))
                {
                    continue;
                }

                var error = ValidateValue(definition, value);
                if (error is not null)
                {
                    errors[key] = [error];
                    continue;
                }

                effectiveChanges[key] = value;
            }

            if (errors.Count > 0)
            {
                throw new GameSettingsValidationException(errors);
            }

            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var rows = await dbContext.GameSettings
                .Where(setting => knownDefinitions.Keys.Contains(setting.Key))
                .ToDictionaryAsync(setting => setting.Key, StringComparer.Ordinal, cancellationToken);
            var candidateValues = rows.ToDictionary(
                row => row.Key,
                row => row.Value.ValueJson,
                StringComparer.Ordinal);
            foreach (var (key, value) in effectiveChanges)
            {
                candidateValues[key] = value.GetRawText();
            }

            var candidateSnapshot = BuildSnapshot(candidateValues);
            ValidateSnapshot(candidateSnapshot);

            foreach (var (key, value) in effectiveChanges)
            {
                if (!rows.TryGetValue(key, out var row))
                {
                    row = new GameSetting { Key = key, ValueJson = value.GetRawText() };
                    dbContext.GameSettings.Add(row);
                }
                else
                {
                    row.ValueJson = value.GetRawText();
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            snapshot = candidateSnapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    private string DefaultValueJson(GameSettingDefinition definition)
    {
        var section = defaultConfiguration.GetSection(definition.Key);
        return definition.ValueType switch
        {
            GameSettingValueType.Boolean => JsonSerializer.Serialize(bool.Parse(section.Value!)),
            GameSettingValueType.Integer => JsonSerializer.Serialize(int.Parse(section.Value!, CultureInfo.InvariantCulture)),
            GameSettingValueType.Decimal => JsonSerializer.Serialize(decimal.Parse(section.Value!, CultureInfo.InvariantCulture)),
            GameSettingValueType.StringList => JsonSerializer.Serialize(
                section.GetChildren().Select(child => child.Value ?? string.Empty).ToArray()),
            _ => JsonSerializer.Serialize(section.Value ?? string.Empty),
        };
    }

    private static string? ValidateValue(GameSettingDefinition definition, JsonElement value)
    {
        return definition.ValueType switch
        {
            GameSettingValueType.Boolean when value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                => "Enter a true or false value.",
            GameSettingValueType.Integer when value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)
                => "Enter a whole number.",
            GameSettingValueType.Decimal when value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out _)
                => "Enter a number.",
            GameSettingValueType.Text or GameSettingValueType.MultilineText
                when value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())
                => "Enter a value.",
            GameSettingValueType.Secret
                when value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())
                => "Enter a value.",
            GameSettingValueType.Url when value.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                => "Enter an absolute HTTPS URL.",
            GameSettingValueType.StringList when value.ValueKind != JsonValueKind.Array
                || value.GetArrayLength() == 0
                || value.EnumerateArray().Any(item =>
                    item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                => "Enter at least one non-empty value.",
            _ => null,
        };
    }

    private async Task<SettingsSnapshot> LoadSnapshotAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var definitionByKey = definitions.ToDictionary(definition => definition.Key, StringComparer.Ordinal);
        var rows = await dbContext.GameSettings
            .Where(setting => definitionByKey.Keys.Contains(setting.Key))
            .AsNoTracking()
            .ToDictionaryAsync(setting => setting.Key, StringComparer.Ordinal, cancellationToken);

        return BuildSnapshot(rows.ToDictionary(row => row.Key, row => row.Value.ValueJson, StringComparer.Ordinal));
    }

    private SettingsSnapshot BuildSnapshot(IReadOnlyDictionary<string, string> rows)
    {
        var managedKeys = definitions.Select(definition => definition.Key).ToArray();
        var mergedValues = defaultConfiguration.AsEnumerable()
            .Where(pair => pair.Value is not null && !IsManagedConfigurationKey(pair.Key, managedKeys))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            AddConfigurationValues(mergedValues, row.Key, row.Value);
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(mergedValues)
            .Build();
        var values = definitions
            .Where(definition => rows.ContainsKey(definition.Key))
            .Select(definition => new GameSettingValue(
                definition,
                ParseJsonValue(rows[definition.Key])))
            .ToList();

        return new SettingsSnapshot(configuration, values);
    }

    private void ValidateSnapshot(SettingsSnapshot candidate)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var loop = candidate.Configuration.GetSection(MarketLoopOptions.SectionName).Get<MarketLoopOptions>() ?? new();
        var clock = candidate.Configuration.GetSection(TradingClockOptions.SectionName).Get<TradingClockOptions>() ?? new();
        if (loop.IntervalSeconds <= 0 || loop.IntervalSeconds != clock.TradingCycleSeconds)
        {
            errors["MarketLoop:IntervalSeconds"] = ["The market-loop interval must be positive and match the trading-cycle duration."];
        }

        if (clock.TradingCyclesPerDay <= 0
            || clock.TradingCycleSeconds <= 0
            || clock.BreakDurationSeconds <= 0
            || clock.BreakDurationSeconds % clock.TradingCycleSeconds != 0)
        {
            errors["TradingClock"] = ["Trading durations must be positive and the break must contain a whole number of trading-cycle ticks."];
        }

        var automated = candidate.Configuration.GetSection(AutomatedTradingOptions.SectionName)
            .Get<AutomatedTradingOptions>() ?? new();
        if (!automated.IsValid())
        {
            errors["AutomatedTrading"] = ["Exposure and order percentages must form valid ranges between 0 and 100."];
        }

        var primaryIssuance = candidate.Configuration.GetSection(PrimaryIssuanceOptions.SectionName)
            .Get<PrimaryIssuanceOptions>() ?? new();
        if (primaryIssuance.Enabled
            && (primaryIssuance.FloatScarcityThresholdPercent is <= 0m or > 100m
                || primaryIssuance.MaximumDailyIssuancePercent is <= 0m or > 100m))
        {
            errors["PrimaryIssuance"] = ["Enabled primary-issuance percentages must be greater than 0 and no more than 100."];
        }

        var margin = candidate.Configuration.GetSection(MarginOptions.SectionName).Get<MarginOptions>() ?? new();
        if (margin.InitialMarginRate is < 0m or > 1m
            || margin.MaintenanceMarginRate is < 0m or > 1m
            || margin.MaintenanceMarginRate > margin.InitialMarginRate
            || margin.DailyInterestRate < 0m
            || margin.MaintenanceBufferRate < 0m
            || margin.ForcedSaleDiscountRate is < 0m or > 1m)
        {
            errors["Margin"] = ["Margin rates must be non-negative fractions, and maintenance margin cannot exceed initial margin."];
        }

        var settlement = candidate.Configuration.GetSection(SettlementOptions.SectionName).Get<SettlementOptions>() ?? new();
        if (settlement.SettlementLagTradingDays < 0)
        {
            errors["Settlement"] = ["Settlement lag cannot be negative."];
        }

        var news = candidate.Configuration.GetSection(NewsOptions.SectionName).Get<NewsOptions>() ?? new();
        if (news.CyclesBetweenPosts <= 0)
        {
            errors["News"] = ["The generated-news cadence must be positive."];
        }

        var collectiveFund = candidate.Configuration.GetSection(CollectiveFundOptions.SectionName)
            .Get<CollectiveFundOptions>() ?? new();
        if (collectiveFund.MinimumMembershipTradingDays < 0
            || collectiveFund.CashBufferFraction is < 0m or > 1m
            || collectiveFund.PreLeaveCashBufferFraction is < 0m or > 1m
            || collectiveFund.JoinBalanceCeiling < 0m
            || collectiveFund.FundGrowthWindowCycles <= 0
            || collectiveFund.MaxMembers <= 0
            || collectiveFund.SoftCloseMembers <= 0
            || collectiveFund.SoftCloseMembers > collectiveFund.MaxMembers
            || collectiveFund.ManagerProfitFeeShare is < 0m or > 1m)
        {
            errors["CollectiveFund"] = ["Fund counts and windows must be valid, and buffer and fee shares must remain between 0 and 1."];
        }

        var tradeFee = candidate.Configuration.GetSection(TradeFeeOptions.SectionName).Get<TradeFeeOptions>() ?? new();
        if (tradeFee.FeeRate is < 0m or > 1m)
        {
            errors["TradeFee"] = ["The trade fee must remain between 0 and 1."];
        }

        var halt = candidate.Configuration.GetSection(VolatilityHaltOptions.SectionName)
            .Get<VolatilityHaltOptions>() ?? new();
        if (halt.ReferenceWindowSeconds <= 0
            || halt.LimitStateDurationSeconds <= 0
            || halt.TradingPauseDurationSeconds <= 0
            || halt.UpperBandPercent is <= 0m or > 100m
            || halt.LowerBandPercent is <= 0m or > 100m
            || halt.AllowedOrderLowerPercent is <= 0m or > 100m
            || halt.AllowedOrderUpperPercent is <= 0m or > 100m
            || halt.DemandRatchetStepPercent is <= 0m or > 100m)
        {
            errors["VolatilityHalt"] = ["LULD durations must be positive and price percentages must remain above 0 and no more than 100."];
        }

        var concentration = candidate.Configuration.GetSection(ConcentrationCapOptions.SectionName)
            .Get<ConcentrationCapOptions>() ?? new();
        if (concentration.MaxSingleCompanyWeightPercent is <= 0m or > 100m
            || concentration.PriceCutPercent is <= 0m or > 100m)
        {
            errors["ConcentrationCap"] = ["Concentration percentages must remain above 0 and no more than 100."];
        }

        var sentiment = candidate.Configuration.GetSection(IndustrySentimentOptions.SectionName)
            .Get<IndustrySentimentOptions>() ?? new();
        if (sentiment.SentimentValueMin > sentiment.SentimentValueMax
            || sentiment.SentimentVolatilityMin < 0m
            || sentiment.SentimentVolatilityMin > sentiment.SentimentVolatilityMax
            || sentiment.SectorBetaMin < 0m
            || sentiment.SectorBetaMin > sentiment.SectorBetaMax
            || sentiment.SentimentValueLimit <= 0
            || sentiment.SentimentDecayPerCycle < 0)
        {
            errors["IndustrySentiment"] = ["Industry sentiment minimums must not exceed maximums, and volatility, beta, limits, and decay cannot be negative."];
        }

        var ai = candidate.Configuration.GetSection(AiTradingOptions.SectionName).Get<AiTradingOptions>() ?? new();
        if (definitions.Any(definition => definition.Key.StartsWith("AiTrading:", StringComparison.Ordinal))
            && (ai.ScanIntervalMilliseconds <= 0
            || ai.RequestTimeoutSeconds <= 0
            || ai.MaxConcurrentRequests <= 0
            || ai.MaxOrdersPerDecision <= 0
            || ai.HistoryCycles <= 0
            || ai.RetryBaseDelaySeconds <= 0
            || ai.RetryMaxDelaySeconds < ai.RetryBaseDelaySeconds
            || ai.AuthErrorRetrySeconds <= 0
            || ai.Providers.Count == 0
            || ai.Providers.Values.Any(provider =>
                string.IsNullOrWhiteSpace(provider.DisplayName)
                || provider.Models.Count == 0
                || provider.Models.Any(string.IsNullOrWhiteSpace)
                || !Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme != Uri.UriSchemeHttps)))
        {
            errors["AiTrading"] = ["AI timing and limits must be positive, retry limits must be ordered, and every provider needs an HTTPS endpoint and at least one model."];
        }

        var random = candidate.Configuration.GetSection(RandomChanceRatesOptions.SectionName)
            .Get<RandomChanceRatesOptions>() ?? new();
        var probabilities = random.EventTriggerChances.GetType().GetProperties()
            .Select(property => property.GetValue(random.EventTriggerChances))
            .Select(value => value switch
            {
                double doubleValue => doubleValue,
                decimal decimalValue => (double)decimalValue,
                _ => double.NaN,
            });
        var modifiers = random.ChanceModifiers.GetType().GetProperties()
            .Select(property => property.GetValue(random.ChanceModifiers))
            .OfType<double>();
        var stableChance = random.EventTriggerChances.AuditorIssueOnStable;
        var bigMoveChance = random.EventTriggerChances.AuditorIssueOnBigMove;
        var crisisMultiplier = random.ChanceModifiers.CrisisAuditorIssueMultiplier;
        if (probabilities.Any(probability => !double.IsFinite(probability) || probability is < 0d or > 1d)
            || modifiers.Any(modifier => !double.IsFinite(modifier) || modifier < 0d)
            || stableChance > 0.5d
            || bigMoveChance > 0.5d
            || stableChance * crisisMultiplier > 0.5d
            || bigMoveChance * crisisMultiplier > 0.5d
            || random.EventTriggerChances.BigInvestmentMax > EventTriggerChances.BigInvestmentHardMax)
        {
            errors["RandomChanceRates"] = ["Event probabilities must remain between 0 and 1, modifiers must be non-negative, and adjusted auditor outcomes and Extra-raised investment chances cannot exceed 50%."];
        }

        var bands = random.RandomMagnitudeBands;
        if (bands.DividendRateMin is < 0m or > 1m
            || bands.DividendRateMax < bands.DividendRateMin
            || bands.DividendRateMax > 1m
            || !OrderedUnitRange(bands.PrimaryIssuanceRateMin, bands.PrimaryIssuanceRateMax)
            || !OrderedUnitRange(bands.ShareEmissionRateMin, bands.ShareEmissionRateMax)
            || bands.CrisisIndustryDropMinPercent < 0m
            || bands.CrisisIndustryDropMaxPercent < bands.CrisisIndustryDropMinPercent
            || bands.CrisisIndustryDropMaxPercent > 100m
            || bands.ScienceIndustryLiftMinPercent < 0m
            || bands.ScienceIndustryLiftMaxPercent < bands.ScienceIndustryLiftMinPercent
            || bands.ScienceIndustryLiftMaxPercent > 100m
            || !OrderedUnitRange(bands.GlobalCrisisIndustryShareMin, bands.GlobalCrisisIndustryShareMax))
        {
            errors["RandomChanceRates:RandomMagnitudeBands"] = ["Random magnitude minimums must not exceed maximums, rates and shares must remain between 0 and 1, and percentages cannot exceed 100."];
        }

        if (errors.Count > 0)
        {
            throw new GameSettingsValidationException(errors);
        }
    }

    private static bool OrderedUnitRange(double minimum, double maximum)
        => double.IsFinite(minimum)
            && double.IsFinite(maximum)
            && minimum is >= 0d and <= 1d
            && maximum is >= 0d and <= 1d
            && minimum <= maximum;

    private static bool IsBlankValue(JsonElement value)
        => value.ValueKind is JsonValueKind.Null
            || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));

    private static JsonElement ParseJsonValue(string valueJson)
    {
        using var document = JsonDocument.Parse(valueJson);
        return document.RootElement.Clone();
    }

    private static bool IsManagedConfigurationKey(string key, IReadOnlyList<string> managedKeys)
        => managedKeys.Any(managedKey =>
            key.Equals(managedKey, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith($"{managedKey}:", StringComparison.OrdinalIgnoreCase));

    private static void AddConfigurationValues(
        IDictionary<string, string?> configuration,
        string key,
        string valueJson)
    {
        using var document = JsonDocument.Parse(valueJson);
        var value = document.RootElement;
        if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                configuration[$"{key}:{index}"] = item.GetString();
                index++;
            }

            return;
        }

        configuration[key] = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText(),
        };
    }

    private sealed record SettingsSnapshot(
        IConfigurationRoot Configuration,
        IReadOnlyList<GameSettingValue> Values)
    {
        public ConcurrentDictionary<string, object> BoundOptions { get; } = new(StringComparer.Ordinal);
    }
}
