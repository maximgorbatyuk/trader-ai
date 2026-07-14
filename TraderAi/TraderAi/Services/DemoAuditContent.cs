namespace TraderAi.Services;

// Separate templates keep the exceptional outcome visible in news, while caller-owned randomness preserves
// reproducible seeded simulations.
internal static class DemoAuditContent
{
    private static readonly (string Title, string Content)[] HighRiskTemplates =
    [
        ("{company} flagged high risk after a volatile run",
            "Auditors placed {company} on a high-risk footing after an unusually sharp price swing. No wrongdoing was found, but the agency urges caution."),
        ("Rating cut: {company} moved to high risk",
            "A routine review moved {company} to a high-risk rating, citing price moves well outside its normal range. The company's fundamentals were not questioned."),
        ("{company} added to the auditor watch list",
            "The rating desk added {company} to its watch list this cycle, noting heightened volatility but no evidence of an underlying problem."),
    ];

    private static readonly (string Title, string Content)[] IssueTemplates =
    [
        ("Auditors uncover accounting gaps at {company}",
            "A deep review of {company} surfaced material accounting irregularities. The finding is expected to weigh heavily on the share price."),
        ("{company} CEO misstated results, auditors say",
            "Investigators concluded that {company}'s chief executive overstated performance at the last conference. Confidence in the stock has been shaken."),
        ("Hidden liabilities found at {company}",
            "Auditors disclosed previously unreported liabilities on {company}'s books. The market reaction is likely to be severe."),
        ("Governance failures revealed at {company}",
            "A review of {company} exposed serious governance failures at board level. The agency warned the fallout could be lasting."),
    ];

    private static readonly (string Title, string Content)[] RaisedExpectationsTemplates =
    [
        ("Auditors raise expectations for {company}",
            "A clean review of {company} led auditors to raise their outlook, citing stronger execution and improving prospects."),
        ("{company} earns an upgraded outlook",
            "Auditors found no material issues at {company} and lifted expectations for its near-term performance."),
        ("Clean audit lifts confidence in {company}",
            "A clean audit strengthened confidence in {company}, with the rating desk pointing to improving fundamentals."),
    ];

    private static readonly (string Title, string Content)[] ExtraRaisedExpectationsTemplates =
    [
        ("Exceptional audit lifts expectations for {company}",
            "Auditors found exceptional strength at {company} and sharply raised their outlook for its near-term performance."),
        ("{company} earns exceptional auditor confidence",
            "A standout review of {company} led auditors to issue an exceptionally strong positive outlook."),
        ("Auditors see exceptional upside at {company}",
            "A clean review uncovered unusually strong prospects at {company}, prompting a major outlook upgrade."),
    ];

    public static (string Title, string Content) HighRisk(string companyName, Random random) =>
        Fill(HighRiskTemplates[random.Next(HighRiskTemplates.Length)], companyName);

    public static (string Title, string Content) Issue(string companyName, Random random) =>
        Fill(IssueTemplates[random.Next(IssueTemplates.Length)], companyName);

    public static (string Title, string Content) RaisedExpectations(string companyName, Random random) =>
        Fill(RaisedExpectationsTemplates[random.Next(RaisedExpectationsTemplates.Length)], companyName);

    public static (string Title, string Content) ExtraRaisedExpectations(string companyName, Random random) =>
        Fill(ExtraRaisedExpectationsTemplates[random.Next(ExtraRaisedExpectationsTemplates.Length)], companyName);

    private static (string Title, string Content) Fill((string Title, string Content) template, string companyName) =>
        (template.Title.Replace("{company}", companyName), template.Content.Replace("{company}", companyName));
}
