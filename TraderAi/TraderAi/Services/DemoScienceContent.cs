namespace TraderAi.Services;

// Generates random but meaningful science-investigation headlines and bodies, filling two slots ({a}, {b})
// of a template from curated fragment pools. Every event is positive and reads as a breakthrough lifting the
// affected sectors. Drawn from a caller-supplied Random so output is reproducible for a given seed.
internal static class DemoScienceContent
{
    public static (string Title, string Content) Generate(Random random) => Compose(random);

    private static (string Title, string Content) Compose(Random random)
    {
        var a = A[random.Next(A.Length)];
        var b = B[random.Next(B.Length)];
        var template = Templates[random.Next(Templates.Length)];

        return (
            Capitalize(Fill(template.Title, a, b)),
            Capitalize(Fill(template.Content, a, b)));
    }

    private static string Fill(string template, string a, string b) =>
        template.Replace("{a}", a).Replace("{b}", b);

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private sealed record Template(string Title, string Content);

    private static readonly string[] A =
    [
        "a breakthrough study", "a landmark clinical trial", "a peer-reviewed discovery", "a major patent",
        "a fusion milestone", "a new materials discovery", "a government research grant", "a university spinout",
        "a record efficiency gain", "a promising drug result",
    ];

    private static readonly string[] B =
    [
        "lifted hopes across several sectors", "drew fresh investment to the affected industries",
        "sparked a quiet rally in the sector", "brightened the outlook for the affected names",
        "sent optimism rippling through a handful of industries", "renewed confidence in the affected firms",
    ];

    private static readonly Template[] Templates =
    [
        new("{a} {b}",
            "Traders piled in after {a} {b} this cycle. Analysts see room to run, and the affected names led the tape higher."),
        new("Sector lift: {a} {b}",
            "{a} {b} as the session opened. Desks rushed to add exposure while the brightest-hit industries climbed."),
        new("Rally as {a} hits the tape",
            "Word that {a} {b} sent prices climbing. The move was concentrated, but strong where it landed."),
    ];
}
