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
        "Aria", "Emil", "Nour", "Dario", "Selin", "Arjun", "Tuva", "Rowan", "Ines", "Malik",
        "Saoirse", "Enzo", "Amina", "Viktor", "Lucia", "Tomas", "Farah", "Anders", "Marisol", "Kenji",
        "Beatriz", "Rafael", "Suki", "Andre", "Talia", "Emre", "Cora", "Ismael", "Greta", "Santiago",
        "Layla", "Bjorn", "Renata", "Youssef", "Astrid", "Matthias", "Noor", "Cyrus", "Elin", "Adrian",
        "Zoya", "Gael", "Simone", "Hassan", "Petra", "Leon", "Aylin", "Mattia", "Dalia", "Kofi",
        "Rosa", "Nikolai", "Yuki", "Adele", "Rina", "Clara", "Ibrahim", "Signe", "Pablo", "Meera",
        "Anton", "Salma", "Gustavo", "Isolde", "Karim", "Vera", "Milos", "Fatima", "Bruno", "Sana",
        "Otto", "Damaris", "Rasmus", "Nia", "Sergio", "Halima", "Lorenzo", "Ayla", "Viktoria", "Emeka",
        "Paloma", "Dmitri", "Aiko", "Callum", "Rania", "Mikael", "Lena", "Fabian", "Zeynep", "Andres",
        "Sinead", "Marco", "Ophelia", "Tobias", "Amani", "Gianna", "Reza", "Elsa", "Mohan", "Ivana",
        "Kian", "Delphine", "Arne", "Rekha", "Pedro", "Ludmila", "Samir", "Nele", "Cristina", "Aleksander",
        "Fatou", "Henrik", "Lior", "Marta", "Nabil", "Ruth", "Stavros", "Amelie", "Vikram", "Bonnie",
        "Osman", "Frida", "Leonardo", "Jamila", "Piotr", "Suraya", "Aksel", "Camille", "Devon", "Rocio",
        "Hamza", "Inga", "Teodoro", "Nazanin", "Levi", "Sofie", "Amos", "Yasmin", "Curtis", "Elowen",
        "Bashir", "Alma", "Ronan", "Priyanka", "Sten", "Mira", "Dominic", "Anouk", "Ferdinand", "Neve",
        "Bilal", "Rosalind", "Kasper", "Divya", "Marek", "Liesel", "Efrain", "Tamsin", "Goran", "Adaora",
        "Mohammed", "Sylvie", "Ari", "Wren", "Basile", "Noelia", "Radek", "Imani", "Casimir", "Lucinda",
        "Halil", "Cecilia", "Bao", "Rhiannon", "Zoltan", "Marguerite", "Elias", "Tanvir", "Odette", "Magnus",
        "Soraya", "Emmett", "Linnea", "Darius", "Priti", "Aurelio", "Sanne", "Boris", "Chiara", "Nikhil",
        "Esther", "Milan", "Zainab", "Rufus", "Aurora", "Kwame", "Lidia", "Osvaldo", "Neriah", "Constance",
    ];

    private static readonly string[] LastNames =
    [
        "Marsh", "Okonkwo", "Vance", "Petrova", "Halloran", "Nakamura", "Reyes", "Castellano",
        "Bergström", "Adeyemi", "Whitfield", "Costa", "Larsen", "Mahmoud", "Sinclair", "Novak",
        "Fontaine", "Delgado", "Eskildsen", "Rahman", "Brennan", "Yamamoto", "Carvalho", "Hewitt",
        "Voss", "Mbeki", "Calloway", "Andersen", "Quintero", "Sato", "Pereira", "Lindqvist",
        "Haddad", "Ferro", "Osei", "Trent", "Bianchi", "Solberg", "Aziz", "Underwood",
        "Ashford", "Ba", "Beaumont", "Cardoso", "Dahl", "Escobar", "Farrell", "Gallardo", "Haas", "Ibarra",
        "Jorgensen", "Kaminski", "Lund", "Moreau", "Nilsson", "Ocampo", "Pahlavi", "Radcliffe", "Salinas", "Tanaka",
        "Ueda", "Valdez", "Wachowski", "Xu", "Yildirim", "Zambrano", "Abadi", "Boone", "Cortes", "Dupont",
        "Engström", "Fischer", "Gallo", "Hartman", "Ishikawa", "Jimenez", "Kowalski", "Lindgren", "Mancini", "Nawaz",
        "Okafor", "Petit", "Quaranta", "Rosales", "Sabatini", "Toledo", "Ustinov", "Verhoeven", "Wallace", "Yoon",
        "Zhao", "Alvarez", "Berger", "Chowdhury", "Diallo", "Ellison", "Fabbri", "Grimaldi", "Holt", "Iqbal",
        "Jain", "Klein", "Laurent", "Montoya", "Nystrom", "Ortega", "Popov", "Qureshi", "Rossi", "Serrano",
        "Tremblay", "Ulrich", "Vega", "Weiss", "Yamashita", "Zielinski", "Amir", "Bergman", "Contreras", "Drummond",
        "Espinoza", "Farooq", "Grover", "Hoffman", "Ivanov", "Jansen", "Kimura", "Lozano", "Meyer", "Nomura",
        "Obrien", "Palermo", "Quintana", "Reinhardt", "Sorensen", "Takahashi", "Ackerman", "Bhatt", "Castillo", "Dominguez",
        "Ekberg", "Fournier", "Galvez", "Hussain", "Iversen", "Jelani", "Kruger", "Lombardi", "Marchetti", "Nakashima",
        "Odom", "Pettersson", "Rios", "Schneider", "Thackeray", "Ueno", "Varga", "Whitaker", "Yusuf", "Zavala",
        "Ahmadi", "Bauer", "Cisneros", "Delacroix", "Emerson", "Fujimoto", "Gutierrez", "Halvorsen", "Ito", "Jovanovic",
        "Kang", "Leblanc", "Matsuda", "Nabhan", "Ohlsson", "Prasad", "Ramos", "Steiner", "Thorsen", "Uddin",
        "Vasquez", "Wagner", "Yakov", "Zetterberg", "Aoki", "Bergqvist", "Chaudhry", "Dubois", "Ericsson", "Fernandes",
        "Ghosh", "Hedlund", "Imran", "Juarez", "Karlsson", "Lindholm", "Mateus", "Nagata", "Osborne", "Petrescu",
        "Quijano", "Rasmussen", "Soto", "Timofeev", "Uzun", "Villanueva", "Weaver", "Yildiz", "Zamora", "Abbas",
        "Bergeron", "Cardenas", "Dietrich", "Enriquez", "Falconer", "Gonzalez", "Hamilton", "Isaksson", "Jha", "Kobayashi",
        "Larkin", "Medeiros", "Nakayama", "Oduya", "Paredes", "Rana", "Saito", "Tovar", "Urbina", "Valentin",
        "Wozniak", "Yamada", "Zurita", "Adebayo", "Blomqvist", "Chandra", "Duarte", "Engberg", "Farhadi", "Gallagher",
    ];

    private static readonly string[] CompanyRoots =
    [
        "Meridian", "Vantage", "Cobalt", "Northwind", "Halcyon", "Vertex", "Cinder", "Lumen",
        "Solstice", "Granite", "Aether", "Pinnacle", "Ironwood", "Helios", "Cascade", "Obsidian",
        "Sable", "Tessera", "Borealis", "Quill", "Marrow", "Ascend", "Kestrel", "Verdant",
        "Onyx", "Lattice", "Foundry", "Zephyr", "Argent", "Beacon", "Crucible", "Drayton",
        "Everline", "Falcon", "Glacier", "Hollow", "Juniper", "Keystone", "Lyric", "Monarch",
        "Aurora", "Basalt", "Citadel", "Dovetail", "Ember", "Fathom", "Gossamer", "Harbor", "Ivory", "Jetstream",
        "Kismet", "Lodestar", "Mistral", "Nimbus", "Oaken", "Prism", "Quarry", "Ridge", "Summit", "Talon",
        "Umbra", "Vellum", "Windward", "Xenon", "Yarrow", "Zenith", "Anchor", "Bramble", "Cirrus", "Dawn",
        "Ecliptic", "Ferrous", "Gantry", "Hearth", "Indigo", "Juno", "Kelvin", "Larkspur", "Mercer", "Nova",
        "Orion", "Palisade", "Quartz", "Redwood", "Sterling", "Thistle", "Ursa", "Vanguard", "Wren", "Yonder",
        "Zircon", "Alloy", "Bellwether", "Copperline", "Delta", "Estuary", "Flint", "Grove", "Halyard", "Isotope",
        "Jasper", "Kindling", "Lantern", "Meridiem", "Nautilus", "Oxbow", "Pallas", "Quorum", "Rampart", "Slate",
        "Tidewater", "Ulster", "Verge", "Wexford", "Yardarm", "Zealot", "Amber", "Brimstone", "Cardinal", "Draco",
        "Elmwood", "Foxglove", "Gable", "Heron", "Ironclad", "Jubilee", "Kirkwall", "Lyceum", "Mainmast", "Nettle",
        "Osprey", "Peregrine", "Quintain", "Rookery", "Saffron", "Tamarind", "Umbrel", "Vireo", "Warden", "Yewtree",
        "Zodiac", "Almanac", "Bastion", "Coriander", "Dune", "Everest", "Fjord", "Garnet", "Hawthorne", "Ignis",
        "Jade", "Kraken", "Loomis", "Magnetite", "Nomad", "Opaline", "Petrichor", "Quicksilver", "Ravensbourne", "Solace",
        "Tempest", "Umberton", "Valkyrie", "Whitethorn", "Yggdrasil", "Zenobia", "Ashcroft", "Brightwater", "Coldharbour", "Dashwood",
        "Ellery", "Fernbank", "Goldcrest", "Havenwood", "Ironbark", "Jessamine", "Kingfisher", "Lockridge", "Moorland", "Nighthawk",
        "Oakhurst", "Ptarmigan", "Quillon", "Ravenswood", "Stonegate", "Thornbury", "Ulfberht", "Vespers", "Windmere", "Yellowstone",
        "Zamorin", "Aldergrove", "Bracken", "Cormorant", "Duskwood", "Elderwood", "Ferncliff", "Goldbrook", "Hartfield", "Ironvale",
        "Junegrass", "Kingsley", "Lorekeep", "Marblehead", "Northgate", "Oakenshield", "Pinehaven", "Quarrystone", "Redcliff", "Silverthorn",
        "Trawler", "Umbermoor", "Voyager", "Westmark", "Yorkfield", "Ashvale", "Blackthorn", "Cindermere", "Dawnbreak", "Emberhold",
        "Frostpeak", "Goldenrod", "Highmoor", "Ironhelm", "Jetwing", "Kilnmore", "Lampwright", "Millstone", "Northreach", "Oldgate",
        "Pinewatch", "Quillmark", "Ravenhold", "Stormharbor", "Thornwood", "Umberdale", "Verdigris", "Windrose", "Yewbank", "Zircite",
    ];

    private static readonly string[] CompanySuffixes =
    [
        "Capital", "Holdings", "Industries", "Technologies", "Dynamics", "Logistics",
        "Partners", "Systems", "Resources", "Labs", "Group", "Ventures",
        "Trading", "Securities", "Enterprises", "Solutions", "Global", "Networks",
        "Analytics", "Trust", "Commodities", "Equity",
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

    // Returns one person name not already in takenNames, using exactly one random draw so a scripted Random
    // stays predictable (unlike PickPeople, whose shuffle burns one int per combo). From a random starting combo it
    // probes the first×last space in order; if every combo is taken it falls back to a numeric suffix.
    public static string PickOnePerson(Random random, IReadOnlySet<string> takenNames)
    {
        var combos = new List<string>(FirstNames.Length * LastNames.Length);
        foreach (var first in FirstNames)
        {
            foreach (var last in LastNames)
            {
                combos.Add($"{first} {last}");
            }
        }

        var start = random.Next(combos.Count);
        for (var offset = 0; offset < combos.Count; offset++)
        {
            var name = combos[(start + offset) % combos.Count];
            if (!takenNames.Contains(name))
            {
                return name;
            }
        }

        var baseName = combos[start];
        for (var wrap = 2; ; wrap++)
        {
            var name = $"{baseName} {wrap}";
            if (!takenNames.Contains(name))
            {
                return name;
            }
        }
    }

    // Returns one company name not already in takenNames, using exactly one random draw so a scripted Random stays
    // predictable (the PickOnePerson analogue for a company that appears mid-simulation). From a random starting
    // combo it probes the root×suffix space in order; if every combo is taken it falls back to a numeric suffix.
    public static string PickOneCompany(Random random, IReadOnlySet<string> takenNames)
    {
        var combos = new List<string>(CompanyRoots.Length * CompanySuffixes.Length);
        foreach (var root in CompanyRoots)
        {
            foreach (var suffix in CompanySuffixes)
            {
                combos.Add($"{root} {suffix}");
            }
        }

        var start = random.Next(combos.Count);
        for (var offset = 0; offset < combos.Count; offset++)
        {
            var name = combos[(start + offset) % combos.Count];
            if (!takenNames.Contains(name))
            {
                return name;
            }
        }

        var baseName = combos[start];
        for (var wrap = 2; ; wrap++)
        {
            var name = $"{baseName} {wrap}";
            if (!takenNames.Contains(name))
            {
                return name;
            }
        }
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
