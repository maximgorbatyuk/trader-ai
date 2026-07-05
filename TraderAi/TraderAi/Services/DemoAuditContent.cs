namespace TraderAi.Services;

// Headlines for auditor findings: a neutral high-risk flag that carries no price impact, and a discovered-issue
// story that accompanies a forced price correction. Each call draws one Next from a caller-supplied Random to
// pick a template, so output is reproducible for a given seed.
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

    public static (string Title, string Content) HighRisk(string companyName, Random random) =>
        Fill(HighRiskTemplates[random.Next(HighRiskTemplates.Length)], companyName);

    public static (string Title, string Content) Issue(string companyName, Random random) =>
        Fill(IssueTemplates[random.Next(IssueTemplates.Length)], companyName);

    private static (string Title, string Content) Fill((string Title, string Content) template, string companyName) =>
        (template.Title.Replace("{company}", companyName), template.Content.Replace("{company}", companyName));
}
