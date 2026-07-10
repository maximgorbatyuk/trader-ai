// Presentation maps for the money-transaction enum, shared by the trader detail and dashboard player panels
// so both render cash movements identically. Loan and fund activity would otherwise show its raw PascalCase
// name and pick the wrong tone. Tones group each type by cash effect: income (green), spend (dark red),
// fee (amber), and reservation (blue); anything unmapped stays neutral.
export const CASH_TONE = {
  Credit: 'income',
  Dividend: 'income',
  CollectiveFundDividend: 'income',
  LoanDisbursement: 'income',
  Debit: 'spend',
  Bankruptcy: 'spend',
  CollectiveFund: 'spend',
  LoanInterest: 'spend',
  LoanRepayment: 'spend',
  LoanFine: 'spend',
  TradeFee: 'fee',
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
  TradeFee: 'Trade fee',
}
