using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Issues new free shares for very large companies to dilute a runaway per-share price. Once a company's
// capitalisation clears a threshold it rolls each cycle — with odds that climb the larger it is — to mint a small
// slice of new shares and hand them, at zero cost, to active traders that do not already hold the stock, capped
// per recipient. It forces no price move and cancels no orders: the enlarged supply reprices the stock through
// ordinary trading. Runs in the pre-match window right after splits; stages changes and the caller owns the save.
//
// Draw discipline for a scripted Random: companies are walked in ascending id order. A company below the
// capitalisation threshold or still inside its cooldown draws nothing. Each company that clears both gates draws
// one NextDouble to roll for an emission; if it fires it draws one more for the size, then one Next per recipient
// it funds while partially shuffling the eligible pool.
public sealed class ShareEmissionService(
    AppDbContext dbContext,
    IOptions<ShareEmissionOptions> options,
    Random random)
{
    // A company starts to risk an emission once its capitalisation clears one band, and the chance climbs by a
    // step for every further band, capped so an emission never becomes a certainty.
    private const decimal CapitalizationBand = 500_000_000m;
    private const double ChancePerBand = 0.05;
    private const double MaxEmissionChance = 1.0;

    // A company that has just emitted is left alone for this many cycles.
    private const int SafePeriodCycles = 50;

    // The emission size is a random fraction of the current share count, in this range.
    private const double MinEmissionRate = 0.01;
    private const double MaxEmissionRate = 0.10;

    // No single recipient may receive more than this many free shares.
    private const int MaxSharesPerRecipient = 50;

    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var latestPriceByCompany = await LatestPriceByCompanyAsync();

        var cycleNumbersById = await dbContext.MarketCycles
            .ToDictionaryAsync(cycle => cycle.Id, cycle => cycle.CycleNumber);

        var lastEmissionCycleByCompany = (await dbContext.ShareEmissions
                .Select(emission => new { emission.CompanyId, emission.CreatedInCycleId })
                .ToListAsync())
            .GroupBy(emission => emission.CompanyId)
            .ToDictionary(
                group => group.Key,
                group => group.Max(emission => cycleNumbersById.GetValueOrDefault(emission.CreatedInCycleId)));

        // Keyed on any existing holding row, not just Quantity > 0: a sold-out position keeps a zero-quantity
        // row (see MatchingEngine.ReduceHolding), and inserting a second row for the same (participant, company)
        // would violate the unique key. Excluding them also matches "must not already hold the stock".
        var holdersByCompany = (await dbContext.Holdings
                .Select(holding => new { holding.CompanyId, holding.ParticipantId })
                .ToListAsync())
            .GroupBy(holding => holding.CompanyId)
            .ToDictionary(group => group.Key, group => group.Select(holding => holding.ParticipantId).ToHashSet());

        var recipientPool = await dbContext.Participants
            .Where(participant => participant.IsActive
                && (participant.Type == ParticipantType.Individual || participant.Type == ParticipantType.AIAgent))
            .OrderBy(participant => participant.Id)
            .Select(participant => participant.Id)
            .ToListAsync();

        var companies = await dbContext.Companies.OrderBy(company => company.Id).ToListAsync();

        foreach (var company in companies)
        {
            if (!latestPriceByCompany.TryGetValue(company.Id, out var price) || price <= 0m)
            {
                continue;
            }

            var bands = (int)(price * company.IssuedSharesCount / CapitalizationBand);
            if (bands <= 0)
            {
                continue;
            }

            if (lastEmissionCycleByCompany.TryGetValue(company.Id, out var lastCycle)
                && currentCycleNumber - lastCycle < SafePeriodCycles)
            {
                continue;
            }

            var chance = Math.Min(MaxEmissionChance, ChancePerBand * bands);
            if (random.NextDouble() >= chance)
            {
                continue;
            }

            var rate = MinEmissionRate + (random.NextDouble() * (MaxEmissionRate - MinEmissionRate));
            var nominal = (int)Math.Round(company.IssuedSharesCount * rate, MidpointRounding.AwayFromZero);
            if (nominal <= 0)
            {
                continue;
            }

            var holders = holdersByCompany.GetValueOrDefault(company.Id);
            var eligible = recipientPool.Where(id => holders is null || !holders.Contains(id)).ToList();
            if (eligible.Count == 0)
            {
                continue;
            }

            // The per-recipient cap bounds how many shares can actually be handed out this emission.
            var distributed = Math.Min(nominal, eligible.Count * MaxSharesPerRecipient);
            var recipientsNeeded = (distributed + MaxSharesPerRecipient - 1) / MaxSharesPerRecipient;

            // Partial Fisher–Yates: pull recipientsNeeded distinct recipients to the front of the eligible list.
            for (var index = 0; index < recipientsNeeded; index++)
            {
                var swap = index + random.Next(eligible.Count - index);
                (eligible[index], eligible[swap]) = (eligible[swap], eligible[index]);
            }

            // The mandated vehicle: the new shares are listed as a company-originated sell at zero price, filled
            // in place to the chosen recipients below rather than left resting on the book.
            dbContext.Orders.Add(new Order
            {
                ParticipantId = null,
                CompanyId = company.Id,
                Type = OrderType.Sell,
                Status = OrderStatus.Filled,
                Quantity = distributed,
                FilledQuantity = distributed,
                LimitPrice = 0m,
                ReservedCashAmount = 0m,
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
                UpdatedAt = now,
            });

            var remaining = distributed;
            for (var index = 0; index < recipientsNeeded; index++)
            {
                var grant = Math.Min(MaxSharesPerRecipient, remaining);
                dbContext.Holdings.Add(new Holding
                {
                    ParticipantId = eligible[index],
                    CompanyId = company.Id,
                    Quantity = grant,
                    AverageCost = 0m,
                });
                remaining -= grant;
            }

            company.IssuedSharesCount += distributed;
            company.UpdatedAt = now;

            dbContext.ShareEmissions.Add(new ShareEmission
            {
                CompanyId = company.Id,
                SharesEmitted = distributed,
                RecipientCount = recipientsNeeded,
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
            });

            dbContext.NewsPosts.Add(new NewsPost
            {
                Title = $"{company.Name} issues {distributed:N0} new free shares",
                Content = $"{company.Name} released {distributed:N0} new shares into the market, handed free to {recipientsNeeded:N0} traders who did not already hold the stock. The added supply is expected to weigh on the share price.",
                PublishedInCycleId = currentCycleId,
                PublishedAt = now,
                Scope = NewsImpactScope.None,
            });
        }
    }

    private Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync() =>
        PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);
}
