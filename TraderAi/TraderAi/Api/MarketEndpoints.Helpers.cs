using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Api;

public static partial class MarketEndpoints
{
    private static IQueryable<Loan> FilterLoansByStatus(IQueryable<Loan> query, string? status) =>
        status?.ToLowerInvariant() switch
        {
            "closed" => query.Where(loan => loan.Status == LoanStatus.Closed),
            "all" => query,
            _ => query.Where(loan => loan.Status == LoanStatus.Open),
        };

    private static async Task<List<LoanResponse>> BuildLoanResponsesAsync(AppDbContext dbContext, IReadOnlyList<Loan> loans)
    {
        if (loans.Count == 0)
        {
            return [];
        }

        var bankIds = loans.Select(loan => loan.BankId).Distinct().ToList();
        var bankNames = await dbContext.Banks
            .Where(bank => bankIds.Contains(bank.Id))
            .ToDictionaryAsync(bank => bank.Id, bank => bank.Name);

        var participantIds = loans.Select(loan => loan.ParticipantId).Distinct().ToList();
        var participantNames = await LoanParticipantNamesAsync(dbContext, participantIds);

        var cycleNumbersById = await CycleNumbersByIdAsync(dbContext);
        var tradingDayNumbersById = await dbContext.TradingDays
            .ToDictionaryAsync(day => day.Id, day => day.DayNumber);
        var currentTradingDayNumber = await CurrentTradingDayNumberAsync(dbContext);

        return loans
            .Select(loan => ToLoanResponse(loan, bankNames, participantNames, cycleNumbersById, tradingDayNumbersById, currentTradingDayNumber))
            .ToList();
    }

    private static LoanResponse ToLoanResponse(
        Loan loan,
        IReadOnlyDictionary<int, string> bankNames,
        IReadOnlyDictionary<int, string> participantNames,
        IReadOnlyDictionary<int, int> cycleNumbersById,
        IReadOnlyDictionary<int, int> tradingDayNumbersById,
        int currentTradingDayNumber)
    {
        var openedNumber = tradingDayNumbersById.GetValueOrDefault(loan.OpenedInTradingDayId);
        var dueNumber = openedNumber + loan.TermTradingDays;
        var interestPerTradingDay = loan.TermTradingDays > 0
            ? Math.Round(loan.Principal * loan.InterestRate / loan.TermTradingDays, 2, MidpointRounding.AwayFromZero)
            : 0m;

        return new LoanResponse(
            loan.Id,
            loan.BankId,
            bankNames.GetValueOrDefault(loan.BankId, $"#{loan.BankId}"),
            loan.ParticipantId,
            participantNames.GetValueOrDefault(loan.ParticipantId, $"#{loan.ParticipantId}"),
            loan.Principal,
            loan.RemainingPrincipal,
            loan.InterestRate,
            interestPerTradingDay,
            loan.ScheduledInstallment,
            loan.PastDuePrincipal,
            loan.PastDueInterest,
            loan.AccruedFees,
            loan.TotalLiability,
            loan.TermTradingDays,
            openedNumber,
            dueNumber,
            Math.Max(0, dueNumber - currentTradingDayNumber),
            loan.Status.ToString(),
            loan.ClosedInCycleId is int closedCycleId ? cycleNumbersById.GetValueOrDefault(closedCycleId) : null,
            loan.Status == LoanStatus.Closed,
            loan.CloseReason?.ToString());
    }

    // Loan borrowers are live participants first, with a MarketExit fallback so a departed borrower's closed
    // loans still carry a name.
    private static async Task<Dictionary<int, string>> LoanParticipantNamesAsync(AppDbContext dbContext, IReadOnlyList<int> participantIds)
    {
        var names = await ParticipantNamesAsync(dbContext, participantIds);
        var missing = participantIds.Where(id => !names.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var exitNames = await dbContext.MarketExits
                .Where(exit => missing.Contains(exit.ParticipantId))
                .Select(exit => new { exit.ParticipantId, exit.Name })
                .ToListAsync();
            foreach (var exit in exitNames)
            {
                names.TryAdd(exit.ParticipantId, exit.Name);
            }
        }

        return names;
    }

    private static async Task<Dictionary<int, string>> IndustryNameByIdAsync(AppDbContext dbContext) =>
        await dbContext.Industries.ToDictionaryAsync(industry => industry.Id, industry => industry.Name);

    private static async Task<List<CompanyResponse>> BuildCompanyResponsesAsync(
        AppDbContext dbContext,
        VolatilityHaltOptions haltOptions)
    {
        // Delisted companies drop off the live roster and the dashboard map; they surface on the closed-companies
        // page and still resolve on their own detail route.
        var companies = await dbContext.Companies
            .Where(company => company.ClosedInCycleId == null)
            .OrderBy(company => company.Id)
            .ToListAsync();
        var latestPriceByCompany = await LatestPriceByCompanyAsync(dbContext);
        var changeByCompany = await PriceChangePctByCompanyAsync(dbContext);
        var industryNameById = await IndustryNameByIdAsync(dbContext);
        var latestRatingByCompany = await LatestRatingByCompanyAsync(dbContext);
        var priceBandByCompany = await dbContext.PriceBandStates.ToDictionaryAsync(state => state.CompanyId);
        var playerId = await dbContext.Participants
            .Where(participant => participant.Type == ParticipantType.Player)
            .Select(participant => (int?)participant.Id)
            .FirstOrDefaultAsync();
        var fundId = playerId is int ownerId
            ? await dbContext.CollectiveFunds
                .Where(fund => fund.IsPlayerManaged
                    && fund.FoundedByParticipantId == ownerId
                    && fund.Status != CollectiveFundStatus.Closed)
                .Select(fund => (int?)fund.ParticipantId)
                .FirstOrDefaultAsync()
            : null;
        var positionParticipantIds = new[] { playerId, fundId }
            .Where(participantId => participantId.HasValue)
            .Select(participantId => participantId!.Value)
            .Distinct()
            .ToArray();
        var positionRows = positionParticipantIds.Length == 0
            ? []
            : await dbContext.Holdings
                .Where(holding => positionParticipantIds.Contains(holding.ParticipantId) && holding.Quantity > 0)
                .Select(holding => new { holding.ParticipantId, holding.CompanyId, Shares = holding.Quantity })
                .ToListAsync();
        var positionShares = positionRows.ToDictionary(
            holding => (holding.ParticipantId, holding.CompanyId),
            holding => holding.Shares);

        return companies
            .Select(company =>
            {
                priceBandByCompany.TryGetValue(company.Id, out var band);
                var currentPrice = latestPriceByCompany.GetValueOrDefault(company.Id);
                var bounds = ResolveOrderPriceBounds(band, currentPrice, haltOptions);
                return new CompanyResponse(
                    company.Id,
                    company.Name,
                    company.IsFavorite,
                    company.IndustryId,
                    industryNameById.GetValueOrDefault(company.IndustryId),
                    company.IssuedSharesCount,
                    currentPrice,
                    changeByCompany.GetValueOrDefault(company.Id),
                    latestRatingByCompany.TryGetValue(company.Id, out var rating) ? rating.ToString() : null,
                    band is not null && band.State != LuldState.Normal,
                    band?.State.ToString() ?? LuldState.Normal.ToString(),
                    band?.LimitDirection?.ToString(),
                    band?.ReferencePrice,
                    band?.LowerBandPrice,
                    band?.UpperBandPrice,
                    bounds?.AllowedMinimumPrice,
                    bounds?.AllowedMaximumPrice,
                    band?.LimitStateStartedCycleNumber,
                    band?.PauseUntilCycleNumber,
                    BuildCompanyPositionResponse(playerId, company, currentPrice, positionShares),
                    BuildCompanyPositionResponse(fundId, company, currentPrice, positionShares));
            })
            .ToList();
    }

    private static CompanyPositionResponse? BuildCompanyPositionResponse(
        int? participantId,
        Company company,
        decimal currentPrice,
        IReadOnlyDictionary<(int ParticipantId, int CompanyId), int> positionShares)
    {
        if (participantId is not int ownerId)
        {
            return null;
        }

        var shares = positionShares.GetValueOrDefault((ownerId, company.Id));
        var ownershipPct = company.IssuedSharesCount > 0
            ? (decimal)shares / company.IssuedSharesCount
            : 0m;
        return new CompanyPositionResponse(shares, ownershipPct, shares * currentPrice);
    }

    // Allowed-range prices are derived: reuse the persisted band as the reference when present, otherwise fall back
    // to the latest price so a just-listed company still exposes a bound-aware range.
    private static OrderPriceBounds? ResolveOrderPriceBounds(
        PriceBandState? band,
        decimal latestPrice,
        VolatilityHaltOptions haltOptions) =>
        OrderPriceBounds.Resolve(
            band,
            latestPrice,
            haltOptions.LowerBandPercent,
            haltOptions.UpperBandPercent,
            haltOptions.AllowedOrderLowerPercent,
            haltOptions.AllowedOrderUpperPercent);

    private static async Task<IResult> SetFavoriteCompanyAsync(
        AppDbContext dbContext,
        int companyId,
        bool isFavorite)
    {
        if (!await dbContext.Participants.AnyAsync(participant => participant.Type == ParticipantType.Player))
        {
            return Results.NotFound(new { error = "No player exists." });
        }

        var company = await dbContext.Companies.FirstOrDefaultAsync(candidate => candidate.Id == companyId);
        if (company is null)
        {
            return Results.NotFound(new { error = "Company not found." });
        }

        company.IsFavorite = isFavorite;
        await dbContext.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> SetFavoriteTraderAsync(
        AppDbContext dbContext,
        int participantId,
        bool isFavorite)
    {
        if (!await dbContext.Participants.AnyAsync(participant => participant.Type == ParticipantType.Player))
        {
            return Results.NotFound(new { error = "No player exists." });
        }

        var participant = await dbContext.Participants.FirstOrDefaultAsync(candidate => candidate.Id == participantId);
        if (participant is null)
        {
            return Results.NotFound(new { error = "Trader not found." });
        }

        participant.IsFavorite = isFavorite;
        await dbContext.SaveChangesAsync();
        return Results.NoContent();
    }

    // Resolves the AI-automation view for a participant. Provider label comes from the backend catalog and the
    // live status from in-memory runtime state; the stored key is never read here, only its presence.
    private static (string? ProviderId, string? ProviderLabel, string? Model, string? Status, string? Message, long? CallId, int? MaxDecisions)
        ResolveAiFields(AiTraderConfiguration? configuration, AiProviderCatalog catalog, AiTraderRuntimeState runtimeState, int participantId)
    {
        if (configuration is null)
        {
            return (null, null, null, null, null, null, null);
        }

        var label = catalog.Find(configuration.ProviderId)?.Label ?? configuration.ProviderId;
        var runtime = runtimeState.Get(participantId);
        return (configuration.ProviderId, label, configuration.Model, runtime.Status.ToString(), runtime.Message, runtime.CurrentCallId, configuration.MaxDecisionsPerDay);
    }

    private static async Task<List<ParticipantResponse>> BuildParticipantResponsesAsync(
        AppDbContext dbContext,
        MarginService marginService,
        AiProviderCatalog catalog,
        AiTraderRuntimeState runtimeState,
        bool includeFundMembers = false)
    {
        var participants = await dbContext.Participants.OrderBy(participant => participant.Id).ToListAsync();

        // Batch-load AI configurations once rather than one query per participant.
        var configurationsByParticipant = await dbContext.AiTraderConfigurations
            .ToDictionaryAsync(configuration => configuration.ParticipantId);

        // Resolve each membership to the fund's own participant row so a member can be labeled with its fund.
        var participantNameById = participants.ToDictionary(participant => participant.Id, participant => participant.Name);
        var fundByMemberId = (await dbContext.CollectiveFundParticipants
                .Join(
                    dbContext.CollectiveFunds,
                    member => member.CollectiveFundId,
                    fund => fund.Id,
                    (member, fund) => new { member.ParticipantId, FundParticipantId = fund.ParticipantId })
                .ToListAsync())
            .ToDictionary(
                membership => membership.ParticipantId,
                membership => (
                    FundParticipantId: membership.FundParticipantId,
                    FundName: participantNameById.GetValueOrDefault(membership.FundParticipantId)));

        var eligibleParticipants = participants
            // A trader that pooled into a fund hands its trading to that fund. The paged roster keeps it, labeled
            // with its fund, while the dashboard's unpaged read drops it so pooled cash is not double-counted.
            .Where(participant => includeFundMembers || !fundByMemberId.ContainsKey(participant.Id))
            // Closed fund participants remain as inactive history rows and belong only on the Closed Funds page.
            .Where(participant => participant.Type != ParticipantType.CollectiveFund || participant.IsActive)
            .ToList();
        var latestPriceByCompany = await LatestPriceByCompanyAsync(dbContext);
        var valuationByParticipant = await BuildParticipantValuationsAsync(
            dbContext, marginService, eligibleParticipants, latestPriceByCompany);

        return eligibleParticipants.Select(participant =>
            {
                var valuation = valuationByParticipant[participant.Id];
                var ai = ResolveAiFields(
                    configurationsByParticipant.GetValueOrDefault(participant.Id), catalog, runtimeState, participant.Id);
                var hasFund = fundByMemberId.TryGetValue(participant.Id, out var membership);
                int? memberOfFundId = hasFund ? membership.FundParticipantId : null;
                string? memberOfFundName = hasFund ? membership.FundName : null;
                return new ParticipantResponse(
                    participant.Id,
                    participant.Name,
                    participant.Type.ToString(),
                    participant.Temperament.ToString(),
                    participant.RiskProfile.ToString(),
                    participant.CurrentBalance,
                    participant.SettledCashBalance,
                    participant.CurrentBalance - participant.SettledCashBalance,
                    participant.ReservedBalance,
                    participant.AvailableBalance,
                    valuation.SharesOwned,
                    valuation.CompaniesOwned,
                    valuation.HoldingsValue,
                    valuation.LoanLiability,
                    valuation.Margin.TotalLiability,
                    valuation.TotalWorth,
                    valuation.PendingSettlementCount,
                    valuation.NextSettlementDueDayNumber,
                    participant.IsActive,
                    participant.IsBankrupt,
                    participant.IsFavorite,
                    ai.ProviderId,
                    ai.ProviderLabel,
                    ai.Model,
                    ai.Status,
                    ai.Message,
                    ai.CallId,
                    ai.MaxDecisions,
                    memberOfFundId,
                    memberOfFundName);
            })
            .ToList();
    }

    // The current risk rating per company is its most recent verdict, found by max rating Id so the whole
    // rating history never has to be loaded.
    private static async Task<Dictionary<int, CompanyRiskRating>> LatestRatingByCompanyAsync(AppDbContext dbContext)
    {
        var latestRatingIds = await dbContext.CompanyRatings
            .GroupBy(rating => rating.CompanyId)
            .Select(group => group.Max(rating => rating.Id))
            .ToListAsync();

        return (await dbContext.CompanyRatings
                .Where(rating => latestRatingIds.Contains(rating.Id))
                .Select(rating => new { rating.CompanyId, rating.Rating })
                .ToListAsync())
            .ToDictionary(row => row.CompanyId, row => row.Rating);
    }

    private static async Task<Dictionary<int, int>> LastSentimentChangeByIndustryAsync(AppDbContext dbContext)
    {
        var snapshots = await dbContext.SectorSentimentSnapshots
            .Select(snapshot => new { snapshot.Id, snapshot.IndustryId, snapshot.SentimentValue })
            .ToListAsync();

        return snapshots
            .GroupBy(snapshot => snapshot.IndustryId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var recent = group.OrderByDescending(snapshot => snapshot.Id).Take(2).ToArray();
                    return recent.Length == 2 ? recent[0].SentimentValue - recent[1].SentimentValue : 0;
                });
    }

    private static async Task<IndustrySentimentHistoryRow[]> IndustrySentimentHistoryRowsAsync(
        AppDbContext dbContext,
        int? industryId = null)
    {
        var query =
            from snapshot in dbContext.SectorSentimentSnapshots
            join cycle in dbContext.MarketCycles on snapshot.CreatedInCycleId equals cycle.Id
            where industryId == null || snapshot.IndustryId == industryId
            orderby cycle.CycleNumber, snapshot.Id
            select new IndustrySentimentHistoryRow(
                snapshot.IndustryId,
                snapshot.CreatedInCycleId,
                cycle.CycleNumber,
                snapshot.SentimentValue,
                snapshot.CreatedAt);

        return await query.ToArrayAsync();
    }

    private static IndustrySentimentPointResponse ToIndustrySentimentPointResponse(IndustrySentimentHistoryRow row) =>
        new(row.CreatedInCycleId, row.CycleNumber, row.SentimentValue, row.CreatedAt);

    private static NewsPostResponse ToNewsResponse(
        NewsPost post,
        IReadOnlyDictionary<int, string> companyNameById,
        IReadOnlyDictionary<int, string> industryNameById,
        IReadOnlyDictionary<int, int> cycleNumbersById) =>
        new(
            post.Id,
            post.Title,
            post.Content,
            post.PublishedInCycleId,
            cycleNumbersById.GetValueOrDefault(post.PublishedInCycleId),
            post.PublishedAt,
            post.Scope.ToString(),
            post.Category.ToString(),
            post.Direction?.ToString(),
            post.ImpactPercent,
            post.TargetCompanyId,
            post.TargetCompanyId is int companyId ? companyNameById.GetValueOrDefault(companyId) : null,
            post.Industries
                .Select(link => industryNameById.GetValueOrDefault(link.IndustryId) ?? $"#{link.IndustryId}")
                .ToArray(),
            post.PortfolioAuditSummary?.Id);

    private static CrisisResponse ToCrisisResponse(
        Crisis crisis,
        IReadOnlyDictionary<int, string> industryNameById,
        IReadOnlyDictionary<int, int> eventCountByCrisis) =>
        new(
            crisis.Id,
            crisis.Title,
            crisis.Content,
            crisis.Scope.ToString(),
            crisis.TriggeredInCycleId,
            crisis.TriggeredInCycleNumber,
            crisis.DurationCycles,
            crisis.TriggeredAt,
            eventCountByCrisis.GetValueOrDefault(crisis.Id),
            ToCrisisIndustryResponses(crisis, industryNameById));

    private static CrisisIndustryResponse[] ToCrisisIndustryResponses(
        Crisis crisis,
        IReadOnlyDictionary<int, string> industryNameById) =>
        crisis.Industries
            .Select(link => new CrisisIndustryResponse(
                link.IndustryId,
                industryNameById.GetValueOrDefault(link.IndustryId) ?? $"#{link.IndustryId}",
                link.ImpactPercent))
            .ToArray();

    private static CrisisDetailResponse ToCrisisDetailResponse(
        Crisis crisis,
        IReadOnlyDictionary<int, string> industryNameById,
        IReadOnlyDictionary<int, string> companyNameById) =>
        new(
            crisis.Id,
            crisis.Title,
            crisis.Content,
            crisis.Scope.ToString(),
            crisis.TriggeredInCycleId,
            crisis.TriggeredInCycleNumber,
            crisis.DurationCycles,
            crisis.TriggeredAt,
            ToCrisisIndustryResponses(crisis, industryNameById),
            crisis.Events
                .OrderBy(crisisEvent => crisisEvent.CreatedInCycleNumber)
                .ThenBy(crisisEvent => crisisEvent.Id)
                .Select(crisisEvent => new CrisisEventResponse(
                    crisisEvent.Id,
                    crisisEvent.Type.ToString(),
                    crisisEvent.Description,
                    crisisEvent.CompanyId,
                    crisisEvent.CompanyId is int companyId
                        ? companyNameById.GetValueOrDefault(companyId)
                        : null,
                    crisisEvent.IndustryId,
                    crisisEvent.IndustryId is int industryId
                        ? industryNameById.GetValueOrDefault(industryId)
                        : null,
                    crisisEvent.ImpactPercent,
                    crisisEvent.CreatedInCycleNumber,
                    crisisEvent.CreatedAt))
                .ToArray());

    private static ScienceInvestigationResponse ToScienceInvestigationResponse(
        ScienceInvestigation investigation,
        IReadOnlyDictionary<int, string> industryNameById,
        IReadOnlyDictionary<int, int> cycleNumberById) =>
        new(
            investigation.Id,
            investigation.Title,
            investigation.Content,
            investigation.TriggeredInCycleId,
            cycleNumberById.GetValueOrDefault(investigation.TriggeredInCycleId),
            investigation.TriggeredAt,
            investigation.Industries
                .Select(link => new ScienceInvestigationIndustryResponse(
                    link.IndustryId,
                    industryNameById.GetValueOrDefault(link.IndustryId) ?? $"#{link.IndustryId}",
                    link.ImpactPercent))
                .ToArray());

    private static BankruptcyResponse ToBankruptcyResponse(
        Bankruptcy bankruptcy,
        IReadOnlyDictionary<int, string> participantNameById,
        IReadOnlyDictionary<int, int> cycleNumberById) =>
        new(
            bankruptcy.Id,
            bankruptcy.ParticipantId,
            participantNameById.GetValueOrDefault(bankruptcy.ParticipantId) ?? $"#{bankruptcy.ParticipantId}",
            bankruptcy.Title,
            bankruptcy.Content,
            bankruptcy.CashLost,
            bankruptcy.ShareWorth,
            bankruptcy.TriggeredInCycleId,
            cycleNumberById.GetValueOrDefault(bankruptcy.TriggeredInCycleId),
            bankruptcy.TriggeredAt);

    private static MarketExitResponse ToMarketExitResponse(
        MarketExit marketExit,
        IReadOnlyDictionary<int, int> cycleNumberById) =>
        new(
            marketExit.Id,
            marketExit.ParticipantId,
            marketExit.Name,
            marketExit.Reason,
            cycleNumberById.GetValueOrDefault(marketExit.JoinedInCycleId),
            cycleNumberById.GetValueOrDefault(marketExit.LeftInCycleId),
            marketExit.OrdersPlaced,
            marketExit.InitialBalance,
            marketExit.MaxTotalWorth,
            marketExit.QuitBalance,
            marketExit.LeftAt);

    // The current company price is the most recent snapshot, found per company by its max Id so the
    // whole snapshot history never has to be loaded.
    private static async Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync(AppDbContext dbContext)
    {
        var latestSnapshotIds = await dbContext.PriceSnapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .Select(group => group.Max(snapshot => snapshot.Id))
            .ToListAsync();

        return (await dbContext.PriceSnapshots
                .Where(snapshot => latestSnapshotIds.Contains(snapshot.Id))
                .Select(snapshot => new { snapshot.CompanyId, snapshot.Price })
                .ToListAsync())
            .ToDictionary(row => row.CompanyId, row => row.Price);
    }

    private static async Task<IndustryDetailResponse?> BuildIndustryDetailAsync(AppDbContext dbContext, int industryId)
    {
        var industry = await dbContext.Industries.FirstOrDefaultAsync(candidate => candidate.Id == industryId);
        if (industry is null)
        {
            return null;
        }

        var companies = await dbContext.Companies
            .Where(company => company.IndustryId == industryId && company.ClosedInCycleId == null)
            .Select(company => new { company.Id, company.IssuedSharesCount })
            .ToListAsync();
        var latestPriceByCompany = await LatestPriceByCompanyAsync(dbContext);
        var lastCycleChange = (await LastSentimentChangeByIndustryAsync(dbContext)).GetValueOrDefault(industryId);

        return new IndustryDetailResponse(
            industry.Id,
            industry.Name,
            industry.SentimentValue,
            industry.SentimentVolatility,
            industry.SectorBeta,
            companies.Sum(company => company.IssuedSharesCount * latestPriceByCompany.GetValueOrDefault(company.Id)),
            lastCycleChange,
            companies.Count);
    }

    // Change since the prior cycle's close, matching the signal the decision engine reads: the newest
    // price versus the newest from an earlier cycle, or zero when there is no earlier point.
    private static async Task<Dictionary<int, decimal>> PriceChangePctByCompanyAsync(AppDbContext dbContext)
    {
        var snapshots = await dbContext.PriceSnapshots
            .Select(snapshot => new { snapshot.CompanyId, snapshot.Id, snapshot.Price, snapshot.CreatedInCycleId })
            .ToListAsync();

        return snapshots
            .GroupBy(snapshot => snapshot.CompanyId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var ordered = group.OrderByDescending(snapshot => snapshot.Id).ToList();
                    var latest = ordered[0];
                    var prior = ordered.FirstOrDefault(snapshot => snapshot.CreatedInCycleId != latest.CreatedInCycleId);
                    return prior is { Price: > 0m } ? (latest.Price - prior.Price) / prior.Price : 0m;
                });
    }

    // Total paid to all shareholders in the most recent dividend cycle; zero before any dividend.
    private static async Task<decimal> LastDividendTotalAsync(AppDbContext dbContext)
    {
        var lastDividendCycleId = await dbContext.MoneyTransactions
            .Where(transaction => transaction.Type == MoneyTransactionType.Dividend)
            .OrderByDescending(transaction => transaction.CreatedInCycleId)
            .Select(transaction => (int?)transaction.CreatedInCycleId)
            .FirstOrDefaultAsync();

        if (lastDividendCycleId is null)
        {
            return 0m;
        }

        return await dbContext.MoneyTransactions
            .Where(transaction => transaction.Type == MoneyTransactionType.Dividend
                && transaction.CreatedInCycleId == lastDividendCycleId)
            .SumAsync(transaction => transaction.Amount);
    }

    private static Task<Dictionary<int, int>> CycleNumbersByIdAsync(AppDbContext dbContext) =>
        dbContext.MarketCycles.ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

    // The number of the market's active cycle, or zero when no market or cycle is set.
    private static async Task<int> CurrentCycleNumberAsync(AppDbContext dbContext)
    {
        var market = await dbContext.Markets.FirstOrDefaultAsync();
        if (market?.CurrentCycleId is not int cycleId)
        {
            return 0;
        }

        return await dbContext.MarketCycles
            .Where(cycle => cycle.Id == cycleId)
            .Select(cycle => cycle.CycleNumber)
            .FirstOrDefaultAsync();
    }

    // The number of the market's active trading day, or zero when no market or day is set.
    private static async Task<int> CurrentTradingDayNumberAsync(AppDbContext dbContext) =>
        await (
                from market in dbContext.Markets
                join day in dbContext.TradingDays on market.CurrentTradingDayId equals day.Id
                select (int?)day.DayNumber)
            .FirstOrDefaultAsync() ?? 0;

    private static async Task<CompanyDetailResponse?> BuildCompanyDetailAsync(
        AppDbContext dbContext,
        int companyId,
        VolatilityHaltOptions haltOptions)
    {
        var company = await dbContext.Companies.FirstOrDefaultAsync(candidate => candidate.Id == companyId);
        if (company is null)
        {
            return null;
        }

        // Outstanding shares are the sum of participants' holdings; the rest of the issued supply is the
        // unsold float still held by the issuer.
        var sharesOutstanding = await dbContext.Holdings
            .Where(holding => holding.CompanyId == companyId && holding.Quantity > 0)
            .SumAsync(holding => holding.Quantity);
        var sharesHeldByIssuer = company.IssuedSharesCount - sharesOutstanding;
        var shareholderCount = await dbContext.Holdings
            .CountAsync(holding => holding.CompanyId == companyId && holding.Quantity > 0);

        var currentPrice = (await LatestPriceByCompanyAsync(dbContext)).GetValueOrDefault(companyId);
        var priceChangePct = (await PriceChangePctByCompanyAsync(dbContext)).GetValueOrDefault(companyId);
        var industryName = await dbContext.Industries
            .Where(industry => industry.Id == company.IndustryId)
            .Select(industry => industry.Name)
            .FirstOrDefaultAsync();

        // The two most recent verdicts give the current risk rating and the direction of its change.
        var recentRatings = await dbContext.CompanyRatings
            .Where(rating => rating.CompanyId == companyId)
            .OrderByDescending(rating => rating.Id)
            .Take(2)
            .Select(rating => rating.Rating)
            .ToListAsync();

        int? closedInCycleNumber = company.ClosedInCycleId is int closedCycleId
            ? await dbContext.MarketCycles
                .Where(cycle => cycle.Id == closedCycleId)
                .Select(cycle => (int?)cycle.CycleNumber)
                .FirstOrDefaultAsync()
            : null;

        var currentCycleNumber = await CurrentCycleNumberAsync(dbContext);
        var priceBand = await dbContext.PriceBandStates.FirstOrDefaultAsync(state => state.CompanyId == companyId);
        var isHalted = priceBand?.State is not null and not LuldState.Normal;
        var orderBounds = ResolveOrderPriceBounds(priceBand, currentPrice, haltOptions);
        var remainingPauseCycles = priceBand?.PauseUntilCycleNumber is int pauseUntil
            ? Math.Max(0, pauseUntil - currentCycleNumber)
            : 0;
        var latestFinancial = await LatestCompanyFinancialSnapshotAsync(dbContext, companyId);

        return new CompanyDetailResponse(
            company.Id,
            company.Name,
            company.IsFavorite,
            company.IndustryId,
            industryName,
            company.IssuedSharesCount,
            currentPrice == 0m ? null : currentPrice,
            priceChangePct,
            currentPrice * company.IssuedSharesCount,
            company.CashBalance,
            sharesHeldByIssuer,
            sharesOutstanding,
            shareholderCount,
            company.CreatedAt,
            recentRatings.Count > 0 ? recentRatings[0].ToString() : null,
            recentRatings.Count > 1 ? recentRatings[1].ToString() : null,
            company.ClosedInCycleId != null,
            closedInCycleNumber,
            isHalted,
            priceBand?.PauseUntilCycleNumber,
            priceBand?.State.ToString() ?? LuldState.Normal.ToString(),
            priceBand?.LimitDirection?.ToString(),
            priceBand?.ReferencePrice,
            priceBand?.LowerBandPrice,
            priceBand?.UpperBandPrice,
            orderBounds?.AllowedMinimumPrice,
            orderBounds?.AllowedMaximumPrice,
            priceBand?.LimitStateStartedCycleNumber,
            priceBand?.PauseUntilCycleNumber,
            remainingPauseCycles,
            remainingPauseCycles * 2,
            latestFinancial is null ? null : ToCompanyFinancialSummaryResponse(latestFinancial));
    }

    private static Task<CompanyFinancialSnapshot?> LatestCompanyFinancialSnapshotAsync(
        AppDbContext dbContext,
        int companyId) =>
        dbContext.CompanyFinancialSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.CompanyId == companyId)
            .Include(snapshot => snapshot.LatestDividendEvent)
            .OrderByDescending(snapshot => snapshot.TradingDayNumber)
            .ThenByDescending(snapshot => snapshot.Moment)
            .ThenByDescending(snapshot => snapshot.CreatedInCycleId)
            .ThenByDescending(snapshot => snapshot.CreatedAt)
            .ThenByDescending(snapshot => snapshot.Id)
            .FirstOrDefaultAsync();

    private static CompanyFinancialSummaryResponse ToCompanyFinancialSummaryResponse(
        CompanyFinancialSnapshot snapshot) =>
        new(
            snapshot.Id,
            snapshot.CreatedInCycleId,
            snapshot.TradingDayNumber,
            snapshot.Moment.ToString(),
            snapshot.CreatedAt,
            snapshot.Revenue,
            snapshot.NetProfit,
            snapshot.OperatingCashFlow,
            snapshot.TotalAssets,
            snapshot.TotalLiabilities,
            snapshot.TotalDebt,
            snapshot.ExpectedDividendPerShare,
            snapshot.ExpectedDividendPool,
            snapshot.DividendCoverageRatio,
            snapshot.LatestDividendEvent is null
                ? null
                : ToCompanyDividendEventResponse(snapshot.LatestDividendEvent),
            snapshot.BusinessRiskScore,
            snapshot.ManagementRevenueForecast,
            snapshot.ManagementProfitForecast,
            snapshot.ManagementOperatingCashFlowForecast,
            snapshot.ManagementOutlook.ToString(),
            snapshot.ManagementConfidenceScore,
            snapshot.ProfitabilityScore,
            snapshot.ProfitabilityLevel.ToString(),
            snapshot.StabilityScore,
            snapshot.FinancialVolatilityLevel.ToString(),
            snapshot.ClosureRiskScore,
            snapshot.ClosureRiskLevel.ToString(),
            snapshot.ChangedMetrics.ToString());

    private static CompanyDividendEventResponse ToCompanyDividendEventResponse(
        CompanyDividendEvent dividend) =>
        new(
            dividend.Id,
            dividend.DeclaredAmount,
            dividend.FundedAmount,
            dividend.FundingOutcome.ToString(),
            dividend.IssuerCashBeforeFunding,
            dividend.CreatedInCycleId,
            dividend.TradingDayNumber,
            dividend.CreatedAt);

    private static CompanyFinancialValuesResponse ToCompanyFinancialValuesResponse(
        CompanyFinancialSnapshot snapshot) =>
        new(
            snapshot.Revenue,
            snapshot.NetProfit,
            snapshot.OperatingCashFlow,
            snapshot.TotalAssets,
            snapshot.TotalLiabilities,
            snapshot.TotalDebt,
            snapshot.ExpectedDividendPerShare,
            snapshot.ExpectedDividendPool,
            snapshot.DividendCoverageRatio,
            snapshot.BusinessRiskScore,
            snapshot.ManagementRevenueForecast,
            snapshot.ManagementProfitForecast,
            snapshot.ManagementOperatingCashFlowForecast,
            snapshot.ManagementConfidenceScore,
            snapshot.ProfitabilityScore,
            snapshot.StabilityScore,
            snapshot.ClosureRiskScore);

    private static CompanyFinancialValuesResponse ToAbsoluteFinancialDeltaResponse(
        CompanyFinancialSnapshot current,
        CompanyFinancialSnapshot previous) =>
        new(
            current.Revenue - previous.Revenue,
            current.NetProfit - previous.NetProfit,
            current.OperatingCashFlow - previous.OperatingCashFlow,
            current.TotalAssets - previous.TotalAssets,
            current.TotalLiabilities - previous.TotalLiabilities,
            current.TotalDebt - previous.TotalDebt,
            current.ExpectedDividendPerShare - previous.ExpectedDividendPerShare,
            current.ExpectedDividendPool - previous.ExpectedDividendPool,
            current.DividendCoverageRatio - previous.DividendCoverageRatio,
            current.BusinessRiskScore - previous.BusinessRiskScore,
            current.ManagementRevenueForecast - previous.ManagementRevenueForecast,
            current.ManagementProfitForecast - previous.ManagementProfitForecast,
            current.ManagementOperatingCashFlowForecast - previous.ManagementOperatingCashFlowForecast,
            current.ManagementConfidenceScore - previous.ManagementConfidenceScore,
            current.ProfitabilityScore - previous.ProfitabilityScore,
            current.StabilityScore - previous.StabilityScore,
            current.ClosureRiskScore - previous.ClosureRiskScore);

    private static CompanyFinancialValuesResponse ToPercentageFinancialDeltaResponse(
        CompanyFinancialSnapshot current,
        CompanyFinancialSnapshot previous) =>
        new(
            PercentageDelta(current.Revenue, previous.Revenue),
            PercentageDelta(current.NetProfit, previous.NetProfit),
            PercentageDelta(current.OperatingCashFlow, previous.OperatingCashFlow),
            PercentageDelta(current.TotalAssets, previous.TotalAssets),
            PercentageDelta(current.TotalLiabilities, previous.TotalLiabilities),
            PercentageDelta(current.TotalDebt, previous.TotalDebt),
            PercentageDelta(current.ExpectedDividendPerShare, previous.ExpectedDividendPerShare),
            PercentageDelta(current.ExpectedDividendPool, previous.ExpectedDividendPool),
            PercentageDelta(current.DividendCoverageRatio, previous.DividendCoverageRatio),
            PercentageDelta(current.BusinessRiskScore, previous.BusinessRiskScore),
            PercentageDelta(current.ManagementRevenueForecast, previous.ManagementRevenueForecast),
            PercentageDelta(current.ManagementProfitForecast, previous.ManagementProfitForecast),
            PercentageDelta(
                current.ManagementOperatingCashFlowForecast,
                previous.ManagementOperatingCashFlowForecast),
            PercentageDelta(current.ManagementConfidenceScore, previous.ManagementConfidenceScore),
            PercentageDelta(current.ProfitabilityScore, previous.ProfitabilityScore),
            PercentageDelta(current.StabilityScore, previous.StabilityScore),
            PercentageDelta(current.ClosureRiskScore, previous.ClosureRiskScore));

    private static decimal? PercentageDelta(decimal current, decimal previous) =>
        previous == 0m
            ? null
            : Math.Round(
                (current - previous) / Math.Abs(previous) * 100m,
                6,
                MidpointRounding.AwayFromZero);

    private static CompanyAuditSummaryResponse ToCompanyAuditSummaryResponse(
        CompanyRating rating,
        string companyName,
        string auditorName,
        int createdInCycleNumber)
    {
        var evidence = rating.Evidence;
        return new CompanyAuditSummaryResponse(
            rating.Id,
            rating.CompanyId,
            companyName,
            rating.Rating.ToString(),
            rating.ImpactPercent,
            rating.AuditorId,
            auditorName,
            rating.CreatedInCycleId,
            createdInCycleNumber,
            rating.CreatedAt,
            evidence is not null,
            evidence?.EvaluationStartTradingDayNumber,
            evidence?.EvaluationEndTradingDayNumber,
            evidence?.EffectiveTradingDayNumber,
            evidence?.TotalScore,
            evidence?.AdjustedReturnPercent,
            evidence?.MaximumAdjustedCycleMovePercent,
            evidence?.LatestDividendEvent?.FundingOutcome.ToString(),
            evidence?.DividendCoverageRatio,
            evidence?.IndustryTrend.ToString(),
            evidence?.ProfitabilityFactorScore,
            evidence?.StabilityFactorScore,
            evidence?.ClosureRiskFactorScore,
            evidence?.ManagementOutlookFactorScore,
            evidence?.CompanyFinancialSnapshot is null
                ? null
                : ToCompanyAuditFinancialFactorsResponse(evidence.CompanyFinancialSnapshot));
    }

    private static CompanyAuditFinancialFactorsResponse ToCompanyAuditFinancialFactorsResponse(
        CompanyFinancialSnapshot snapshot) =>
        new(
            snapshot.Id,
            snapshot.ProfitabilityScore,
            snapshot.ProfitabilityLevel.ToString(),
            snapshot.StabilityScore,
            snapshot.FinancialVolatilityLevel.ToString(),
            snapshot.ClosureRiskScore,
            snapshot.ClosureRiskLevel.ToString(),
            snapshot.ManagementOutlook.ToString(),
            snapshot.ManagementConfidenceScore);

    private static async Task<ParticipantDetailResponse?> BuildParticipantDetailAsync(
        AppDbContext dbContext,
        MarginService marginService,
        CollectiveFundOptions fundOptions,
        AiProviderCatalog catalog,
        AiTraderRuntimeState runtimeState,
        int participantId)
    {
        var participant = await dbContext.Participants.FirstOrDefaultAsync(candidate => candidate.Id == participantId);
        if (participant is null)
        {
            return null;
        }

        var aiConfiguration = await dbContext.AiTraderConfigurations
            .FirstOrDefaultAsync(configuration => configuration.ParticipantId == participantId);
        var ai = ResolveAiFields(aiConfiguration, catalog, runtimeState, participantId);

        var valuation = (await BuildParticipantValuationsAsync(
            dbContext,
            marginService,
            [participant],
            await LatestPriceByCompanyAsync(dbContext)))[participant.Id];

        string? fundStatus = null;
        CollectiveFundMemberResponse[] fundMembers = [];
        if (participant.Type == ParticipantType.CollectiveFund)
        {
            (fundStatus, fundMembers) = await BuildCollectiveFundMembersAsync(dbContext, fundOptions, participantId);
        }

        // For an ordinary trader, surface the fund it has joined (if any) so its page can link there.
        int? memberOfFundId = null;
        string? memberOfFundName = null;
        var membership = await dbContext.CollectiveFundParticipants
            .FirstOrDefaultAsync(member => member.ParticipantId == participantId);
        if (membership is not null)
        {
            var fund = await dbContext.CollectiveFunds.FirstOrDefaultAsync(candidate => candidate.Id == membership.CollectiveFundId);
            if (fund is not null)
            {
                memberOfFundId = fund.ParticipantId;
                memberOfFundName = await dbContext.Participants
                    .Where(candidate => candidate.Id == fund.ParticipantId)
                    .Select(candidate => candidate.Name)
                    .FirstOrDefaultAsync();
            }
        }

        return new ParticipantDetailResponse(
            participant.Id,
            participant.Name,
            participant.Type.ToString(),
            participant.Temperament.ToString(),
            participant.RiskProfile.ToString(),
            participant.InitialBalance,
            participant.CurrentBalance,
            participant.SettledCashBalance,
            participant.CurrentBalance - participant.SettledCashBalance,
            participant.ReservedBalance,
            participant.AvailableBalance,
            valuation.SharesOwned,
            valuation.HoldingsValue,
            valuation.CostBasis,
            valuation.LoanLiability,
            valuation.Margin,
            valuation.TotalWorth,
            valuation.PendingSettlementCount,
            valuation.NextSettlementDueDayNumber,
            participant.IsActive,
            participant.IsFavorite,
            fundStatus,
            fundMembers,
            memberOfFundId,
            memberOfFundName,
            ai.ProviderId,
            ai.ProviderLabel,
            ai.Model,
            ai.Status,
            ai.Message,
            ai.CallId,
            ai.MaxDecisions);
    }

    private static async Task<PlayerResponse?> BuildPlayerResponseAsync(AppDbContext dbContext, MarginService marginService)
    {
        var player = await dbContext.Participants
            .FirstOrDefaultAsync(participant => participant.Type == ParticipantType.Player);
        if (player is null)
        {
            return null;
        }

        var latestPriceByCompany = await LatestPriceByCompanyAsync(dbContext);
        var playerValuation = (await BuildParticipantValuationsAsync(
            dbContext, marginService, [player], latestPriceByCompany))[player.Id];

        // The two newest snapshots are cycles N and N−1; last-cycle deltas stay null until both exist.
        var recentSnapshots = await dbContext.ParticipantWorthSnapshots
            .Where(snapshot => snapshot.ParticipantId == player.Id)
            .OrderByDescending(snapshot => snapshot.Id)
            .Take(2)
            .ToListAsync();

        decimal? lastCycleMoneyChange = null;
        decimal? lastCycleWorthChange = null;
        if (recentSnapshots.Count == 2)
        {
            var latest = recentSnapshots[0];
            var prior = recentSnapshots[1];
            lastCycleMoneyChange = latest.Balance - prior.Balance;
            lastCycleWorthChange = latest.Balance + latest.HoldingsValue - latest.LoanLiability - latest.MarginLiability
                - (prior.Balance + prior.HoldingsValue - prior.LoanLiability - prior.MarginLiability);
        }

        // The player-managed fund (if any) rides along on the player response so the UI can trade through it and
        // manage its cash without a second round-trip.
        var managedFund = await dbContext.CollectiveFunds
            .FirstOrDefaultAsync(fund => fund.IsPlayerManaged
                && fund.FoundedByParticipantId == player.Id
                && fund.Status != CollectiveFundStatus.Closed);

        int? fundParticipantId = null;
        string? fundName = null;
        decimal? fundCurrentBalance = null;
        decimal? fundAvailableBalance = null;
        decimal? fundHoldingsValue = null;
        decimal? fundTotalWorth = null;
        decimal? fundWithdrawable = null;
        int? fundPopularityIndex = null;
        MarginAccountResponse? fundMargin = null;
        int? fundPendingSettlementCount = null;
        int? fundNextSettlementDueDayNumber = null;
        decimal? fundLastCycleMoneyChange = null;
        if (managedFund is not null)
        {
            fundPopularityIndex = managedFund.PopularityIndex;
            var fundParticipant = await dbContext.Participants
                .FirstOrDefaultAsync(participant => participant.Id == managedFund.ParticipantId);
            if (fundParticipant is not null)
            {
                var fundValuation = (await BuildParticipantValuationsAsync(
                    dbContext, marginService, [fundParticipant], latestPriceByCompany))[fundParticipant.Id];
                var memberDepositsOwed = await dbContext.CollectiveFundParticipants
                    .Where(member => member.CollectiveFundId == managedFund.Id)
                    .SumAsync(member => member.DepositAmount);

                fundParticipantId = fundParticipant.Id;
                fundName = fundParticipant.Name;
                fundCurrentBalance = fundParticipant.CurrentBalance;
                fundAvailableBalance = fundParticipant.AvailableBalance;
                fundHoldingsValue = fundValuation.HoldingsValue;
                fundMargin = fundValuation.Margin;
                fundTotalWorth = fundValuation.TotalWorth;
                fundWithdrawable = Math.Max(
                    0m,
                    Math.Min(fundParticipant.AvailableBalance, fundParticipant.SettledCashBalance) - memberDepositsOwed);
                fundPendingSettlementCount = fundValuation.PendingSettlementCount;
                fundNextSettlementDueDayNumber = fundValuation.NextSettlementDueDayNumber;

                // The fund's last-cycle cash delta mirrors the player computation above so the sidebar can show the
                // active actor's cash performance from this single response.
                var fundRecentSnapshots = await dbContext.ParticipantWorthSnapshots
                    .Where(snapshot => snapshot.ParticipantId == fundParticipant.Id)
                    .OrderByDescending(snapshot => snapshot.Id)
                    .Take(2)
                    .ToListAsync();
                if (fundRecentSnapshots.Count == 2)
                {
                    fundLastCycleMoneyChange = fundRecentSnapshots[0].Balance - fundRecentSnapshots[1].Balance;
                }
            }
        }

        return new PlayerResponse(
            player.Id,
            player.Name,
            player.InitialBalance,
            player.CurrentBalance,
            player.SettledCashBalance,
            player.CurrentBalance - player.SettledCashBalance,
            player.ReservedBalance,
            player.AvailableBalance,
            playerValuation.SharesOwned,
            playerValuation.HoldingsValue,
            playerValuation.LoanLiability,
            playerValuation.Margin,
            playerValuation.TotalWorth,
            playerValuation.PendingSettlementCount,
            playerValuation.NextSettlementDueDayNumber,
            player.CurrentBalance - player.InitialBalance,
            playerValuation.TotalWorth - player.InitialBalance,
            lastCycleMoneyChange,
            lastCycleWorthChange,
            player.IsActive,
            fundParticipantId,
            fundName,
            fundCurrentBalance,
            fundAvailableBalance,
            fundHoldingsValue,
            fundTotalWorth,
            fundWithdrawable,
            fundPopularityIndex,
            fundMargin,
            fundPendingSettlementCount,
            fundNextSettlementDueDayNumber,
            fundLastCycleMoneyChange);
    }

    private static async Task<Dictionary<int, ParticipantValuation>> BuildParticipantValuationsAsync(
        AppDbContext dbContext,
        MarginService marginService,
        IReadOnlyCollection<Participant> participants,
        IReadOnlyDictionary<int, decimal> prices)
    {
        var participantIds = participants.Select(participant => participant.Id).ToHashSet();
        var holdings = await dbContext.Holdings
            .Where(holding => participantIds.Contains(holding.ParticipantId) && holding.Quantity > 0)
            .Select(holding => new { holding.ParticipantId, holding.CompanyId, holding.Quantity, holding.AverageCost })
            .ToListAsync();
        var holdingsByParticipant = holdings
            .GroupBy(holding => holding.ParticipantId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var loanLiabilityByParticipant = (await dbContext.Loans
                .Where(loan => participantIds.Contains(loan.ParticipantId) && loan.Status == LoanStatus.Open)
                .Select(loan => new { loan.ParticipantId, Liability = loan.RemainingPrincipal + loan.PastDueInterest + loan.AccruedFees })
                .ToListAsync())
            .GroupBy(loan => loan.ParticipantId)
            .ToDictionary(group => group.Key, group => group.Sum(loan => loan.Liability));
        var pendingSettlements = await dbContext.SettlementInstructions
            .Where(instruction => instruction.Status == SettlementStatus.Pending
                && (participantIds.Contains(instruction.BuyerId)
                    || (instruction.SellerId != null && participantIds.Contains(instruction.SellerId.Value))))
            .Select(instruction => new { instruction.BuyerId, instruction.SellerId, instruction.DueDayNumber })
            .ToListAsync();
        var holdingsValueByParticipant = participants.ToDictionary(
            participant => participant.Id,
            participant => (holdingsByParticipant.GetValueOrDefault(participant.Id) ?? [])
                .Sum(holding => holding.Quantity * prices.GetValueOrDefault(holding.CompanyId)));
        var marginMetricsByParticipant = await marginService.GetReadOnlyMetricsByParticipantAsync(
            participants,
            holdingsValueByParticipant);

        var result = new Dictionary<int, ParticipantValuation>();
        foreach (var participant in participants)
        {
            var participantHoldings = holdingsByParticipant.GetValueOrDefault(participant.Id) ?? [];
            var sharesOwned = participantHoldings.Sum(holding => holding.Quantity);
            var companiesOwned = participantHoldings.Select(holding => holding.CompanyId).Distinct().Count();
            var holdingsValue = holdingsValueByParticipant[participant.Id];
            var costBasis = participantHoldings.Sum(holding => holding.Quantity * holding.AverageCost);
            var metrics = marginMetricsByParticipant[participant.Id];
            var margin = new MarginAccountResponse(
                metrics.DebitBalance,
                metrics.AccruedInterest,
                metrics.DebitBalance + metrics.AccruedInterest,
                metrics.AccountEquity,
                metrics.BuyingPower,
                metrics.InitialMarginRate,
                metrics.MaintenanceMarginRate,
                metrics.InitialRequirement,
                metrics.MaintenanceRequirement,
                metrics.MaintenanceExcess,
                metrics.Deficiency,
                metrics.CallStatus);
            var participantSettlements = pendingSettlements
                .Where(instruction => instruction.BuyerId == participant.Id || instruction.SellerId == participant.Id)
                .ToArray();
            var loanLiability = loanLiabilityByParticipant.GetValueOrDefault(participant.Id);
            result[participant.Id] = new ParticipantValuation(
                sharesOwned,
                companiesOwned,
                holdingsValue,
                costBasis,
                loanLiability,
                margin,
                participant.CurrentBalance + holdingsValue - loanLiability - margin.TotalLiability,
                participantSettlements.Length,
                participantSettlements.Length == 0 ? null : participantSettlements.Min(instruction => instruction.DueDayNumber));
        }

        return result;
    }

    private static async Task<(string? Status, CollectiveFundMemberResponse[] Members)> BuildCollectiveFundMembersAsync(
        AppDbContext dbContext,
        CollectiveFundOptions fundOptions,
        int fundParticipantId)
    {
        var fund = await dbContext.CollectiveFunds.FirstOrDefaultAsync(candidate => candidate.ParticipantId == fundParticipantId);
        if (fund is null)
        {
            return (null, []);
        }

        var memberships = await dbContext.CollectiveFundParticipants
            .Where(member => member.CollectiveFundId == fund.Id)
            .ToListAsync();
        var memberIds = memberships.Select(member => member.ParticipantId).ToList();
        var memberById = await dbContext.Participants
            .Where(candidate => memberIds.Contains(candidate.Id))
            .ToDictionaryAsync(candidate => candidate.Id);
        var cycleNumberById = await dbContext.MarketCycles
            .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);
        var joinedCycleIds = memberships.Select(member => member.JoinedInCycleId).Distinct().ToList();
        var joinedTradingDayByCycleId = await (
                from cycle in dbContext.MarketCycles
                join day in dbContext.TradingDays on cycle.TradingDayId equals day.Id
                where joinedCycleIds.Contains(cycle.Id)
                select new { cycle.Id, day.DayNumber })
            .ToDictionaryAsync(entry => entry.Id, entry => entry.DayNumber);
        var currentTradingDayNumber = await (
                from market in dbContext.Markets
                join day in dbContext.TradingDays on market.CurrentTradingDayId equals day.Id
                select (int?)day.DayNumber)
            .FirstOrDefaultAsync() ?? 0;

        // What the fund has paid each member as pass-through dividends, kept separate from their own holdings' dividends.
        var payoutByMember = (await dbContext.MoneyTransactions
                .Where(transaction => memberIds.Contains(transaction.ParticipantId)
                    && transaction.Type == MoneyTransactionType.CollectiveFundDividend)
                .GroupBy(transaction => transaction.ParticipantId)
                .Select(group => new { ParticipantId = group.Key, Total = group.Sum(transaction => transaction.Amount) })
                .ToListAsync())
            .ToDictionary(entry => entry.ParticipantId, entry => entry.Total);

        var members = memberships
            .OrderBy(member => member.JoinedAt)
            .Select(member =>
            {
                var memberParticipant = memberById.GetValueOrDefault(member.ParticipantId);
                return new CollectiveFundMemberResponse(
                    member.ParticipantId,
                    memberParticipant?.Name ?? $"#{member.ParticipantId}",
                    (memberParticipant?.Type ?? ParticipantType.Individual).ToString(),
                    cycleNumberById.GetValueOrDefault(member.JoinedInCycleId),
                    member.JoinedAt,
                    member.DepositAmount,
                    payoutByMember.GetValueOrDefault(member.ParticipantId),
                    member.IsLeaving,
                    currentTradingDayNumber
                        - joinedTradingDayByCycleId.GetValueOrDefault(member.JoinedInCycleId)
                        - Math.Max(0, fundOptions.MinimumMembershipTradingDays),
                    member.ParticipantId == fund.FoundedByParticipantId);
            })
            .ToArray();

        return (fund.Status.ToString(), members);
    }

    private static async Task<Dictionary<int, string>> ParticipantNamesAsync(
        AppDbContext dbContext, IEnumerable<int> participantIds)
    {
        var ids = participantIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        return await dbContext.Participants
            .Where(participant => ids.Contains(participant.Id))
            .ToDictionaryAsync(participant => participant.Id, participant => participant.Name);
    }

    // Resolves the investor and company names and cycle numbers a big-investment list needs. The investor name is
    // left null when the trader has since left the market, so callers fall back to its id; the company name uses an
    // id fallback because a closed company keeps its row.
    private static async Task<InvestmentResponse[]> ToInvestmentResponsesAsync(
        AppDbContext dbContext, IReadOnlyList<CompanyInvestment> investments)
    {
        if (investments.Count == 0)
        {
            return [];
        }

        var cycleNumbersById = await CycleNumbersByIdAsync(dbContext);
        var currentCycleNumber = await CurrentCycleNumberAsync(dbContext);
        var investorNameById = await ParticipantNamesAsync(dbContext, investments.Select(investment => investment.InvestorParticipantId));
        var companyIds = investments.Select(investment => investment.CompanyId).Distinct().ToList();
        var companyNameById = await dbContext.Companies
            .Where(company => companyIds.Contains(company.Id))
            .ToDictionaryAsync(company => company.Id, company => company.Name);

        return investments
            .Select(investment => new InvestmentResponse(
                investment.Id,
                investment.CompanyId,
                companyNameById.GetValueOrDefault(investment.CompanyId, $"#{investment.CompanyId}"),
                investment.InvestorParticipantId,
                investorNameById.GetValueOrDefault(investment.InvestorParticipantId),
                investment.DealValue,
                investment.SharesIssued,
                investment.SharesBeforeDeal,
                investment.TradingDayNumber,
                investment.CreatedInCycleId,
                cycleNumbersById.GetValueOrDefault(investment.CreatedInCycleId),
                Math.Max(0, currentCycleNumber - cycleNumbersById.GetValueOrDefault(investment.CreatedInCycleId)),
                investment.CapitalizationBeforeDeal,
                investment.FinalCapitalization,
                investment.InvestorSharePercent,
                investment.CreatedAt))
            .ToArray();
    }

    // The prevailing market price immediately before each trade — the latest price snapshot strictly older than
    // the trade's own snapshot — so a fill can be shown against the market it hit rather than against its own print
    // (a trade and the snapshot it stamps share the same price). Returns transaction id → that earlier price.
    private static async Task<Dictionary<int, decimal>> MarketPriceBeforeTradesAsync(
        AppDbContext dbContext, int companyId, IReadOnlyList<ShareTransaction> transactions)
    {
        var result = new Dictionary<int, decimal>();
        if (transactions.Count == 0)
        {
            return result;
        }

        var transactionIds = transactions.Select(transaction => transaction.Id).ToList();
        var tradeSnapshots = await dbContext.PriceSnapshots
            .Where(snapshot => snapshot.CompanyId == companyId
                && snapshot.SourceShareTransactionId != null
                && transactionIds.Contains(snapshot.SourceShareTransactionId.Value))
            .Select(snapshot => new { snapshot.Id, TransactionId = snapshot.SourceShareTransactionId!.Value })
            .ToListAsync();
        if (tradeSnapshots.Count == 0)
        {
            return result;
        }

        var minSnapshotId = tradeSnapshots.Min(snapshot => snapshot.Id);
        var maxSnapshotId = tradeSnapshots.Max(snapshot => snapshot.Id);

        // The snapshots in the id window spanning these trades, plus the one right before the oldest, are enough
        // to resolve every trade's preceding price by scanning in ascending id order.
        var window = (await dbContext.PriceSnapshots
                .Where(snapshot => snapshot.CompanyId == companyId
                    && snapshot.Id >= minSnapshotId && snapshot.Id <= maxSnapshotId)
                .OrderBy(snapshot => snapshot.Id)
                .Select(snapshot => new { snapshot.Id, snapshot.Price })
                .ToListAsync())
            .Select(snapshot => (snapshot.Id, snapshot.Price))
            .ToList();

        var beforeOldest = await dbContext.PriceSnapshots
            .Where(snapshot => snapshot.CompanyId == companyId && snapshot.Id < minSnapshotId)
            .OrderByDescending(snapshot => snapshot.Id)
            .Select(snapshot => new { snapshot.Id, snapshot.Price })
            .FirstOrDefaultAsync();
        if (beforeOldest is not null)
        {
            window.Insert(0, (beforeOldest.Id, beforeOldest.Price));
        }

        foreach (var trade in tradeSnapshots)
        {
            decimal? before = null;
            foreach (var (id, price) in window)
            {
                if (id >= trade.Id)
                {
                    break;
                }

                before = price;
            }

            if (before is decimal earlier)
            {
                result[trade.TransactionId] = earlier;
            }
        }

        return result;
    }

    // Names and the pre-trade market price are left null here and filled by the callers that hold a lookup,
    // so this mapper stays a single-argument method group usable by every other endpoint.
    private static ShareTransactionResponse ToShareTransactionResponse(ShareTransaction transaction) => new(
        transaction.Id,
        transaction.SellerId,
        null,
        transaction.BuyerId,
        null,
        transaction.CompanyId,
        transaction.Quantity,
        transaction.Price,
        null,
        transaction.TotalCost,
        transaction.CreatedInCycleId,
        transaction.CreatedAt,
        transaction.SettlementInstruction?.TradeDayNumber,
        transaction.SettlementInstruction?.DueDayNumber,
        transaction.SettlementInstruction?.Status.ToString(),
        transaction.SellerAverageCost,
        transaction.SellerCostBasis,
        transaction.SellerTradeFee,
        transaction.SellerManagerFee,
        transaction.SellerGrossRealizedPnl,
        transaction.SellerNetRealizedPnl);

    private static async Task<MarketResponse> BuildMarketResponseAsync(
        Market market,
        AppDbContext dbContext,
        TradingClockService tradingClockService)
    {
        var lastDividendTotal = await LastDividendTotalAsync(dbContext);
        var currentCycleNumber = market.CurrentCycleId is int cycleId
            ? await dbContext.MarketCycles
                .Where(cycle => cycle.Id == cycleId)
                .Select(cycle => (int?)cycle.CycleNumber)
                .FirstOrDefaultAsync()
            : null;
        var clock = await tradingClockService.GetStateAsync(market);
        var luldAffectedCount = await dbContext.PriceBandStates.CountAsync(state => state.State != LuldState.Normal);
        return ToMarketResponse(market, lastDividendTotal, currentCycleNumber, clock, luldAffectedCount);
    }

    private static MarketResponse ToMarketResponse(
        Market market,
        decimal lastDividendTotal,
        int? currentCycleNumber,
        TradingClockState? clock,
        int luldAffectedCount) =>
        new(
            market.Id,
            market.Name,
            market.Status.ToString(),
            market.CurrentCycleId,
            currentCycleNumber,
            lastDividendTotal,
            clock?.TradingDayNumber,
            clock?.TradingSessionState.ToString(),
            clock?.TradingCycleNumber,
            clock?.RemainingTradingCycles,
            clock?.RemainingPhaseSeconds,
            clock?.TradingCycleSeconds,
            luldAffectedCount);

    private static OrderResponse ToOrderResponse(Order order) => new(
        order.Id,
        order.ParticipantId,
        null,
        order.CompanyId,
        order.Type.ToString(),
        order.Status.ToString(),
        order.Quantity,
        order.FilledQuantity,
        order.LimitPrice,
        order.ReservedCashAmount,
        order.CreatedInCycleId);

    private static IEnumerable<T> OrderRows<T, TKey>(IEnumerable<T> source, Func<T, TKey> keySelector, bool descending) =>
        descending ? source.OrderByDescending(keySelector) : source.OrderBy(keySelector);

    private static (int PageIndex, int PageSize) ResolvePaging(int? page, int? pageSize, int defaultPageSize) =>
        (Math.Max(page ?? 1, 1), Math.Clamp(pageSize ?? defaultPageSize, 1, 100));

    // A sort defaults to descending; only an explicit "asc" flips it.
    private static bool SortDescending(string? sortDir) =>
        !string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);

    // Mutable accumulator for the portfolio-by-industry aggregate; folded into an IndustryHoldingResponse per bucket.
    private sealed class IndustryBucket
    {
        public IndustryBucket(int industryId, string industryName)
        {
            IndustryId = industryId;
            IndustryName = industryName;
        }

        public int IndustryId { get; }
        public string IndustryName { get; }
        public int CompanyCount { get; set; }
        public int Shares { get; set; }
        public decimal Value { get; set; }
        public decimal CostBasis { get; set; }
    }

    private sealed record ParticipantValuation(
        int SharesOwned,
        int CompaniesOwned,
        decimal HoldingsValue,
        decimal CostBasis,
        decimal LoanLiability,
        MarginAccountResponse Margin,
        decimal TotalWorth,
        int PendingSettlementCount,
        int? NextSettlementDueDayNumber);

    private sealed record IndustrySentimentHistoryRow(
        int IndustryId,
        int CreatedInCycleId,
        int CycleNumber,
        int SentimentValue,
        DateTime CreatedAt);
}
