using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

public sealed record BigInvestmentOpportunity(
    int CompanyId,
    string CompanyName,
    decimal CurrentPrice,
    decimal Capitalization,
    decimal MinimumAmount,
    decimal MaximumAmount);

// Executes both automated and manual direct funding deals by minting shares at the current price and settling the
// resulting cash, holding, rating, sentiment, protection, and history changes together. It runs after primary
// issuance and before closure so a fresh deal's delisting protection is honoured in the same cycle.
// Generic draws are trigger, pair, then size; a fresh Extra raise draws pair, cash-scaled chance, then size, while
// disabled processing and the shared deal executor draw nothing so scripted simulations remain reproducible.
public sealed class BigInvestmentService(
    AppDbContext dbContext,
    IOptions<BigInvestmentOptions> options,
    IOptions<RandomChanceRatesOptions> chanceRates,
    MarketImpactService marketImpact,
    Random random,
    IOptions<IndustrySentimentOptions>? industrySentimentOptions = null)
{
    // Trading days a fresh deal shields the company from delisting.
    private const int ProtectionTradingDays = 5;

    // The auditor "raise" attached to a deal lifts the price by this fixed percent, inside the ordinary raise band.
    private const decimal RatingImpactPercent = 8m;

    // Positive sentiment nudge applied to the company's industry.
    private const int IndustrySentimentPush = 10;

    private readonly IndustrySentimentOptions industrySentimentOptionValues =
        industrySentimentOptions?.Value ?? new IndustrySentimentOptions();

    public bool IsEnabled => options.Value.Enabled;

    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var triggers = chanceRates.Value.EventTriggerChances;
        var bands = chanceRates.Value.RandomMagnitudeBands;
        var minFraction = (decimal)bands.BigInvestmentFractionMin;

        var freshExtraRaiseCompanyIds = await (
                from rating in dbContext.CompanyRatings
                join cycle in dbContext.MarketCycles on rating.CreatedInCycleId equals cycle.Id
                where rating.Rating == CompanyRiskRating.ExtraRaisedExpectations
                    && cycle.CycleNumber == currentCycleNumber - 1
                select rating.CompanyId)
            .Distinct()
            .ToHashSetAsync();
        var hasFreshExtraRaise = freshExtraRaiseCompanyIds.Count > 0;

        if (!hasFreshExtraRaise && random.NextDouble() >= triggers.BigInvestment)
        {
            return;
        }

        var latestPriceByCompany = await PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);
        var companyQuery = dbContext.Companies.Where(company => company.ClosedInCycleId == null);
        if (hasFreshExtraRaise)
        {
            companyQuery = companyQuery.Where(company => freshExtraRaiseCompanyIds.Contains(company.Id));
        }

        var companies = await companyQuery
            .OrderBy(company => company.Id)
            .ToListAsync();
        var investors = await dbContext.Participants
            .Where(participant => participant.IsActive
                && !participant.IsBankrupt
                && (participant.Type == ParticipantType.Individual
                    || participant.Type == ParticipantType.CollectiveFund))
            .OrderBy(participant => participant.Id)
            .ToListAsync();

        // Pairs are built in (company id, participant id) order so the selection draw is reproducible.
        var pairs = new List<(Participant Investor, Company Company, decimal Price, decimal Cap)>();
        foreach (var company in companies)
        {
            if (!latestPriceByCompany.TryGetValue(company.Id, out var price) || price <= 0m)
            {
                continue;
            }

            var cap = price * company.IssuedSharesCount;
            if (cap <= 0m)
            {
                continue;
            }

            var minCash = minFraction * cap;
            foreach (var investor in investors)
            {
                if (Spendable(investor) >= minCash)
                {
                    pairs.Add((investor, company, price, cap));
                }
            }
        }

        if (pairs.Count == 0)
        {
            return;
        }

        var chosen = pairs[random.Next(pairs.Count)];
        if (hasFreshExtraRaise)
        {
            var minimumRequiredCash = minFraction * chosen.Cap;
            var cashScaledChance = Math.Min(
                Math.Min(triggers.BigInvestmentMax, EventTriggerChances.BigInvestmentHardMax),
                triggers.BigInvestment * (double)(Spendable(chosen.Investor) / minimumRequiredCash));
            if (random.NextDouble() >= cashScaledChance)
            {
                return;
            }
        }

        var fraction = minFraction
            + ((decimal)random.NextDouble() * ((decimal)bands.BigInvestmentFractionMax - minFraction));
        var cash = Math.Min(fraction * chosen.Cap, Spendable(chosen.Investor));

        await ExecuteDealAsync(chosen.Investor, chosen.Company, chosen.Price, cash, currentCycleId, now);
    }

    // Stages every record for one deal and returns the shares minted (0 if the cash cannot buy a whole share). The
    // caller owns the surrounding save; the one internal save mints the filled orders' ids and lets the raise's
    // market-impact query read the enlarged supply and the deal-price snapshot.
    public async Task<int> ExecuteDealAsync(
        Participant investor, Company company, decimal price, decimal investmentCash, int currentCycleId, DateTime now)
    {
        var newShares = (int)Math.Floor(investmentCash / price);
        if (newShares <= 0)
        {
            return 0;
        }

        var cash = newShares * price;
        var sharesBeforeDeal = company.IssuedSharesCount;

        // A company-originated sell (no participant seller) and the investor's buy, both filled in place rather
        // than left resting on the book.
        var sellOrder = new Order
        {
            ParticipantId = null,
            CompanyId = company.Id,
            Type = OrderType.Sell,
            Status = OrderStatus.Filled,
            Quantity = newShares,
            FilledQuantity = newShares,
            LimitPrice = price,
            ReservedCashAmount = 0m,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var buyOrder = new Order
        {
            ParticipantId = investor.Id,
            CompanyId = company.Id,
            Type = OrderType.Buy,
            Status = OrderStatus.Filled,
            Quantity = newShares,
            FilledQuantity = newShares,
            LimitPrice = price,
            ReservedCashAmount = 0m,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.Orders.Add(sellOrder);
        dbContext.Orders.Add(buyOrder);

        // The investor pays now and the shares settle immediately, so the economic and settled balances move
        // together and neither goes negative (eligibility is gated on the settled floor).
        investor.CurrentBalance -= cash;
        investor.SettledCashBalance -= cash;

        company.IssuedSharesCount += newShares;
        company.CashBalance += cash;
        company.UpdatedAt = now;
        dbContext.CorporateCashTransactions.Add(new CorporateCashTransaction
        {
            CompanyId = company.Id,
            Type = CorporateCashTransactionType.BigInvestment,
            Amount = cash,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        });

        var holding = await dbContext.Holdings
            .FirstOrDefaultAsync(candidate => candidate.ParticipantId == investor.Id && candidate.CompanyId == company.Id);
        var priorHoldingQuantity = holding?.Quantity ?? 0;
        if (holding is null)
        {
            dbContext.Holdings.Add(new Holding
            {
                ParticipantId = investor.Id,
                CompanyId = company.Id,
                Quantity = newShares,
                SettledQuantity = newShares,
                AverageCost = price,
            });
        }
        else
        {
            var blended = ((holding.Quantity * holding.AverageCost) + (newShares * price)) / (holding.Quantity + newShares);
            holding.AverageCost = Math.Round(blended, 2, MidpointRounding.AwayFromZero);
            holding.Quantity += newShares;
            holding.SettledQuantity += newShares;
        }

        dbContext.PriceSnapshots.Add(new PriceSnapshot
        {
            CompanyId = company.Id,
            Price = price,
            Capitalization = price * company.IssuedSharesCount,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        });

        var currentTradingDayNumber = await TradingDayNumberForCycleAsync(currentCycleId);
        if (currentTradingDayNumber is int dayNumber)
        {
            company.CloseProtectedUntilTradingDayNumber = dayNumber + ProtectionTradingDays;
        }

        if (industrySentimentOptionValues.Enabled)
        {
            var industry = await dbContext.Industries.FirstOrDefaultAsync(candidate => candidate.Id == company.IndustryId);
            if (industry is not null)
            {
                var limit = Math.Max(0, industrySentimentOptionValues.SentimentValueLimit);
                industry.SentimentValue = (int)Math.Clamp(
                    (long)industry.SentimentValue + IndustrySentimentPush,
                    -(long)limit,
                    limit);
            }
        }

        var investorSharesAfter = priorHoldingQuantity + newShares;
        dbContext.CompanyInvestments.Add(new CompanyInvestment
        {
            CompanyId = company.Id,
            InvestorParticipantId = investor.Id,
            DealValue = cash,
            SharesIssued = newShares,
            SharesBeforeDeal = sharesBeforeDeal,
            CapitalizationBeforeDeal = price * sharesBeforeDeal,
            FinalCapitalization = price * company.IssuedSharesCount,
            InvestorSharePercent = company.IssuedSharesCount > 0
                ? Math.Round(investorSharesAfter / (decimal)company.IssuedSharesCount * 100m, 2, MidpointRounding.AwayFromZero)
                : 0m,
            TradingDayNumber = currentTradingDayNumber,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        });

        await dbContext.SaveChangesAsync();

        var shareTransaction = new ShareTransaction
        {
            SellerId = null,
            BuyerId = investor.Id,
            CompanyId = company.Id,
            Quantity = newShares,
            Price = price,
            TotalCost = cash,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.ShareTransactions.Add(shareTransaction);
        dbContext.OrderFills.Add(new OrderFill
        {
            BuyOrderId = buyOrder.Id,
            SellOrderId = sellOrder.Id,
            Quantity = newShares,
            ExecutionPrice = price,
            ShareTransaction = shareTransaction,
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        });
        dbContext.MoneyTransactions.Add(new MoneyTransaction
        {
            ParticipantId = investor.Id,
            Type = MoneyTransactionType.Debit,
            Amount = cash,
            RelatedOrderId = buyOrder.Id,
            RelatedShareTransaction = shareTransaction,
            Description = $"Big investment in {company.Name}",
            CreatedInCycleId = currentCycleId,
            CreatedAt = now,
        });

        // The deal earns an auditor "raise": a RaisedExpectations rating with a positive price lift. Attributed to
        // any existing auditor; skipped only in the degenerate case of a market with no auditors yet.
        var auditor = await dbContext.Auditors.OrderBy(candidate => candidate.Id).FirstOrDefaultAsync();
        if (auditor is not null)
        {
            dbContext.CompanyRatings.Add(new CompanyRating
            {
                CompanyId = company.Id,
                AuditorId = auditor.Id,
                Rating = CompanyRiskRating.RaisedExpectations,
                ImpactPercent = RatingImpactPercent,
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
            });

            await marketImpact.ApplyImpactAsync(
                NewsImpactDirection.Increase,
                [company.Id],
                RatingImpactPercent,
                currentCycleId,
                now,
                cancelStaleOrders: false);
        }

        dbContext.NewsPosts.Add(new NewsPost
        {
            Title = $"{company.Name} raises ${cash:N0} from {investor.Name}",
            Content = $"{investor.Name} invested ${cash:N0} in {company.Name} for {newShares:N0} newly issued shares. The capital raise lifts the company's cash reserves and expands its share count.",
            PublishedInCycleId = currentCycleId,
            PublishedAt = now,
            Scope = NewsImpactScope.None,
            Category = NewsCategory.CapitalRaise,
        });

        return newShares;
    }

    // Cash an investor can commit without pushing either balance negative: bounded by both available and settled
    // cash so an immediately-settled deal never strands a reservation or oversells settled cash.
    public static decimal Spendable(Participant participant) =>
        Math.Max(0m, Math.Min(participant.AvailableBalance, participant.SettledCashBalance));

    public static BigInvestmentOpportunity? BuildOpportunity(
        Participant investor,
        int companyId,
        string companyName,
        decimal price,
        int issuedShares,
        RandomMagnitudeBands bands)
    {
        if (price <= 0m || issuedShares <= 0)
        {
            return null;
        }

        var capitalization = price * issuedShares;
        var minimumAmount = (decimal)bands.BigInvestmentFractionMin * capitalization;
        var maximumAmount = Math.Min(
            (decimal)bands.BigInvestmentFractionMax * capitalization,
            Spendable(investor));
        return minimumAmount > 0m && maximumAmount >= minimumAmount
            ? new BigInvestmentOpportunity(
                companyId,
                companyName,
                price,
                capitalization,
                minimumAmount,
                maximumAmount)
            : null;
    }

    private async Task<int?> TradingDayNumberForCycleAsync(int cycleId) =>
        await (from cycle in dbContext.MarketCycles
               join day in dbContext.TradingDays on cycle.TradingDayId equals day.Id
               where cycle.Id == cycleId
               select (int?)day.DayNumber)
            .SingleOrDefaultAsync();
}
