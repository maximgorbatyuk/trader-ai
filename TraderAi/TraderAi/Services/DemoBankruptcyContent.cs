using System.Globalization;

namespace TraderAi.Services;

// Generates a newswire headline and body for a trader's bankruptcy, filling the trader's name and the
// headline figures into a curated template. Drawn from a caller-supplied Random so output is reproducible
// for a given seed.
internal static class DemoBankruptcyContent
{
    public static (string Title, string Content) Generate(string name, decimal cashLost, decimal shareWorth, Random random)
    {
        var template = Templates[random.Next(Templates.Length)];
        var lost = Money(cashLost);
        var worth = Money(shareWorth);

        return (
            Fill(template.Title, name, lost, worth),
            Fill(template.Content, name, lost, worth));
    }

    private static string Fill(string template, string name, string lost, string worth) =>
        template.Replace("{name}", name).Replace("{lost}", lost).Replace("{worth}", worth);

    private static string Money(decimal value) =>
        value.ToString("C0", CultureInfo.GetCultureInfo("en-US"));

    private sealed record Template(string Title, string Content);

    private static readonly Template[] Templates =
    [
        new("{name} files for bankruptcy",
            "{name} has gone bust, wiping out {lost} in cash. The trader is now dumping a holding worth about {worth} to settle up."),
        new("Trader {name} goes under",
            "After a brutal run, {name} is bankrupt: {lost} in cash gone and roughly {worth} in shares headed for a fire sale."),
        new("{name} collapses, {lost} wiped out",
            "The desk of {name} has collapsed. Cash reserves of {lost} are gone and a {worth} share position is being liquidated into the market."),
    ];
}
