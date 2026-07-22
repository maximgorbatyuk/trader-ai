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
    CollectiveFundDividendPaid,
    CollectiveFundDividendFee,
    CollectiveFundDividendFeeReceived,
    CollectiveFundWithdrawal,
    CollectiveFundWithdrawalReceived,
    CollectiveFundManagerFee,
    CollectiveFundManagerFeeReceived,
    LoanDisbursement,
    LoanInterest,
    LoanRepayment,
    LoanFine,
    TradeFee,
    FundAdvertisement,
    MarginAdvance,
    MarginInterestPayment,
    MarginDebitRepayment,
}

public enum MarginAccountStatus
{
    Active,
    UnderCall,
    Closed,
}

public enum MarginCallStatus
{
    Open,
    Satisfied,
}

public enum CorporateCashTransactionType
{
    PrimaryIssuance,
    OperatingIncome,
    DividendDeclared,
    ClosureDistribution,
    BigInvestment,
}

public enum StockDenominationActionType
{
    Split,
    ReverseSplit,
}

public enum LuldState
{
    Normal,
    LimitState,
    TradingPause,
    Reopening,
}

public enum PriceLimitDirection
{
    Upper,
    Lower,
}

public enum SettlementStatus
{
    Pending,
    Settled,
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

public enum TradingSessionState
{
    Trading,
    Break,
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
    CapitalRaise,
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
    RaisedExpectations,
    ExtraRaisedExpectations,
}

public enum AiPredictionDirection
{
    Up,
    Down,
}

public enum CrisisEventType
{
    IndustryShock,
    AuditorRating,
    Bankruptcy,
    FundClosed,
    CompanyClosed,
}

public enum AiTraderCallStatus
{
    Pending,
    Completed,
    HttpError,
    TimedOut,
    InvalidJson,
    Cancelled,
    Abandoned,

    // An end-of-day planning call whose decision is stored and applied at the next trading day's opening cycle.
    PendingNextDay,
}
