using TraderAi.Data;
using TraderAi.Models;

namespace TraderAi.Services;

// Shared cancellation of a single resting order so the reserved-cash release stays identical wherever an order
// is cancelled: rule-based ageing, player and fund cancels, and AI convert-back. It stages the mutation only;
// the caller owns the market lock and the SaveChanges.
internal static class OrderCancellation
{
    public static void Cancel(AppDbContext dbContext, Order order, Participant participant, int currentCycleId)
    {
        if (order.Type == OrderType.Buy)
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
                    Description = "Reserved cash released on buy order cancel",
                    CreatedInCycleId = currentCycleId,
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }

        // A sell reserves no cash and holds no links; cancelling it simply stops the order counting toward the
        // seller's outstanding sells, freeing that quantity to be listed again.
        order.Status = OrderStatus.Cancelled;
        order.UpdatedAt = DateTime.UtcNow;
    }
}
