export const PLAYER_ONBOARDING_STEPS = [
  {
    title: 'This is a game',
    description: 'Trader AI is a simulated market. It uses no real money and does not provide financial advice.',
    points: [
      'The market advances in cycles as orders trade and news, crises, and company events change conditions.',
      'Orders match by price and time priority, transferring shares and cash between market participants.',
      'Prices are created by the simulation rather than copied from a real exchange.',
    ],
  },
  {
    title: 'Trade as the player',
    description: 'You join the same live order book as every other market participant.',
    points: [
      'Place buy and sell orders, inspect companies, follow news, and mark companies as favorites.',
      'Track cash, holdings, settlements, loans, and total worth from the player dashboard.',
      'Your open orders remain yours to manage—the market will not cancel or replace them for you.',
    ],
  },
  {
    title: 'Create a managed fund',
    description: 'Once you are ready, use part of your player cash to open a managed fund.',
    points: [
      'Switch between the player and fund while keeping each actor’s balances, holdings, and orders separate.',
      'Invite market participants through fund performance and advertising, then manage their shared capital.',
      'You decide how the fund trades; the automated fund engine will not take control away from you.',
    ],
  },
  {
    title: 'Add AI traders',
    description: 'You can convert an eligible individual into an AI trader and choose its provider and model.',
    points: [
      'The AI trader reads the live market, explains its decision, manages its orders, and may fund a company.',
      'Every action still passes through the same market rules and risk checks as other participants.',
      'Provider calls, responses, and outcomes remain visible so you can audit how the agent behaves.',
    ],
  },
  {
    title: 'Create your player',
    description: 'Choose the name that will represent you in the market. A starting balance is assigned when you join.',
    points: [],
  },
]
