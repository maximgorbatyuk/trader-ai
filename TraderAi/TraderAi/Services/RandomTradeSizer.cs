using TraderAi.Models;

namespace TraderAi.Services;

// Sizes an order from a uniform draw within the cap, then scales it by temperament: aggressive traders
// reach for larger packages while conservative ones take smaller bites to hold shares longer for
// dividends. Balanced keeps the raw draw, and the result is clamped back inside the cap.
public sealed class RandomTradeSizer(Random random) : ITradeSizer
{
    private const double AggressiveScale = 1.5;
    private const double ConservativeScale = 0.5;

    public int Size(Temperament temperament, int maxQuantity)
    {
        if (maxQuantity < 1)
        {
            return 0;
        }

        // Next's upper bound is exclusive; guard the +1 so a cap at the 32-bit max cannot overflow it.
        var upperExclusive = maxQuantity == int.MaxValue ? maxQuantity : maxQuantity + 1;
        var baseline = random.Next(1, upperExclusive);

        var scaled = temperament switch
        {
            Temperament.Aggressive => (int)Math.Ceiling(baseline * AggressiveScale),
            Temperament.Conservative => (int)Math.Floor(baseline * ConservativeScale),
            _ => baseline,
        };

        return Math.Clamp(scaled, 1, maxQuantity);
    }
}
