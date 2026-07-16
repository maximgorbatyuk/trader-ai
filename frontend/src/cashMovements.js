// Presentation maps for the money-transaction enum, shared by the trader detail and dashboard player panels
// so both render cash movements identically. Loan and fund activity would otherwise show its raw PascalCase
// name and pick the wrong tone. Tones group each type by cash effect: income (green), spend (dark red),
// fee (amber), and reservation (blue); anything unmapped stays neutral.
export const CASH_TONE = {
  Credit: 'income',
  Dividend: 'income',
  CollectiveFundDividend: 'income',
  CollectiveFundDividendFeeReceived: 'income',
  CollectiveFundWithdrawalReceived: 'income',
  CollectiveFundManagerFeeReceived: 'income',
  LoanDisbursement: 'income',
  MarginAdvance: 'income',
  Debit: 'spend',
  Bankruptcy: 'spend',
  CollectiveFund: 'spend',
  CollectiveFundDividendPaid: 'spend',
  CollectiveFundWithdrawal: 'spend',
  LoanInterest: 'spend',
  LoanRepayment: 'spend',
  LoanFine: 'spend',
  MarginInterestPayment: 'spend',
  MarginDebitRepayment: 'spend',
  FundAdvertisement: 'spend',
  TradeFee: 'fee',
  CollectiveFundDividendFee: 'fee',
  CollectiveFundManagerFee: 'fee',
  Reserve: 'reserve',
  Release: 'reserve',
}

export const CASH_LABEL = {
  LoanDisbursement: 'Loan taken',
  LoanInterest: 'Loan interest',
  LoanRepayment: 'Loan repayment',
  LoanFine: 'Loan fine',
  CollectiveFund: 'Fund deposit',
  CollectiveFundDividend: 'Fund dividend',
  CollectiveFundDividendPaid: 'Fund dividend paid',
  CollectiveFundDividendFee: 'Fund fee',
  CollectiveFundDividendFeeReceived: 'Fund fee received',
  CollectiveFundWithdrawal: 'Fund withdrawal',
  CollectiveFundWithdrawalReceived: 'Fund withdrawal',
  CollectiveFundManagerFee: 'Manager fee',
  CollectiveFundManagerFeeReceived: 'Manager fee',
  FundAdvertisement: 'Fund advertisement',
  TradeFee: 'Trade fee',
  MarginAdvance: 'Margin advance',
  MarginInterestPayment: 'Margin interest',
  MarginDebitRepayment: 'Margin debit repayment',
}

const CORPORATE_CASH_MOVEMENT_PRESENTATION = {
  PrimaryIssuance: {
    label: 'Primary issuance',
    direction: 'Credit',
    sign: '+',
    tone: 'up',
  },
  OperatingIncome: {
    label: 'Operating income',
    direction: 'Credit',
    sign: '+',
    tone: 'up',
  },
  BigInvestment: {
    label: 'Big investment',
    direction: 'Credit',
    sign: '+',
    tone: 'up',
  },
  DividendDeclared: {
    label: 'Dividend paid',
    direction: 'Debit',
    sign: '−',
    tone: 'down',
  },
  ClosureDistribution: {
    label: 'Closure distribution',
    direction: 'Debit',
    sign: '−',
    tone: 'down',
  },
}

export function corporateCashMovementPresentation(type) {
  const presentation = CORPORATE_CASH_MOVEMENT_PRESENTATION[type]
  if (presentation) return presentation

  const rawLabel = typeof type === 'string' ? type.trim().replace(/([a-z0-9])([A-Z])/g, '$1 $2') : ''
  const label = rawLabel ? `${rawLabel[0].toUpperCase()}${rawLabel.slice(1).toLowerCase()}` : 'Corporate cash movement'

  return {
    label,
    direction: 'Movement',
    sign: '',
    tone: 'neutral',
  }
}
