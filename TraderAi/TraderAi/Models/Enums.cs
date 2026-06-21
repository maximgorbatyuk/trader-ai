namespace TraderAi.Models;

public enum ParticipantType
{
    Individual,
    Company,
    AIAgent,
}

public enum Temperament
{
    Aggressive,
    Balanced,
    Conservative,
}

public enum RiskProfile
{
    High,
    Medium,
    Low,
}

public enum CycleStatus
{
    Planned,
    Running,
    Completed,
    Failed,
}

public enum OrderType
{
    Buy,
    Sell,
}

public enum OrderStatus
{
    Open,
    PartiallyFilled,
    Filled,
    Cancelled,
}

public enum MoneyTransactionType
{
    Reserve,
    Release,
    Debit,
    Credit,
    Dividend,
}

public enum MarketStatus
{
    NotStarted,
    Running,
    Paused,
    Completed,
}
