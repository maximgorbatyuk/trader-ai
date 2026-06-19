namespace TraderAi.Services;

// Curated name pools for the demo seed so generated traders and companies read like real
// market participants rather than "Trader 01" / "Company 01". Names are drawn from the
// caller-supplied Random so the demo stays reproducible for a given seed.
internal static class DemoMarketNames
{
    private static readonly string[] FirstNames =
    [
        "Olivia", "Liam", "Amara", "Noah", "Sofia", "Mateo", "Priya", "Lucas", "Elena", "Kai",
        "Hana", "Diego", "Aisha", "Marcus", "Yara", "Ethan", "Ingrid", "Omar", "Chloe", "Ravi",
        "Naomi", "Felix", "Leila", "Hugo", "Mei", "Sebastian", "Zara", "Theo", "Anika", "Caleb",
        "Freya", "Tariq", "Camila", "Soren", "Nadia", "Julian", "Esme", "Idris", "Bianca", "Niko",
    ];

    private static readonly string[] LastNames =
    [
        "Marsh", "Okonkwo", "Vance", "Petrova", "Halloran", "Nakamura", "Reyes", "Castellano",
        "Bergström", "Adeyemi", "Whitfield", "Costa", "Larsen", "Mahmoud", "Sinclair", "Novak",
        "Fontaine", "Delgado", "Eskildsen", "Rahman", "Brennan", "Yamamoto", "Carvalho", "Hewitt",
        "Voss", "Mbeki", "Calloway", "Andersen", "Quintero", "Sato", "Pereira", "Lindqvist",
        "Haddad", "Ferro", "Osei", "Trent", "Bianchi", "Solberg", "Aziz", "Underwood",
    ];

    private static readonly string[] CompanyRoots =
    [
        "Meridian", "Vantage", "Cobalt", "Northwind", "Halcyon", "Vertex", "Cinder", "Lumen",
        "Solstice", "Granite", "Aether", "Pinnacle", "Ironwood", "Helios", "Cascade", "Obsidian",
        "Sable", "Tessera", "Borealis", "Quill", "Marrow", "Ascend", "Kestrel", "Verdant",
        "Onyx", "Lattice", "Foundry", "Zephyr", "Argent", "Beacon", "Crucible", "Drayton",
        "Everline", "Falcon", "Glacier", "Hollow", "Juniper", "Keystone", "Lyric", "Monarch",
    ];

    private static readonly string[] CompanySuffixes =
    [
        "Capital", "Holdings", "Industries", "Technologies", "Dynamics", "Logistics",
        "Partners", "Systems", "Resources", "Labs", "Group", "Ventures",
    ];

    public static string[] PickPeople(int count, Random random)
    {
        var combos = new List<string>(FirstNames.Length * LastNames.Length);
        foreach (var first in FirstNames)
        {
            foreach (var last in LastNames)
            {
                combos.Add($"{first} {last}");
            }
        }

        return PickUnique(combos, count, random);
    }

    public static string[] PickCompanies(int count, Random random)
    {
        var combos = new List<string>(CompanyRoots.Length * CompanySuffixes.Length);
        foreach (var root in CompanyRoots)
        {
            foreach (var suffix in CompanySuffixes)
            {
                combos.Add($"{root} {suffix}");
            }
        }

        return PickUnique(combos, count, random);
    }

    // Returns count distinct names in a shuffled order. If more names are requested than the
    // pool can produce, the surplus repeats the pool with a numeric suffix to stay unique.
    private static string[] PickUnique(List<string> candidates, int count, Random random)
    {
        Shuffle(candidates, random);

        var result = new string[count];
        for (var index = 0; index < count; index++)
        {
            var name = candidates[index % candidates.Count];
            var wrap = index / candidates.Count;
            result[index] = wrap == 0 ? name : $"{name} {wrap + 1}";
        }

        return result;
    }

    private static void Shuffle(List<string> items, Random random)
    {
        for (var index = items.Count - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (items[index], items[swap]) = (items[swap], items[index]);
        }
    }
}
