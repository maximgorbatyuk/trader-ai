using TraderAi.Models;

namespace TraderAi.Services;

// Placeholder sizer that ignores temperament and picks a random quantity within the cap. The
// temperament-aware logic will replace the body without changing callers.
public sealed class RandomTradeSizer(Random random) : ITradeSizer
{
    public int Size(Temperament temperament, int maxQuantity) =>
        maxQuantity < 1 ? 0 : random.Next(1, maxQuantity + 1);
}
