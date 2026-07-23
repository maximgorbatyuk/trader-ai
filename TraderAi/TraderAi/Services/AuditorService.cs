using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Audits are forward-looking signals built from completed trading days. They intentionally stage only immutable
// ratings and evidence, leaving price discovery and resting orders to the trading system.
public sealed class AuditorService
{
    private const double ReviewFraction = 0.05;

    private readonly AppDbContext dbContext;
    private readonly AuditorOptions options;
    private readonly CompanyAuditScorer scorer;

    public AuditorService(
        AppDbContext dbContext,
        IOptions<AuditorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Value.IsValid())
        {
            throw new ArgumentException("Auditor options are invalid.", nameof(options));
        }

        this.dbContext = dbContext;
        this.options = options.Value;
        scorer = new CompanyAuditScorer(options);
    }

    public async Task ProcessForCycleAsync(
        int currentCycleId,
        int currentCycleNumber,
        DateTime now)
    {
        if (!options.Enabled)
        {
            return;
        }

        var cycles = await (
            from cycle in dbContext.MarketCycles.AsNoTracking()
            join day in dbContext.TradingDays.AsNoTracking() on cycle.TradingDayId equals day.Id
            select new CyclePoint(
                cycle.Id,
                cycle.CycleNumber,
                cycle.TradingCycleNumber,
                cycle.MarketRunId,
                day.DayNumber))
            .ToListAsync();
        var currentCycle = cycles.SingleOrDefault(cycle => cycle.Id == currentCycleId);
        if (currentCycle is null
            || currentCycle.CycleNumber != currentCycleNumber
            || currentCycle.TradingCycleNumber != 1)
        {
            return;
        }

        var cycleById = cycles.ToDictionary(cycle => cycle.Id);
        var companies = await dbContext.Companies
            .Where(company => company.ClosedInCycleId == null)
            .OrderBy(company => company.Id)
            .ToListAsync();
        if (companies.Count == 0)
        {
            return;
        }

        var companyIds = companies.Select(company => company.Id).ToArray();
        var latestEffectiveDayByCompany = (await dbContext.CompanyAuditEvidence
                .AsNoTracking()
                .Where(evidence => companyIds.Contains(evidence.CompanyId))
                .Select(evidence => new
                {
                    evidence.CompanyId,
                    evidence.EffectiveTradingDayNumber,
                })
                .ToListAsync())
            .GroupBy(evidence => evidence.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.Max(evidence => evidence.EffectiveTradingDayNumber));

        var due = companies
            .Select(company =>
            {
                var listingDay = company.CreatedInCycleId is int listingCycleId
                    && cycleById.TryGetValue(listingCycleId, out var listingCycle)
                        ? listingCycle.TradingDayNumber
                        : 1;
                var previousEffectiveDay = latestEffectiveDayByCompany.GetValueOrDefault(company.Id);
                var dueFromDay = previousEffectiveDay > 0 ? previousEffectiveDay : listingDay;
                return new DueCompany(
                    company,
                    previousEffectiveDay > 0 ? previousEffectiveDay : listingDay,
                    currentCycle.TradingDayNumber - 1,
                    currentCycle.TradingDayNumber,
                    dueFromDay + options.AuditIntervalTradingDays);
            })
            .Where(candidate =>
                candidate.EffectiveTradingDayNumber >= candidate.DueTradingDayNumber
                && candidate.EvaluationEndTradingDayNumber >= candidate.EvaluationStartTradingDayNumber)
            .OrderBy(candidate => candidate.Company.Id)
            .ToArray();
        if (due.Length == 0)
        {
            return;
        }

        await EnsureAuditorsExistAsync(companies.Count, now);
        var auditors = await dbContext.Auditors
            .OrderBy(auditor => auditor.Id)
            .ToListAsync();
        if (auditors.Count == 0)
        {
            return;
        }

        var dueCompanyIds = due.Select(candidate => candidate.Company.Id).ToArray();
        var dueIndustryIds = due
            .Select(candidate => candidate.Company.IndustryId)
            .Distinct()
            .ToArray();
        var minimumStartDay = due.Min(candidate => candidate.EvaluationStartTradingDayNumber);
        var maximumEndDay = due.Max(candidate => candidate.EvaluationEndTradingDayNumber);

        var livePrices = await dbContext.PriceSnapshots
            .AsNoTracking()
            .Where(snapshot => dueCompanyIds.Contains(snapshot.CompanyId))
            .Select(snapshot => new
            {
                snapshot.Id,
                snapshot.CompanyId,
                snapshot.Price,
                snapshot.Capitalization,
                snapshot.CreatedInCycleId,
                snapshot.CreatedAt,
                SourcePriority = 1,
            })
            .ToListAsync();
        var archivedPrices = await dbContext.PriceSnapshotArchives
            .AsNoTracking()
            .Where(snapshot => dueCompanyIds.Contains(snapshot.CompanyId))
            .Select(snapshot => new
            {
                snapshot.Id,
                snapshot.CompanyId,
                snapshot.Price,
                snapshot.Capitalization,
                snapshot.CreatedInCycleId,
                snapshot.CreatedAt,
                SourcePriority = 0,
            })
            .ToListAsync();
        var pricesByCompany = livePrices
            .Concat(archivedPrices)
            .Where(snapshot =>
                cycleById.TryGetValue(snapshot.CreatedInCycleId, out var cycle)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && cycle.TradingDayNumber <= maximumEndDay)
            .Select(snapshot =>
            {
                var cycle = cycleById[snapshot.CreatedInCycleId];
                return new PricePoint(
                    snapshot.Id,
                    snapshot.CompanyId,
                    snapshot.Price,
                    snapshot.Capitalization,
                    cycle.CycleNumber,
                    cycle.TradingDayNumber,
                    snapshot.CreatedAt,
                    snapshot.SourcePriority);
            })
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(snapshot => snapshot.Id)
                    .Select(duplicates => duplicates
                        .OrderByDescending(snapshot => snapshot.SourcePriority)
                        .ThenBy(snapshot => snapshot.CreatedAt)
                        .First())
                    .OrderBy(snapshot => snapshot.CycleNumber)
                    .ThenBy(snapshot => snapshot.CreatedAt)
                    .ThenBy(snapshot => snapshot.Id)
                    .ToArray());

        var denominationEvents = await dbContext.StockDenominationEvents
            .AsNoTracking()
            .Where(denominationEvent => dueCompanyIds.Contains(denominationEvent.CompanyId))
            .OrderBy(denominationEvent => denominationEvent.EffectiveInCycleNumber)
            .ThenBy(denominationEvent => denominationEvent.Id)
            .ToListAsync();
        var denominationByCompany = denominationEvents
            .Where(denominationEvent =>
                cycleById.TryGetValue(denominationEvent.EffectiveInCycleId, out var cycle)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && cycle.CycleNumber <= currentCycle.CycleNumber)
            .GroupBy(denominationEvent => denominationEvent.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var emissions = await dbContext.ShareEmissions
            .AsNoTracking()
            .Where(emission => dueCompanyIds.Contains(emission.CompanyId))
            .OrderBy(emission => emission.CreatedInCycleId)
            .ThenBy(emission => emission.Id)
            .ToListAsync();
        var emissionsByCompany = emissions
            .Where(emission =>
                cycleById.TryGetValue(emission.CreatedInCycleId, out var cycle)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && cycle.TradingDayNumber >= minimumStartDay
                && cycle.CycleNumber <= currentCycle.CycleNumber)
            .GroupBy(emission => emission.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var primaryIssuances = await dbContext.PrimaryIssuanceEvents
            .AsNoTracking()
            .Where(issuance => dueCompanyIds.Contains(issuance.CompanyId))
            .OrderBy(issuance => issuance.CreatedInCycleId)
            .ThenBy(issuance => issuance.Id)
            .ToListAsync();
        var primaryIssuancesByCompany = primaryIssuances
            .Where(issuance =>
                cycleById.TryGetValue(issuance.CreatedInCycleId, out var cycle)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && cycle.TradingDayNumber >= minimumStartDay
                && cycle.CycleNumber <= currentCycle.CycleNumber)
            .GroupBy(issuance => issuance.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var investments = await dbContext.CompanyInvestments
            .AsNoTracking()
            .Where(investment => dueCompanyIds.Contains(investment.CompanyId))
            .OrderBy(investment => investment.CreatedInCycleId)
            .ThenBy(investment => investment.Id)
            .ToListAsync();
        var investmentsByCompany = investments
            .Where(investment =>
                cycleById.TryGetValue(investment.CreatedInCycleId, out var cycle)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && cycle.TradingDayNumber >= minimumStartDay
                && cycle.CycleNumber <= currentCycle.CycleNumber)
            .GroupBy(investment => investment.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var corporateCashTransactions = await dbContext.CorporateCashTransactions
            .AsNoTracking()
            .Where(transaction => dueCompanyIds.Contains(transaction.CompanyId))
            .OrderBy(transaction => transaction.CreatedInCycleId)
            .ThenBy(transaction => transaction.Id)
            .ToListAsync();
        corporateCashTransactions.AddRange(dbContext.ChangeTracker
            .Entries<CorporateCashTransaction>()
            .Where(entry => entry.State == EntityState.Added
                && dueCompanyIds.Contains(entry.Entity.CompanyId))
            .Select(entry => entry.Entity));
        var corporateCashByCompany = corporateCashTransactions
            .Where(transaction =>
                cycleById.TryGetValue(transaction.CreatedInCycleId, out var cycle)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && cycle.CycleNumber <= currentCycle.CycleNumber)
            .GroupBy(transaction => transaction.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var dividends = await dbContext.CompanyDividendEvents
            .AsNoTracking()
            .Where(dividendEvent =>
                dueCompanyIds.Contains(dividendEvent.CompanyId)
                && dividendEvent.TradingDayNumber <= maximumEndDay)
            .OrderBy(dividendEvent => dividendEvent.TradingDayNumber)
            .ThenBy(dividendEvent => dividendEvent.Id)
            .ToListAsync();
        var dividendsByCompany = dividends
            .GroupBy(dividendEvent => dividendEvent.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var financialSnapshots = await dbContext.CompanyFinancialSnapshots
            .AsNoTracking()
            .Where(snapshot =>
                dueCompanyIds.Contains(snapshot.CompanyId)
                && snapshot.TradingDayNumber >= minimumStartDay
                && snapshot.TradingDayNumber <= maximumEndDay)
            .ToListAsync();
        var financialsByCompany = financialSnapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var liveSentiment = await dbContext.SectorSentimentSnapshots
            .AsNoTracking()
            .Where(snapshot => dueIndustryIds.Contains(snapshot.IndustryId))
            .Select(snapshot => new
            {
                snapshot.Id,
                snapshot.IndustryId,
                snapshot.SentimentValue,
                snapshot.CreatedInCycleId,
            })
            .ToListAsync();
        var archivedSentiment = await dbContext.SectorSentimentSnapshotArchives
            .AsNoTracking()
            .Where(snapshot => dueIndustryIds.Contains(snapshot.IndustryId))
            .Select(snapshot => new
            {
                snapshot.Id,
                snapshot.IndustryId,
                snapshot.SentimentValue,
                snapshot.CreatedInCycleId,
            })
            .ToListAsync();
        var sentimentByIndustry = liveSentiment
            .Concat(archivedSentiment)
            .Where(snapshot =>
                cycleById.TryGetValue(snapshot.CreatedInCycleId, out var cycle)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && cycle.TradingDayNumber >= minimumStartDay
                && cycle.TradingDayNumber <= maximumEndDay)
            .Select(snapshot =>
            {
                var cycle = cycleById[snapshot.CreatedInCycleId];
                return new SentimentPoint(
                    snapshot.Id,
                    snapshot.IndustryId,
                    snapshot.SentimentValue,
                    cycle.CycleNumber,
                    cycle.TradingDayNumber);
            })
            .GroupBy(snapshot => snapshot.IndustryId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(snapshot => snapshot.TradingDayNumber)
                    .ThenBy(snapshot => snapshot.CycleNumber)
                    .ThenBy(snapshot => snapshot.Id)
                    .ToArray());

        var auditorOffset = (currentCycle.TradingDayNumber - 1) % auditors.Count;
        for (var index = 0; index < due.Length; index++)
        {
            var candidate = due[index];
            var companyId = candidate.Company.Id;
            var priceWindow = PriceWindowWithCarry(
                pricesByCompany.GetValueOrDefault(companyId),
                candidate.EvaluationStartTradingDayNumber,
                candidate.EvaluationEndTradingDayNumber);
            var denominationWindow = Window(
                denominationByCompany.GetValueOrDefault(companyId),
                cycleById,
                candidate.EvaluationStartTradingDayNumber,
                candidate.EvaluationEndTradingDayNumber,
                denominationEvent => denominationEvent.EffectiveInCycleId);
            var emissionWindow = Window(
                emissionsByCompany.GetValueOrDefault(companyId),
                cycleById,
                candidate.EvaluationStartTradingDayNumber,
                candidate.EvaluationEndTradingDayNumber,
                emission => emission.CreatedInCycleId);
            var supplyDenominations = SupplyWindow(
                denominationByCompany.GetValueOrDefault(companyId),
                cycleById,
                candidate.EvaluationStartTradingDayNumber,
                currentCycle.CycleNumber,
                denominationEvent => denominationEvent.EffectiveInCycleId);
            var supplyEmissions = SupplyWindow(
                emissionsByCompany.GetValueOrDefault(companyId),
                cycleById,
                candidate.EvaluationStartTradingDayNumber,
                currentCycle.CycleNumber,
                emission => emission.CreatedInCycleId);
            var supplyPrimaryIssuances = SupplyWindow(
                primaryIssuancesByCompany.GetValueOrDefault(companyId),
                cycleById,
                candidate.EvaluationStartTradingDayNumber,
                currentCycle.CycleNumber,
                issuance => issuance.CreatedInCycleId);
            var supplyInvestments = SupplyWindow(
                investmentsByCompany.GetValueOrDefault(companyId),
                cycleById,
                candidate.EvaluationStartTradingDayNumber,
                currentCycle.CycleNumber,
                investment => investment.CreatedInCycleId);
            var financial = financialsByCompany.GetValueOrDefault(companyId)?
                .Where(snapshot =>
                    snapshot.TradingDayNumber >= candidate.EvaluationStartTradingDayNumber
                    && snapshot.TradingDayNumber <= candidate.EvaluationEndTradingDayNumber)
                .OrderByDescending(snapshot => snapshot.TradingDayNumber)
                .ThenByDescending(snapshot => snapshot.Moment)
                .ThenByDescending(snapshot => cycleById.GetValueOrDefault(snapshot.CreatedInCycleId)?.CycleNumber ?? 0)
                .ThenByDescending(snapshot => snapshot.Id)
                .FirstOrDefault();
            var latestDividend = dividendsByCompany.GetValueOrDefault(companyId)?
                .Where(dividendEvent =>
                    dividendEvent.TradingDayNumber <= candidate.EvaluationEndTradingDayNumber)
                .OrderByDescending(dividendEvent => dividendEvent.TradingDayNumber)
                .ThenByDescending(dividendEvent => dividendEvent.Id)
                .FirstOrDefault();
            var sentimentWindow = Window(
                sentimentByIndustry.GetValueOrDefault(candidate.Company.IndustryId),
                candidate.EvaluationStartTradingDayNumber,
                candidate.EvaluationEndTradingDayNumber);

            var priceEvidence = PriceEvidenceFor(
                priceWindow,
                denominationByCompany.GetValueOrDefault(companyId) ?? []);
            var openingIssuedShares = OpeningIssuedShares(
                candidate.Company.IssuedSharesCount,
                supplyDenominations,
                supplyEmissions,
                supplyPrimaryIssuances,
                supplyInvestments,
                cycleById);
            var issuerCash = IssuerCashAtWindowEnd(
                candidate.Company.CashBalance,
                corporateCashByCompany.GetValueOrDefault(companyId) ?? [],
                cycleById,
                candidate.EvaluationEndTradingDayNumber,
                currentCycle.CycleNumber);
            var emittedShares = emissionWindow.Sum(emission => emission.SharesEmitted);
            var dilutionPercent = openingIssuedShares > 0
                ? Round6(100m * emittedShares / openingIssuedShares)
                : 0m;
            var industryEvidence = IndustryEvidenceFor(sentimentWindow);
            var modeledMaximumDividend = financial?.ExpectedDividendPool ?? 0m;
            var dividendCoverageRatio = financial?.DividendCoverageRatio ?? 0m;

            var scoring = scorer.Score(new CompanyAuditScoringInput(
                priceEvidence.AdjustedReturnPercent,
                priceEvidence.MaximumAdjustedCycleMovePercent,
                dilutionPercent,
                denominationWindow.Count(denominationEvent =>
                    denominationEvent.ActionType == StockDenominationActionType.Split),
                denominationWindow.Count(denominationEvent =>
                    denominationEvent.ActionType == StockDenominationActionType.ReverseSplit),
                latestDividend?.FundingOutcome,
                modeledMaximumDividend,
                dividendCoverageRatio,
                industryEvidence.Trend,
                financial?.ProfitabilityLevel ?? CompanyMetricLevel.Medium,
                financial?.FinancialVolatilityLevel ?? CompanyMetricLevel.Medium,
                financial?.ClosureRiskLevel ?? CompanyMetricLevel.Medium,
                financial?.ManagementOutlook ?? ManagementOutlook.Neutral,
                financial?.ManagementConfidenceScore ?? 0m,
                financial?.OperatingCashFlow ?? 0m));

            var rating = new CompanyRating
            {
                CompanyId = companyId,
                AuditorId = auditors[(auditorOffset + index) % auditors.Count].Id,
                Rating = scoring.Rating,
                ImpactPercent = null,
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
            };
            dbContext.CompanyRatings.Add(rating);
            rating.Evidence = new CompanyAuditEvidence
            {
                CompanyRating = rating,
                CompanyId = companyId,
                CompanyFinancialSnapshotId = financial?.Id,
                EvaluationStartTradingDayNumber = candidate.EvaluationStartTradingDayNumber,
                EvaluationEndTradingDayNumber = candidate.EvaluationEndTradingDayNumber,
                EffectiveTradingDayNumber = candidate.EffectiveTradingDayNumber,
                TotalScore = scoring.TotalScore,
                AdjustedReturnScore = scoring.AdjustedReturnScore,
                CycleJumpScore = scoring.CycleJumpScore,
                FreeShareEmissionScore = scoring.FreeShareEmissionScore,
                DenominationScore = scoring.DenominationScore,
                DividendOutcomeScore = scoring.DividendOutcomeScore,
                DividendCoverageScore = scoring.DividendCoverageScore,
                IndustryScore = scoring.IndustryScore,
                ProfitabilityFactorScore = scoring.ProfitabilityFactorScore,
                StabilityFactorScore = scoring.StabilityFactorScore,
                ClosureRiskFactorScore = scoring.ClosureRiskFactorScore,
                ManagementOutlookFactorScore = scoring.ManagementOutlookFactorScore,
                StartPrice = priceEvidence.StartPrice,
                EndPrice = priceEvidence.EndPrice,
                AdjustedReturnPercent = priceEvidence.AdjustedReturnPercent,
                MaximumAdjustedCycleMovePercent = priceEvidence.MaximumAdjustedCycleMovePercent,
                OpeningIssuedShares = openingIssuedShares,
                EmittedShares = emittedShares,
                FreeShareDilutionPercent = dilutionPercent,
                StockSplitCount = denominationWindow.Count(denominationEvent =>
                    denominationEvent.ActionType == StockDenominationActionType.Split),
                ReverseSplitCount = denominationWindow.Count(denominationEvent =>
                    denominationEvent.ActionType == StockDenominationActionType.ReverseSplit),
                LatestDividendEventId = latestDividend?.Id,
                IssuerCash = issuerCash,
                ModeledMaximumDividend = modeledMaximumDividend,
                DividendCoverageRatio = dividendCoverageRatio,
                OpeningIndustrySentiment = industryEvidence.Opening,
                ClosingIndustrySentiment = industryEvidence.Closing,
                IndustryTrend = industryEvidence.Trend,
            };
        }
    }

    public static int AuditorCountFor(int companyCount) =>
        Math.Max(1, (int)Math.Ceiling(companyCount * ReviewFraction));

    private async Task EnsureAuditorsExistAsync(int companyCount, DateTime now)
    {
        if (await dbContext.Auditors.AnyAsync())
        {
            return;
        }

        foreach (var (name, description) in DemoAuditorProfiles.Take(AuditorCountFor(companyCount)))
        {
            dbContext.Auditors.Add(new Auditor
            {
                Name = name,
                Description = description,
                CreatedAt = now,
            });
        }

        // Ratings store scalar auditor ids, so deterministic backfill is flushed once before the batch is staged.
        await dbContext.SaveChangesAsync();
    }

    private (int? Opening, int? Closing, IndustryTrend Trend) IndustryEvidenceFor(
        IReadOnlyList<SentimentPoint> window)
    {
        if (window.Count == 0)
        {
            return (null, null, IndustryTrend.Plateau);
        }

        var opening = window[0].SentimentValue;
        var closing = window[^1].SentimentValue;
        var change = closing - opening;
        var trend = change >= options.IndustryDirectionDeadband
            ? IndustryTrend.Rising
            : change <= -options.IndustryDirectionDeadband
                ? IndustryTrend.Falling
                : IndustryTrend.Plateau;
        return (opening, closing, trend);
    }

    private static PriceEvidence PriceEvidenceFor(
        IReadOnlyList<PricePoint> prices,
        IReadOnlyList<StockDenominationEvent> denominationEvents)
    {
        if (prices.Count == 0)
        {
            return new PriceEvidence(0m, 0m, 0m, 0m);
        }

        var startPrice = prices[0].Price;
        var endPrice = prices[^1].Price;
        var applicableDenominations = denominationEvents
            .Where(denominationEvent =>
                OccursAfter(denominationEvent, prices[0])
                && OccursAtOrBefore(denominationEvent, prices[^1]))
            .OrderBy(denominationEvent => denominationEvent.EffectiveInCycleNumber)
            .ThenBy(denominationEvent => denominationEvent.CreatedAt)
            .ThenBy(denominationEvent => denominationEvent.Id)
            .ToArray();
        var adjustedReturn = 0m;
        if (startPrice > 0m)
        {
            var adjustedFactor = endPrice / startPrice;
            foreach (var denominationEvent in applicableDenominations)
            {
                if (denominationEvent.PriceAfter > 0m)
                {
                    adjustedFactor *= denominationEvent.PriceBefore / denominationEvent.PriceAfter;
                }
            }

            adjustedReturn = Round6((adjustedFactor - 1m) * 100m);
        }

        var maximumMove = 0m;
        for (var index = 1; index < prices.Count; index++)
        {
            var previous = prices[index - 1];
            var current = prices[index];
            if (previous.Price <= 0m
                || IsDenominationBoundary(previous, current, applicableDenominations))
            {
                continue;
            }

            var move = Math.Abs((current.Price / previous.Price - 1m) * 100m);
            maximumMove = Math.Max(maximumMove, move);
        }

        return new PriceEvidence(
            startPrice,
            endPrice,
            adjustedReturn,
            Round6(maximumMove));
    }

    private static bool IsDenominationBoundary(
        PricePoint previous,
        PricePoint current,
        IReadOnlyList<StockDenominationEvent> denominationEvents) =>
        denominationEvents.Any(denominationEvent =>
            OccursAfter(denominationEvent, previous)
            && OccursAtOrBefore(denominationEvent, current));

    private static bool OccursAfter(
        StockDenominationEvent denominationEvent,
        PricePoint price) =>
        denominationEvent.EffectiveInCycleNumber > price.CycleNumber
        || (denominationEvent.EffectiveInCycleNumber == price.CycleNumber
            && denominationEvent.CreatedAt > price.CreatedAt);

    private static bool OccursAtOrBefore(
        StockDenominationEvent denominationEvent,
        PricePoint price) =>
        denominationEvent.EffectiveInCycleNumber < price.CycleNumber
        || (denominationEvent.EffectiveInCycleNumber == price.CycleNumber
            && denominationEvent.CreatedAt <= price.CreatedAt);

    private static int OpeningIssuedShares(
        int currentIssuedShares,
        IReadOnlyList<StockDenominationEvent> denominationEvents,
        IReadOnlyList<ShareEmission> emissions,
        IReadOnlyList<PrimaryIssuanceEvent> primaryIssuances,
        IReadOnlyList<CompanyInvestment> investments,
        IReadOnlyDictionary<int, CyclePoint> cycleById)
    {
        var changes = denominationEvents
            .Select(denominationEvent => new SupplyChange(
                denominationEvent.EffectiveInCycleNumber,
                ReverseSupplyPhase.Denomination,
                denominationEvent.Id,
                denominationEvent.IssuedSharesBefore,
                0))
            .Concat(emissions.Select(emission => new SupplyChange(
                cycleById.GetValueOrDefault(emission.CreatedInCycleId)?.CycleNumber ?? 0,
                ReverseSupplyPhase.FreeEmission,
                emission.Id,
                null,
                emission.SharesEmitted)))
            .Concat(primaryIssuances.Select(issuance => new SupplyChange(
                cycleById.GetValueOrDefault(issuance.CreatedInCycleId)?.CycleNumber ?? 0,
                ReverseSupplyPhase.PrimaryIssuance,
                issuance.Id,
                null,
                issuance.NewlyIssuedShares)))
            .Concat(investments.Select(investment => new SupplyChange(
                cycleById.GetValueOrDefault(investment.CreatedInCycleId)?.CycleNumber ?? 0,
                ReverseSupplyPhase.BigInvestment,
                investment.Id,
                null,
                investment.SharesIssued)))
            .OrderByDescending(change => change.CycleNumber)
            .ThenByDescending(change => change.Phase)
            .ThenByDescending(change => change.Id)
            .ToArray();
        var reconstructed = currentIssuedShares;
        foreach (var change in changes)
        {
            if (change.IssuedSharesBefore is int issuedSharesBefore)
            {
                reconstructed = issuedSharesBefore;
                continue;
            }

            if (change.SharesIssued > reconstructed)
            {
                throw new InvalidOperationException("Supply history exceeds the company's current issued shares.");
            }

            reconstructed -= change.SharesIssued;
        }

        return reconstructed;
    }

    private static decimal IssuerCashAtWindowEnd(
        decimal currentIssuerCash,
        IReadOnlyList<CorporateCashTransaction> transactions,
        IReadOnlyDictionary<int, CyclePoint> cycleById,
        int evaluationEndTradingDayNumber,
        int currentCycleNumber)
    {
        var reconstructed = currentIssuerCash;
        foreach (var transaction in transactions
            .Where(transaction =>
                cycleById.TryGetValue(transaction.CreatedInCycleId, out var cycle)
                && cycle.TradingDayNumber > evaluationEndTradingDayNumber
                && cycle.CycleNumber <= currentCycleNumber)
            .OrderByDescending(transaction =>
                cycleById.GetValueOrDefault(transaction.CreatedInCycleId)?.CycleNumber ?? 0)
            .ThenByDescending(transaction => transaction.Id))
        {
            reconstructed += transaction.Type switch
            {
                CorporateCashTransactionType.PrimaryIssuance => -transaction.Amount,
                CorporateCashTransactionType.OperatingIncome => -transaction.Amount,
                CorporateCashTransactionType.BigInvestment => -transaction.Amount,
                CorporateCashTransactionType.DividendDeclared => transaction.Amount,
                CorporateCashTransactionType.ClosureDistribution => transaction.Amount,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(transaction.Type),
                    transaction.Type,
                    "Unknown corporate cash transaction type."),
            };
        }

        return reconstructed;
    }

    private static T[] Window<T>(
        IReadOnlyList<T>? rows,
        IReadOnlyDictionary<int, CyclePoint> cycleById,
        int startDay,
        int endDay,
        Func<T, int> cycleId) =>
        rows?
            .Where(row =>
                cycleById.TryGetValue(cycleId(row), out var cycle)
                && cycle.TradingDayNumber >= startDay
                && cycle.TradingDayNumber <= endDay)
            .ToArray()
        ?? [];

    private static PricePoint[] PriceWindowWithCarry(
        IReadOnlyList<PricePoint>? rows,
        int startDay,
        int endDay)
    {
        if (rows is not { Count: > 0 })
        {
            return [];
        }

        var anchor = rows.LastOrDefault(row => row.TradingDayNumber < startDay);
        var window = rows
            .Where(row =>
                row.TradingDayNumber >= startDay
                && row.TradingDayNumber <= endDay)
            .ToList();
        if (anchor is not null)
        {
            window.Insert(0, anchor);
        }

        return window.ToArray();
    }

    private static T[] SupplyWindow<T>(
        IReadOnlyList<T>? rows,
        IReadOnlyDictionary<int, CyclePoint> cycleById,
        int startDay,
        int currentCycleNumber,
        Func<T, int> cycleId) =>
        rows?
            .Where(row =>
                cycleById.TryGetValue(cycleId(row), out var cycle)
                && cycle.TradingDayNumber >= startDay
                && cycle.CycleNumber <= currentCycleNumber)
            .ToArray()
        ?? [];

    private static SentimentPoint[] Window(
        IReadOnlyList<SentimentPoint>? rows,
        int startDay,
        int endDay) =>
        rows?
            .Where(row =>
                row.TradingDayNumber >= startDay
                && row.TradingDayNumber <= endDay)
            .ToArray()
        ?? [];

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private sealed record CyclePoint(
        int Id,
        int CycleNumber,
        int TradingCycleNumber,
        int MarketRunId,
        int TradingDayNumber);

    private sealed record DueCompany(
        Company Company,
        int EvaluationStartTradingDayNumber,
        int EvaluationEndTradingDayNumber,
        int EffectiveTradingDayNumber,
        int DueTradingDayNumber);

    private sealed record PricePoint(
        int Id,
        int CompanyId,
        decimal Price,
        decimal? Capitalization,
        int CycleNumber,
        int TradingDayNumber,
        DateTime CreatedAt,
        int SourcePriority);

    private sealed record SentimentPoint(
        int Id,
        int IndustryId,
        int SentimentValue,
        int CycleNumber,
        int TradingDayNumber);

    private sealed record PriceEvidence(
        decimal StartPrice,
        decimal EndPrice,
        decimal AdjustedReturnPercent,
        decimal MaximumAdjustedCycleMovePercent);

    private sealed record SupplyChange(
        int CycleNumber,
        ReverseSupplyPhase Phase,
        int Id,
        int? IssuedSharesBefore,
        int SharesIssued);

    private enum ReverseSupplyPhase
    {
        Denomination = 4,
        FreeEmission = 5,
        PrimaryIssuance = 6,
        BigInvestment = 7,
    }
}
