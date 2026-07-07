// Presentation maps for the money-transaction enum, shared by the trader detail and dashboard player panels
// so both render cash movements identically. Loan and fund activity would otherwise show its raw PascalCase
// name and pick the wrong up/down tone.
export const CASH_TONE = {
  Credit: 'up',
  Debit: 'down',
  Reserve: 'flat',
  Release: 'flat',
  LoanDisbursement: 'up',
  LoanInterest: 'down',
  LoanRepayment: 'down',
  LoanFine: 'down',
}

export const CASH_LABEL = {
  LoanDisbursement: 'Loan taken',
  LoanInterest: 'Loan interest',
  LoanRepayment: 'Loan repayment',
  LoanFine: 'Loan fine',
  CollectiveFund: 'Fund deposit',
  CollectiveFundDividend: 'Fund dividend',
}
