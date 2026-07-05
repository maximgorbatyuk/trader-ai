namespace TraderAi.Services;

// Curated names and one-line descriptions for the rating agencies seeded into a market, so auditors read like
// real firms rather than "Auditor 01". Taken in list order with no random draw, keeping seeding deterministic.
internal static class DemoAuditorProfiles
{
    private static readonly (string Name, string Description)[] Profiles =
    [
        ("Meridian Ratings", "Independent credit and risk assessor covering listed equities."),
        ("Vantage Assurance", "Reviews issuer disclosures and flags governance risk."),
        ("Halcyon Analytics", "Data-driven house scoring price stability and volatility."),
        ("Ironwood Review Board", "Conservative auditor known for early fraud calls."),
        ("Sterling Oversight", "Examines financial statements for hidden liabilities."),
        ("Beacon Diligence", "Field auditor specialising in operational red flags."),
        ("Argent Compliance", "Tracks regulatory and accounting irregularities."),
        ("Cascade Ratings Group", "Broad-coverage agency rating market-wide risk."),
        ("Keystone Audit Partners", "Long-tenured firm with a strict rating scale."),
        ("Lodestar Integrity", "Forensic auditor focused on management conduct."),
    ];

    // Returns count name/description pairs in list order, wrapping with a numeric suffix if more are requested
    // than the pool holds.
    public static IReadOnlyList<(string Name, string Description)> Take(int count)
    {
        var result = new List<(string, string)>(count);
        for (var index = 0; index < count; index++)
        {
            var (name, description) = Profiles[index % Profiles.Length];
            var wrap = index / Profiles.Length;
            result.Add((wrap == 0 ? name : $"{name} {wrap + 1}", description));
        }

        return result;
    }
}
