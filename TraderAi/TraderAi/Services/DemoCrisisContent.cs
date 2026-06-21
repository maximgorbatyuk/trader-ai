using TraderAi.Models;

namespace TraderAi.Services;

// Generates random but meaningful crisis headlines and bodies, filling two slots ({a}, {b}) of a template
// from curated fragment pools. Local crises read as regional or sector shocks; global ones read as
// market-wide downturns. Drawn from a caller-supplied Random so output is reproducible for a given seed.
internal static class DemoCrisisContent
{
    public static (string Title, string Content) Generate(CrisisScope scope, Random random) =>
        Compose(scope == CrisisScope.Global ? Global : Local, random);

    private static (string Title, string Content) Compose(Pool pool, Random random)
    {
        var a = pool.A[random.Next(pool.A.Length)];
        var b = pool.B[random.Next(pool.B.Length)];
        var template = pool.Templates[random.Next(pool.Templates.Length)];

        return (
            Capitalize(Fill(template.Title, a, b)),
            Capitalize(Fill(template.Content, a, b)));
    }

    private static string Fill(string template, string a, string b) =>
        template.Replace("{a}", a).Replace("{b}", b);

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private sealed record Template(string Title, string Content);

    private sealed record Pool(string[] A, string[] B, Template[] Templates);

    private static readonly Pool Local =
        new(
            [
                "a sudden supply shock", "a wave of factory shutdowns", "a regional credit squeeze",
                "a key supplier's collapse", "a damaging safety recall", "a logistics breakdown",
                "a regulatory crackdown", "a labor strike", "a cyberattack on operators",
                "a burst of profit warnings",
            ],
            [
                "rattled several sectors", "sent a handful of industries reeling", "hit a cluster of firms hard",
                "spooked investors across a few sectors", "froze deal-making in the affected industries",
                "triggered a sharp sell-off in the sector",
            ],
            [
                new("{a} {b}",
                    "Traders dumped shares after {a} {b} this cycle. Analysts expect the damage to stay contained, but the affected names took a heavy hit."),
                new("Sector shock: {a} {b}",
                    "{a} {b} as the session opened. Desks scrambled to reprice exposure while the worst-hit industries slid."),
                new("Sell-off as {a} hits the tape",
                    "Word that {a} {b} sent prices tumbling. The move was concentrated, but brutal where it landed."),
            ]);

    private static readonly Pool Global =
        new(
            [
                "a global recession scare", "a worldwide credit crunch", "a cascading banking panic",
                "a collapse in global demand", "a sovereign-debt shock", "a sweeping liquidity freeze",
                "a worldwide trade war", "a systemic market meltdown",
            ],
            [
                "sent markets tumbling worldwide", "wiped value off nearly every sector",
                "triggered a broad, indiscriminate sell-off", "drove a market-wide flight to safety",
                "hammered industries across the board", "sparked panic on every exchange",
            ],
            [
                new("{a} {b}",
                    "Fear gripped the market as {a} {b}. The decline was broad and deep, with few sectors left untouched."),
                new("Markets in freefall: {a}",
                    "{a} {b} in one of the worst sessions on record. Investors fled risk as losses spread across the board."),
                new("Global rout as {a} unfolds",
                    "{a} {b} this cycle. The sell-off spared almost nothing, and the damage was felt everywhere at once."),
            ]);
}
