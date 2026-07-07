using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Gives the market natural churn. Once per cycle, after fund processing has settled, a trader stuck with no cash
// and no shares for a long stretch gets a rising chance to leave for good, and a devastated ex-fund-member gets a
// one-shot chance to quit on its first shareless cycle. A departing trader's row is deleted, its history archived
// to a MarketExit row, and a fresh replacement is spawned in its place so the population holds steady. Called
// from inside order maintenance after its final save — so a fund that closed this cycle has already persisted its
// payouts and loss flags — and stages its own changes for a trailing save.
public sealed class MarketExitService(
    AppDbContext dbContext,
    IOptions<MarketExitOptions> options,
    IOptions<RandomChanceRatesOptions> chanceRates,
    Random random)
{
    // A trader with less than this in cash can starve out, once it is also shareless and has been unable to buy
    // for a long stretch.
    private const decimal StarvationBalanceLine = 50_000m;
    private const int StarvationDroughtCycles = 20;

    // Starvation odds open at the drought threshold and climb one step per further can't-buy cycle up to 1.0.
    private const double StarvationStepPerCycle = 0.01;

    // A replacement trader starts with a whole-dollar balance drawn uniformly from this range (the player range).
    private const int ReplacementMinBalance = 10_000;
    private const int ReplacementMaxBalance = 200_000;

    private static readonly Temperament[] Temperaments =
        [Temperament.Aggressive, Temperament.Balanced, Temperament.Conservative];
    private static readonly RiskProfile[] RiskProfiles =
        [RiskProfile.High, RiskProfile.Medium, RiskProfile.Low];

    private Dictionary<int, decimal> latestPriceByCompany = null!;
    private Dictionary<int, List<OwnedHolding>> ownedByParticipant = null!;
    private Dictionary<int, List<Order>> openBuyOrdersByParticipant = null!;
    private HashSet<int> fundMemberIds = null!;
    private HashSet<string> takenNames = null!;

    // Draw discipline for a scripted Random in tests: no draws in the max-worth ratchet; at most one NextDouble()
    // per exit candidate in id order (fund-loss holders before starvation); and only a confirmed departure burns
    // its replacement's fixed run of draws — balance, type, temperament, risk, then name. An active crisis only
    // scales the exit threshold, it takes no extra draw, so calm-market sequences are unchanged.
    public async Task ProcessForCycleAsync(int currentCycleId, int currentCycleNumber, DateTime now, Crisis? activeCrisis = null)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var crisisExitMultiplier = activeCrisis?.Scope switch
        {
            CrisisScope.Global => chanceRates.Value.ChanceModifiers.GlobalCrisisExitMultiplier,
            CrisisScope.Local => chanceRates.Value.ChanceModifiers.LocalCrisisExitMultiplier,
            _ => 1.0,
        };

        await LoadStateAsync();

        // Stable id order keeps the per-trader random draws reproducible for a scripted Random in tests.
        var participants = await dbContext.Participants.OrderBy(participant => participant.Id).ToListAsync();

        // Pass 1: ratchet each trader's high-water worth up only. No random draws.
        foreach (var participant in participants)
        {
            if (participant.Type is not (ParticipantType.Individual or ParticipantType.AIAgent))
            {
                continue;
            }

            var owned = ownedByParticipant.GetValueOrDefault(participant.Id) ?? [];
            var worth = participant.CurrentBalance
                + owned.Sum(holding => holding.Quantity * latestPriceByCompany.GetValueOrDefault(holding.CompanyId));
            if (worth > participant.MaxTotalWorth)
            {
                participant.MaxTotalWorth = worth;
            }
        }

        // Pass 2: exit rolls. Each candidate rolls at most once per cycle.
        foreach (var participant in participants)
        {
            var owned = ownedByParticipant.GetValueOrDefault(participant.Id) ?? [];

            if (participant.PendingFundLossExitRoll)
            {
                // Defer the roll (keep the flag, no draw) until the member can leave cleanly — not bankrupt,
                // shareless, and free of any fund, since deleting a current fund member would orphan its live
                // membership row. The one chance is then spent on its first eligible cycle.
                if (participant.IsBankrupt || owned.Count > 0 || fundMemberIds.Contains(participant.Id))
                {
                    continue;
                }

                participant.PendingFundLossExitRoll = false;
                if (random.NextDouble() < Math.Min(1.0, chanceRates.Value.EventTriggerChances.ExitFundLoss * crisisExitMultiplier))
                {
                    await DepartAsync(participant, MarketExitReason.FundLoss, currentCycleId, now);
                }

                continue;
            }

            if (!IsStarvationCandidate(participant, owned))
            {
                continue;
            }

            var chance = Math.Min(
                1.0,
                (chanceRates.Value.EventTriggerChances.ExitStarvationBase
                    + (StarvationStepPerCycle * (participant.CannotBuyCycles - StarvationDroughtCycles)))
                    * crisisExitMultiplier);
            if (random.NextDouble() < chance)
            {
                await DepartAsync(participant, MarketExitReason.Starvation, currentCycleId, now);
            }
        }
    }

    private bool IsStarvationCandidate(Participant participant, List<OwnedHolding> owned) =>
        participant.IsActive
        && !participant.IsBankrupt
        && participant.Type is ParticipantType.Individual or ParticipantType.AIAgent
        && !fundMemberIds.Contains(participant.Id)
        && participant.CurrentBalance < StarvationBalanceLine
        && owned.Count == 0
        && participant.CannotBuyCycles >= StarvationDroughtCycles;

    private async Task DepartAsync(Participant participant, MarketExitReason reason, int currentCycleId, DateTime now)
    {
        // A departing trader is shareless, so it can hold no open sell order (a sell offers shares it owns); only
        // its standing buys need cancelling — to release reserved cash and keep ghost bids out of matching. The
        // reserve is a hold inside CurrentBalance, so QuitBalance is complete whether taken before or after.
        foreach (var order in OpenBuys(participant.Id))
        {
            CancelBuy(order, participant, currentCycleId, now);
        }

        var quitBalance = participant.CurrentBalance;
        var ordersPlaced = await dbContext.Orders.CountAsync(order => order.ParticipantId == participant.Id);

        dbContext.MarketExits.Add(new MarketExit
        {
            ParticipantId = participant.Id,
            Name = participant.Name,
            Reason = reason,
            JoinedInCycleId = participant.JoinedInCycleId,
            LeftInCycleId = currentCycleId,
            OrdersPlaced = ordersPlaced,
            InitialBalance = participant.InitialBalance,
            MaxTotalWorth = participant.MaxTotalWorth,
            QuitBalance = quitBalance,
            LeftAt = now,
        });

        // Nothing FK-references the Participants table, so the row deletes cleanly, leaving only orphaned scalar
        // ids in history tables that every read path already tolerates (numeric-id fallbacks / id-keyed lookups).
        // Both exit gates exclude current fund members, so no live CollectiveFundParticipant is ever orphaned.
        dbContext.Participants.Remove(participant);

        SpawnReplacement(currentCycleId, now);
    }

    private void SpawnReplacement(int currentCycleId, DateTime now)
    {
        var balance = random.Next(ReplacementMinBalance, ReplacementMaxBalance + 1);
        var type = random.Next(2) == 0 ? ParticipantType.Individual : ParticipantType.AIAgent;
        var temperament = Temperaments[random.Next(Temperaments.Length)];
        var riskProfile = RiskProfiles[random.Next(RiskProfiles.Length)];
        var name = DemoMarketNames.PickOnePerson(random, takenNames);

        // Keep the freed name reserved for the rest of this pass so a second departure this cycle picks another.
        takenNames.Add(name);

        dbContext.Participants.Add(new Participant
        {
            Name = name,
            Type = type,
            Temperament = temperament,
            RiskProfile = riskProfile,
            InitialBalance = balance,
            CurrentBalance = balance,
            ReservedBalance = 0m,
            IsActive = true,
            JoinedInCycleId = currentCycleId,
            MaxTotalWorth = balance,
        });
    }

    private void CancelBuy(Order order, Participant participant, int currentCycleId, DateTime now)
    {
        var release = order.ReservedCashAmount;
        if (release > 0m)
        {
            participant.ReservedBalance -= release;
            order.ReservedCashAmount = 0m;
            dbContext.MoneyTransactions.Add(new MoneyTransaction
            {
                ParticipantId = participant.Id,
                Type = MoneyTransactionType.Release,
                Amount = release,
                RelatedOrderId = order.Id,
                CreatedInCycleId = currentCycleId,
                CreatedAt = now,
            });
        }

        order.Status = OrderStatus.Cancelled;
        order.UpdatedAt = now;
    }

    private IEnumerable<Order> OpenBuys(int participantId) =>
        openBuyOrdersByParticipant.GetValueOrDefault(participantId) ?? [];

    private async Task LoadStateAsync()
    {
        latestPriceByCompany = await LatestPriceByCompanyAsync();

        ownedByParticipant = (await dbContext.Holdings
                .Where(holding => holding.Quantity > 0)
                .Select(holding => new OwnedHolding(holding.ParticipantId, holding.CompanyId, holding.Quantity))
                .ToListAsync())
            .GroupBy(holding => holding.OwnerId)
            .ToDictionary(group => group.Key, group => group.ToList());

        openBuyOrdersByParticipant = (await dbContext.Orders
                .Where(order => order.ParticipantId != null
                    && order.Type == OrderType.Buy
                    && (order.Status == OrderStatus.Open || order.Status == OrderStatus.PartiallyFilled))
                .ToListAsync())
            .GroupBy(order => order.ParticipantId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        fundMemberIds = (await dbContext.CollectiveFundParticipants
                .Select(member => member.ParticipantId)
                .ToListAsync())
            .ToHashSet();

        takenNames = (await dbContext.Participants
                .Select(participant => participant.Name)
                .ToListAsync())
            .ToHashSet();
    }

    private Task<Dictionary<int, decimal>> LatestPriceByCompanyAsync() =>
        PriceSnapshotQueries.LatestPriceByCompanyAsync(dbContext);

    private readonly record struct OwnedHolding(int OwnerId, int CompanyId, int Quantity);
}
