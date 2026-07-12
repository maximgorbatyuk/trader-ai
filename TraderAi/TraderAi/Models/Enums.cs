namespace TraderAi.Models;

public enum ParticipantType
{
    Individual,
    Company,
    AIAgent,
    CollectiveFund,
    Player,
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
    Bankruptcy,
    CollectiveFund,
    CollectiveFundDividend,
    LoanDisbursement,
    LoanInterest,
    LoanRepayment,
    LoanFine,
    TradeFee,
    FundAdvertisement,
}

public enum LoanStatus
{
    Open,
    Closed,
}

public enum LoanCloseReason
{
    PaidInFull,
    ParticipantDeparted,
}

public enum MarketStatus
{
    NotStarted,
    Running,
    Paused,
    Completed,
}

public enum NewsImpactScope
{
    None,
    Company,
    Industries,
}

public enum NewsImpactDirection
{
    Increase,
    Decrease,
}

// Classifies a post so the newswire can flag structural corporate actions distinctly from ordinary news;
// General covers automated and manual posts that carry no special treatment.
public enum NewsCategory
{
    General,
    CompanyClosed,
    StockSplit,
    StockMerge,
    FundPerformance,
    FundAdvertisement,
}

public enum CrisisScope
{
    Local,
    Global,
}

public enum CollectiveFundStatus
{
    Active,
    GoingToBeClosed,
    Closed,
}

public enum CollectiveFundMembershipEventType
{
    Joined,
    Left,
}

public enum MarketExitReason
{
    FundLoss,
    Starvation,
}

public enum CompanyRiskRating
{
    Low,
    High,
    Extra,
}

public enum CrisisEventType
{
    IndustryShock,
    AuditorRating,
    Bankruptcy,
    FundClosed,
    CompanyClosed,
}
