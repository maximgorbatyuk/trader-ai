using TraderAi.Models;

namespace TraderAi.Services;

// Decides how many shares a single order should be for, capped at maxQuantity (shares owned for a
// sell, shares affordable for a buy). Temperament is supplied so sizing can later reflect personality.
public interface ITradeSizer
{
    int Size(Temperament temperament, int maxQuantity);
}
