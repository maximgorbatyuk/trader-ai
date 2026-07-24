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
    private readonly CompanyFinancialOptions financialOptions;
    private readonly CompanyAuditScorer scorer;

    public AuditorService(
        AppDbContext dbContext,
        IOptions<AuditorOptions> options,
        IOptions<CompanyFinancialOptions> financialOptions)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(financialOptions);
        if (!options.Value.IsValid())
        {
            throw new ArgumentException("Auditor options are invalid.", nameof(options));
        }
        if (!financialOptions.Value.IsValid())
        {
            throw new ArgumentException("Company financial options are invalid.", nameof(financialOptions));
        }

        this.dbContext = dbContext;
        this.options = options.Value;
        this.financialOptions = financialOptions.Value;
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

        var currentCycle = await (
            from cycle in dbContext.MarketCycles.AsNoTracking()
            join day in dbContext.TradingDays.AsNoTracking() on cycle.TradingDayId equals day.Id
            where cycle.Id == currentCycleId
            select new CyclePoint(
                cycle.Id,
                cycle.CycleNumber,
                cycle.TradingCycleNumber,
                cycle.MarketRunId,
                day.DayNumber))
            .SingleOrDefaultAsync();
        if (currentCycle is null
            || currentCycle.CycleNumber != currentCycleNumber
            || currentCycle.TradingCycleNumber != 1)
        {
            return;
        }

        var listedCompanies = await (
            from company in dbContext.Companies
            where company.ClosedInCycleId == null
            join listingCycle in dbContext.MarketCycles.AsNoTracking()
                on company.CreatedInCycleId equals (int?)listingCycle.Id into listingCycles
            from listingCycle in listingCycles.DefaultIfEmpty()
            join listingDay in dbContext.TradingDays.AsNoTracking()
                on listingCycle.TradingDayId equals listingDay.Id into listingDays
            from listingDay in listingDays.DefaultIfEmpty()
            orderby company.Id
            select new ListedCompany(
                company,
                listingDay == null ? 1 : listingDay.DayNumber))
            .ToListAsync();
        if (listedCompanies.Count == 0)
        {
            return;
        }

        var companies = listedCompanies.Select(row => row.Company).ToArray();
        var companyIds = companies.Select(company => company.Id).ToArray();
        var latestEffectiveDayByCompany = await dbContext.CompanyAuditEvidence
                .AsNoTracking()
                .Where(evidence => companyIds.Contains(evidence.CompanyId))
                .GroupBy(evidence => evidence.CompanyId)
                .Select(group => new
                {
                    CompanyId = group.Key,
                    EffectiveTradingDayNumber = group.Max(
                        evidence => evidence.EffectiveTradingDayNumber),
                })
                .ToDictionaryAsync(
                group => group.CompanyId,
                group => group.EffectiveTradingDayNumber);
        foreach (var evidence in dbContext.ChangeTracker
            .Entries<CompanyAuditEvidence>()
            .Where(entry => entry.State == EntityState.Added
                && companyIds.Contains(entry.Entity.CompanyId))
            .Select(entry => entry.Entity))
        {
            latestEffectiveDayByCompany[evidence.CompanyId] = Math.Max(
                latestEffectiveDayByCompany.GetValueOrDefault(evidence.CompanyId),
                evidence.EffectiveTradingDayNumber);
        }

        var due = listedCompanies
            .Select(row =>
            {
                var previousEffectiveDay = latestEffectiveDayByCompany.GetValueOrDefault(row.Company.Id);
                var dueFromDay = previousEffectiveDay > 0
                    ? previousEffectiveDay
                    : row.ListingTradingDayNumber;
                return new DueCompany(
                    row.Company,
                    previousEffectiveDay > 0
                        ? previousEffectiveDay
                        : row.ListingTradingDayNumber,
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

        await EnsureAuditorsExistAsync(companies.Length, now);
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

        var cycles = await (
            from cycle in dbContext.MarketCycles.AsNoTracking()
            join day in dbContext.TradingDays.AsNoTracking() on cycle.TradingDayId equals day.Id
            where cycle.MarketRunId == currentCycle.MarketRunId
                && cycle.CycleNumber <= currentCycle.CycleNumber
                && day.DayNumber >= minimumStartDay
            select new CyclePoint(
                cycle.Id,
                cycle.CycleNumber,
                cycle.TradingCycleNumber,
                cycle.MarketRunId,
                day.DayNumber))
            .ToListAsync();
        var cycleById = cycles.ToDictionary(cycle => cycle.Id);

        var livePriceRows = await (
            from snapshot in dbContext.PriceSnapshots.AsNoTracking()
            join cycle in dbContext.MarketCycles.AsNoTracking()
                on snapshot.CreatedInCycleId equals cycle.Id
            join day in dbContext.TradingDays.AsNoTracking()
                on cycle.TradingDayId equals day.Id
            where dueCompanyIds.Contains(snapshot.CompanyId)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && cycle.CycleNumber <= currentCycle.CycleNumber
                && day.DayNumber <= maximumEndDay
                && (day.DayNumber >= minimumStartDay
                    || !(
                        from otherSnapshot in dbContext.PriceSnapshots.AsNoTracking()
                        join otherCycle in dbContext.MarketCycles.AsNoTracking()
                            on otherSnapshot.CreatedInCycleId equals otherCycle.Id
                        join otherDay in dbContext.TradingDays.AsNoTracking()
                            on otherCycle.TradingDayId equals otherDay.Id
                        where otherSnapshot.CompanyId == snapshot.CompanyId
                            && otherCycle.MarketRunId == currentCycle.MarketRunId
                            && otherCycle.CycleNumber <= currentCycle.CycleNumber
                            && otherDay.DayNumber < minimumStartDay
                            && (otherCycle.CycleNumber > cycle.CycleNumber
                                || (otherCycle.CycleNumber == cycle.CycleNumber
                                    && otherSnapshot.CreatedAt > snapshot.CreatedAt)
                                || (otherCycle.CycleNumber == cycle.CycleNumber
                                    && otherSnapshot.CreatedAt == snapshot.CreatedAt
                                    && otherSnapshot.Id > snapshot.Id))
                        select otherSnapshot.Id)
                    .Any())
            select new
            {
                snapshot.Id,
                snapshot.CompanyId,
                snapshot.Price,
                snapshot.Capitalization,
                CycleNumber = cycle.CycleNumber,
                TradingDayNumber = day.DayNumber,
                snapshot.CreatedAt,
            })
            .ToListAsync();
        var livePrices = livePriceRows
            .Select(snapshot => new PricePoint(
                snapshot.Id,
                snapshot.CompanyId,
                snapshot.Price,
                snapshot.Capitalization,
                snapshot.CycleNumber,
                snapshot.TradingDayNumber,
                snapshot.CreatedAt,
                1))
            .ToList();

        var archivedPriceRows = await (
            from snapshot in dbContext.PriceSnapshotArchives.AsNoTracking()
            join cycle in dbContext.MarketCycles.AsNoTracking()
                on snapshot.CreatedInCycleId equals cycle.Id
            join day in dbContext.TradingDays.AsNoTracking()
                on cycle.TradingDayId equals day.Id
            where dueCompanyIds.Contains(snapshot.CompanyId)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && cycle.CycleNumber <= currentCycle.CycleNumber
                && day.DayNumber <= maximumEndDay
                && (day.DayNumber >= minimumStartDay
                    || !(
                        from otherSnapshot in dbContext.PriceSnapshotArchives.AsNoTracking()
                        join otherCycle in dbContext.MarketCycles.AsNoTracking()
                            on otherSnapshot.CreatedInCycleId equals otherCycle.Id
                        join otherDay in dbContext.TradingDays.AsNoTracking()
                            on otherCycle.TradingDayId equals otherDay.Id
                        where otherSnapshot.CompanyId == snapshot.CompanyId
                            && otherCycle.MarketRunId == currentCycle.MarketRunId
                            && otherCycle.CycleNumber <= currentCycle.CycleNumber
                            && otherDay.DayNumber < minimumStartDay
                            && (otherCycle.CycleNumber > cycle.CycleNumber
                                || (otherCycle.CycleNumber == cycle.CycleNumber
                                    && otherSnapshot.CreatedAt > snapshot.CreatedAt)
                                || (otherCycle.CycleNumber == cycle.CycleNumber
                                    && otherSnapshot.CreatedAt == snapshot.CreatedAt
                                    && otherSnapshot.Id > snapshot.Id))
                        select otherSnapshot.Id)
                    .Any())
            select new
            {
                snapshot.Id,
                snapshot.CompanyId,
                snapshot.Price,
                snapshot.Capitalization,
                CycleNumber = cycle.CycleNumber,
                TradingDayNumber = day.DayNumber,
                snapshot.CreatedAt,
            })
            .ToListAsync();
        var archivedPrices = archivedPriceRows
            .Select(snapshot => new PricePoint(
                snapshot.Id,
                snapshot.CompanyId,
                snapshot.Price,
                snapshot.Capitalization,
                snapshot.CycleNumber,
                snapshot.TradingDayNumber,
                snapshot.CreatedAt,
                0))
            .ToList();
        var pricesByCompany = livePrices
            .Concat(archivedPrices)
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

        var minimumPriceCycle = livePrices
            .Concat(archivedPrices)
            .Select(snapshot => snapshot.CycleNumber)
            .DefaultIfEmpty(currentCycle.CycleNumber)
            .Min();
        var denominationEvents = await (
            from denominationEvent in dbContext.StockDenominationEvents.AsNoTracking()
            join cycle in dbContext.MarketCycles.AsNoTracking()
                on denominationEvent.EffectiveInCycleId equals cycle.Id
            join day in dbContext.TradingDays.AsNoTracking()
                on cycle.TradingDayId equals day.Id
            where dueCompanyIds.Contains(denominationEvent.CompanyId)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && cycle.CycleNumber <= currentCycle.CycleNumber
                && (day.DayNumber >= minimumStartDay
                    || cycle.CycleNumber >= minimumPriceCycle)
            select denominationEvent)
            .OrderBy(denominationEvent => denominationEvent.EffectiveInCycleNumber)
            .ThenBy(denominationEvent => denominationEvent.Id)
            .ToListAsync();
        var denominationByCompany = denominationEvents
            .GroupBy(denominationEvent => denominationEvent.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var emissions = await (
            from emission in dbContext.ShareEmissions.AsNoTracking()
            join cycle in dbContext.MarketCycles.AsNoTracking()
                on emission.CreatedInCycleId equals cycle.Id
            join day in dbContext.TradingDays.AsNoTracking()
                on cycle.TradingDayId equals day.Id
            where dueCompanyIds.Contains(emission.CompanyId)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && day.DayNumber >= minimumStartDay
                && cycle.CycleNumber <= currentCycle.CycleNumber
            select emission)
            .OrderBy(emission => emission.CreatedInCycleId)
            .ThenBy(emission => emission.Id)
            .ToListAsync();
        var emissionsByCompany = emissions
            .GroupBy(emission => emission.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var primaryIssuances = await (
            from issuance in dbContext.PrimaryIssuanceEvents.AsNoTracking()
            join cycle in dbContext.MarketCycles.AsNoTracking()
                on issuance.CreatedInCycleId equals cycle.Id
            join day in dbContext.TradingDays.AsNoTracking()
                on cycle.TradingDayId equals day.Id
            where dueCompanyIds.Contains(issuance.CompanyId)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && day.DayNumber >= minimumStartDay
                && cycle.CycleNumber <= currentCycle.CycleNumber
            select issuance)
            .OrderBy(issuance => issuance.CreatedInCycleId)
            .ThenBy(issuance => issuance.Id)
            .ToListAsync();
        var primaryIssuancesByCompany = primaryIssuances
            .GroupBy(issuance => issuance.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var investments = await (
            from investment in dbContext.CompanyInvestments.AsNoTracking()
            join cycle in dbContext.MarketCycles.AsNoTracking()
                on investment.CreatedInCycleId equals cycle.Id
            join day in dbContext.TradingDays.AsNoTracking()
                on cycle.TradingDayId equals day.Id
            where dueCompanyIds.Contains(investment.CompanyId)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && day.DayNumber >= minimumStartDay
                && cycle.CycleNumber <= currentCycle.CycleNumber
            select investment)
            .OrderBy(investment => investment.CreatedInCycleId)
            .ThenBy(investment => investment.Id)
            .ToListAsync();
        var investmentsByCompany = investments
            .GroupBy(investment => investment.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var corporateCashTransactions = await (
            from transaction in dbContext.CorporateCashTransactions.AsNoTracking()
            join cycle in dbContext.MarketCycles.AsNoTracking()
                on transaction.CreatedInCycleId equals cycle.Id
            join day in dbContext.TradingDays.AsNoTracking()
                on cycle.TradingDayId equals day.Id
            where dueCompanyIds.Contains(transaction.CompanyId)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && day.DayNumber > maximumEndDay
                && cycle.CycleNumber <= currentCycle.CycleNumber
            select transaction)
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
                && cycle.TradingDayNumber > maximumEndDay
                && cycle.CycleNumber <= currentCycle.CycleNumber)
            .GroupBy(transaction => transaction.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var dividends = await (
            from dividendEvent in dbContext.CompanyDividendEvents.AsNoTracking()
            join cycle in dbContext.MarketCycles.AsNoTracking()
                on dividendEvent.CreatedInCycleId equals cycle.Id
            where dueCompanyIds.Contains(dividendEvent.CompanyId)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && dividendEvent.TradingDayNumber <= maximumEndDay
                && cycle.CycleNumber <= currentCycle.CycleNumber
            select dividendEvent)
            .GroupBy(dividendEvent => dividendEvent.CompanyId)
            .Select(group => group
                .OrderByDescending(dividendEvent => dividendEvent.TradingDayNumber)
                .ThenByDescending(dividendEvent => dividendEvent.Id)
                .First())
            .ToListAsync();
        var dividendsByCompany = dividends
            .GroupBy(dividendEvent => dividendEvent.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var financialSnapshots = await (
            from snapshot in dbContext.CompanyFinancialSnapshots.AsNoTracking()
            join cycle in dbContext.MarketCycles.AsNoTracking()
                on snapshot.CreatedInCycleId equals cycle.Id
            where dueCompanyIds.Contains(snapshot.CompanyId)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && snapshot.TradingDayNumber >= minimumStartDay
                && snapshot.TradingDayNumber <= maximumEndDay
                && cycle.CycleNumber <= currentCycle.CycleNumber
            select snapshot)
            .ToListAsync();
        var financialsByCompany = financialSnapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var liveSentiment = await (
            from snapshot in dbContext.SectorSentimentSnapshots.AsNoTracking()
            join cycle in dbContext.MarketCycles.AsNoTracking()
                on snapshot.CreatedInCycleId equals cycle.Id
            join day in dbContext.TradingDays.AsNoTracking()
                on cycle.TradingDayId equals day.Id
            where dueIndustryIds.Contains(snapshot.IndustryId)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && day.DayNumber >= minimumStartDay
                && day.DayNumber <= maximumEndDay
                && cycle.CycleNumber <= currentCycle.CycleNumber
            select new SentimentPoint(
                snapshot.Id,
                snapshot.IndustryId,
                snapshot.SentimentValue,
                cycle.CycleNumber,
                day.DayNumber))
            .ToListAsync();
        var archivedSentiment = await (
            from snapshot in dbContext.SectorSentimentSnapshotArchives.AsNoTracking()
            join cycle in dbContext.MarketCycles.AsNoTracking()
                on snapshot.CreatedInCycleId equals cycle.Id
            join day in dbContext.TradingDays.AsNoTracking()
                on cycle.TradingDayId equals day.Id
            where dueIndustryIds.Contains(snapshot.IndustryId)
                && cycle.MarketRunId == currentCycle.MarketRunId
                && day.DayNumber >= minimumStartDay
                && day.DayNumber <= maximumEndDay
                && cycle.CycleNumber <= currentCycle.CycleNumber
            select new SentimentPoint(
                snapshot.Id,
                snapshot.IndustryId,
                snapshot.SentimentValue,
                cycle.CycleNumber,
                day.DayNumber))
            .ToListAsync();
        var sentimentByIndustry = liveSentiment
            .Concat(archivedSentiment)
            .GroupBy(snapshot => snapshot.IndustryId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(snapshot => snapshot.TradingDayNumber)
                    .ThenBy(snapshot => snapshot.CycleNumber)
                    .ThenBy(snapshot => snapshot.Id)
                    .ToArray());

        var auditorOffset = (currentCycle.TradingDayNumber - 1) % auditors.Count;
        var stagedAudits = new List<(CompanyRating Rating, CompanyAuditEvidence Evidence)>(due.Length);
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
                financial is not null,
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
            var evidence = new CompanyAuditEvidence
            {
                CompanyRating = rating,
                CompanyId = companyId,
                CompanyFinancialSnapshotId = financial?.Id,
                BusinessRiskLevel = financial is null
                    ? CompanyMetricLevel.Medium
                    : financialOptions.ClassifyLevel(financial.BusinessRiskScore),
                EvaluationStartTradingDayNumber = candidate.EvaluationStartTradingDayNumber,
                EvaluationEndTradingDayNumber = candidate.EvaluationEndTradingDayNumber,
                EffectiveTradingDayNumber = candidate.EffectiveTradingDayNumber,
                RuleVersion = CompanyAuditScorer.RuleVersion,
                Notes = $"Trading days {candidate.EvaluationStartTradingDayNumber}-{candidate.EvaluationEndTradingDayNumber}: score {scoring.TotalScore}, rating {scoring.Rating}.",
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
            rating.Evidence = evidence;
            stagedAudits.Add((rating, evidence));
        }

        await PublishPortfolioSummaryAsync(stagedAudits, currentCycleId, now);
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

    private async Task PublishPortfolioSummaryAsync(
        IReadOnlyList<(CompanyRating Rating, CompanyAuditEvidence Evidence)> stagedAudits,
        int currentCycleId,
        DateTime now)
    {
        if (stagedAudits.Count == 0)
        {
            return;
        }

        var playerIds = await dbContext.Participants
            .AsNoTracking()
            .Where(participant => participant.Type == ParticipantType.Player)
            .Select(participant => participant.Id)
            .ToArrayAsync();
        var managedFundParticipantIds = await dbContext.CollectiveFunds
            .AsNoTracking()
            .Where(fund => fund.IsPlayerManaged)
            .Select(fund => fund.ParticipantId)
            .ToArrayAsync();
        var ownerIds = playerIds
            .Concat(managedFundParticipantIds)
            .Distinct()
            .ToArray();
        if (ownerIds.Length == 0)
        {
            return;
        }

        var stagedByCompany = stagedAudits.ToDictionary(
            audit => audit.Rating.CompanyId);
        var auditedCompanyIds = stagedByCompany.Keys.ToArray();
        var heldRows = await dbContext.Holdings
            .AsNoTracking()
            .Where(holding =>
                ownerIds.Contains(holding.ParticipantId)
                && auditedCompanyIds.Contains(holding.CompanyId)
                && holding.Quantity > 0)
            .Select(holding => new
            {
                holding.ParticipantId,
                holding.CompanyId,
                holding.Quantity,
            })
            .ToListAsync();
        var heldAudits = heldRows
            .GroupBy(holding => holding.CompanyId)
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                Audit = stagedByCompany[group.Key],
                PlayerQuantity = group
                    .Where(holding => playerIds.Contains(holding.ParticipantId))
                    .Sum(holding => holding.Quantity),
                ManagedFundQuantity = group
                    .Where(holding => managedFundParticipantIds.Contains(holding.ParticipantId))
                    .Sum(holding => holding.Quantity),
            })
            .Where(row => row.PlayerQuantity > 0 || row.ManagedFundQuantity > 0)
            .ToArray();
        if (heldAudits.Length == 0)
        {
            return;
        }

        var averageScore = Round6(
            heldAudits.Sum(row => (decimal)row.Audit.Evidence.TotalScore)
            / heldAudits.Length);
        var news = new NewsPost
        {
            Title = "Portfolio audit update",
            Content = $"Auditors published new evidence-backed ratings for {heldAudits.Length} portfolio companies.",
            PublishedInCycleId = currentCycleId,
            PublishedAt = now,
            Scope = NewsImpactScope.None,
            Category = NewsCategory.PortfolioAudit,
        };
        var summary = new PortfolioAuditSummary
        {
            NewsPost = news,
            EvaluationStartTradingDayNumber = heldAudits.Min(
                row => row.Audit.Evidence.EvaluationStartTradingDayNumber),
            EvaluationEndTradingDayNumber = heldAudits.Max(
                row => row.Audit.Evidence.EvaluationEndTradingDayNumber),
            EffectiveTradingDayNumber = heldAudits.Max(
                row => row.Audit.Evidence.EffectiveTradingDayNumber),
            ExtraRaisedExpectationsCount = heldAudits.Count(
                row => row.Audit.Rating.Rating == CompanyRiskRating.ExtraRaisedExpectations),
            RaisedExpectationsCount = heldAudits.Count(
                row => row.Audit.Rating.Rating == CompanyRiskRating.RaisedExpectations),
            StableCount = heldAudits.Count(
                row => row.Audit.Rating.Rating == CompanyRiskRating.Stable),
            LowRiskCount = heldAudits.Count(
                row => row.Audit.Rating.Rating == CompanyRiskRating.LowRisk),
            HighRiskCount = heldAudits.Count(
                row => row.Audit.Rating.Rating == CompanyRiskRating.HighRisk),
            AverageScore = averageScore,
            OverallDirection = averageScore >= 2m
                ? PortfolioAuditDirection.Positive
                : averageScore <= -2m
                    ? PortfolioAuditDirection.Negative
                    : PortfolioAuditDirection.Neutral,
            CreatedAt = now,
        };
        foreach (var row in heldAudits)
        {
            summary.Items.Add(new PortfolioAuditSummaryItem
            {
                CompanyId = row.Audit.Rating.CompanyId,
                CompanyRating = row.Audit.Rating,
                PlayerQuantity = row.PlayerQuantity,
                ManagedFundQuantity = row.ManagedFundQuantity,
            });
        }

        dbContext.PortfolioAuditSummaries.Add(summary);
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

        if (reconstructed < 0m)
        {
            throw new InvalidOperationException(
                "Corporate cash history reconstructs a negative issuer balance.");
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

    private sealed record ListedCompany(
        Company Company,
        int ListingTradingDayNumber);

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
