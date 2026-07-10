using TraderAi.Models;

namespace TraderAi.Services;

// Generates random but meaningful, themed news headlines and bodies by filling two slots ({a}, {b}) of a
// per-theme template from curated fragment pools. The themes (UFO, wildlife, fantasy, celebrity, weather,
// and more) combine into well over two hundred distinct, readable items so a long-running market rarely
// repeats. Drawn from a caller-supplied Random so output is reproducible for a given seed.
internal static class DemoNewsContent
{
    // Theme keys and human labels, in display order, for the manual news form.
    public static IReadOnlyList<(string Key, string Label)> ThemeOptions =>
        Themes.Select(theme => (theme.Key, theme.Label)).ToArray();

    public static IReadOnlyList<(string Key, string Label)> ScopedThemeOptions =>
        FinanceThemes.Select(theme => (theme.Key, theme.Label)).ToArray();

    public static (string Title, string Content) Generate(Random random) =>
        Compose(Themes[random.Next(Themes.Length)], random);

    // Generates from the named theme, or null when the key is unknown.
    public static (string Title, string Content)? GenerateForTheme(string key, Random random)
    {
        var theme = Themes.FirstOrDefault(candidate => candidate.Key == key);
        return theme is null ? null : Compose(theme, random);
    }

    public static (string Title, string Content)? GenerateForScopedTheme(
        string key, NewsImpactDirection direction, Random random)
    {
        var theme = FinanceThemes.FirstOrDefault(candidate => candidate.Key == key);
        return theme is null ? null : Compose(theme, direction, random);
    }

    public static (string Title, string Content) GenerateForScopedDirection(
        NewsImpactDirection direction, Random random) =>
        Compose(FinanceThemes[random.Next(FinanceThemes.Length)], direction, random);

    private static (string Title, string Content) Compose(Theme theme, Random random)
    {
        var a = theme.A[random.Next(theme.A.Length)];
        var b = theme.B[random.Next(theme.B.Length)];
        var template = theme.Templates[random.Next(theme.Templates.Length)];

        return (
            Capitalize(Fill(template.Title, a, b)),
            Capitalize(Fill(template.Content, a, b)));
    }

    private static (string Title, string Content) Compose(
        FinanceTheme theme, NewsImpactDirection direction, Random random)
    {
        var templates = direction == NewsImpactDirection.Increase
            ? theme.IncreaseTemplates
            : theme.DecreaseTemplates;
        var template = templates[random.Next(templates.Length)];

        return (Capitalize(template.Title), Capitalize(template.Content));
    }

    private static string Fill(string template, string a, string b) =>
        template.Replace("{a}", a).Replace("{b}", b);

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private sealed record Template(string Title, string Content);

    private sealed record Theme(string Key, string Label, string[] A, string[] B, Template[] Templates);

    private sealed record FinanceTheme(
        string Key,
        string Label,
        Template[] IncreaseTemplates,
        Template[] DecreaseTemplates);

    private static readonly FinanceTheme[] FinanceThemes =
    [
        new(
            "market-sentiment",
            "Market sentiment",
            [
                new(
                    "Market sentiment rally gathers momentum",
                    "An upbeat outlook and analyst upgrade have renewed confidence, bringing sector inflows and risk-on demand."),
                new(
                    "Improving sentiment lifts sector momentum",
                    "Strong expectations and renewed confidence are supporting a broad market rally with clear tailwinds."),
            ],
            [
                new(
                    "Market sentiment selloff deepens",
                    "A downgrade and gloomy outlook have soured confidence, driving sector outflows and risk-off positioning."),
                new(
                    "Souring sentiment triggers fresh jitters",
                    "Missed expectations and a profit warning are creating headwinds as investors brace for a wider selloff."),
            ]),
    ];

    private static readonly Theme[] Themes =
    [
        // UFO / aliens
        new(
            "ufo",
            "UFO & Aliens",
            [
                "a fleet of silver discs", "a glowing orb", "three triangular craft", "a humming green light",
                "a saucer the size of a stadium", "a swarm of tiny drones", "a pulsing purple beam",
                "a translucent jellyfish-shaped craft",
            ],
            [
                "over the harbor", "above a quiet farm town", "near the airport", "during the county fair",
                "above the financial district", "over the desert highway", "near the old lighthouse",
                "above the football stadium",
            ],
            [
                new("UFO sighting: {a} spotted {b}",
                    "Hundreds of witnesses say {a} hovered {b} for several minutes before vanishing without a trace. Officials urge calm while investigators review the footage."),
                new("Mystery in the sky as {a} appears {b}",
                    "Local residents reported {a} {b} late last night. Aviation authorities say no aircraft were scheduled in the area."),
                new("Did aliens visit? {a} caught on camera {b}",
                    "A shaky phone video of {a} {b} has gone viral overnight. Skeptics blame weather balloons; believers are convinced otherwise."),
            ]),

        // Animals / wildlife
        new(
            "wildlife",
            "Wildlife",
            [
                "a runaway emu", "a family of raccoons", "an escaped python", "a herd of goats",
                "a clever octopus", "a lost penguin", "a giant catfish", "a parade of ducks", "a confused moose",
            ],
            [
                "shut down a downtown intersection", "moved into an abandoned mall", "outsmarted the local zookeepers",
                "took over a suburban backyard", "wandered into a coffee shop", "led police on a slow-speed chase",
                "became the town's unofficial mascot", "raided a bakery before dawn",
            ],
            [
                new("{a} {b}",
                    "Residents could hardly believe their eyes when {a} {b} this morning. Animal control says no one was hurt and the situation is now under control."),
                new("Caught on camera: {a} that {b}",
                    "The story of {a} that {b} has charmed the internet. Experts say such behavior, while rare, is not unheard of."),
            ]),

        // Hobbits / fantasy
        new(
            "fantasy",
            "Fantasy & Hobbits",
            [
                "a band of hobbits", "a grumpy dwarf", "an elven choir", "a wandering wizard",
                "a hungry troll", "a retired dragon", "a society of garden gnomes", "a talking badger",
            ],
            [
                "opened a second-breakfast café", "filed a complaint about loud festivities",
                "claimed an ancient map was a forgery", "challenged the mayor to a riddle contest",
                "started selling enchanted umbrellas", "demanded better roads through the Shire",
                "hosted an unexpected party",
            ],
            [
                new("{a} {b}",
                    "In news stranger than fiction, {a} reportedly {b} this week. Witnesses describe the scene as oddly delightful."),
                new("From the Shire: {a} {b}",
                    "Word from the countryside is that {a} {b}. Elders say it is the most excitement the village has seen in an age."),
            ]),

        // Celebrity / pop culture
        new(
            "celebrity",
            "Celebrity",
            [
                "a reclusive pop star", "a beloved chef", "an aging rock band", "a viral dance influencer",
                "a famous novelist", "an A-list actor", "a retired astronaut",
            ],
            [
                "announced a surprise comeback tour", "opened a tiny noodle stand", "adopted twelve rescue cats",
                "swapped fame for a quiet farm life", "launched a line of glow-in-the-dark sneakers",
                "live-streamed a 24-hour bread-baking marathon",
            ],
            [
                new("{a} {b}",
                    "Fans went wild after {a} {b}. Reaction online has been swift, loud, and largely affectionate."),
                new("Surprise: {a} {b}",
                    "No one saw it coming when {a} {b}. Insiders hint there may be more announcements to follow."),
            ]),

        // Weather / nature
        new(
            "weather",
            "Weather",
            [
                "a freak hailstorm", "a double rainbow", "an unseasonal heatwave", "a thick blanket of fog",
                "a sudden meteor shower", "a record-breaking downpour", "an early frost",
            ],
            [
                "blanketed the valley overnight", "stopped traffic for hours", "drew crowds to the waterfront",
                "delighted stargazers across the region", "caught commuters by surprise",
                "painted the evening sky orange",
            ],
            [
                new("{a} {b}",
                    "Forecasters were stunned as {a} {b}. Meteorologists say conditions should return to normal within a day or two."),
            ]),

        // Science / tech (light)
        new(
            "science",
            "Science & Tech",
            [
                "a backyard inventor", "a team of students", "a small startup", "a curious retiree", "a university lab",
            ],
            [
                "built a solar-powered toaster", "trained a parrot to read the news", "3D-printed an entire violin",
                "taught a robot to fold laundry", "grew tomatoes the size of melons",
                "designed a bicycle that runs on music",
            ],
            [
                new("{a} {b}",
                    "Curiosity paid off after {a} {b}. The project has since attracted a flurry of online attention and a few skeptical eyebrows."),
            ]),

        // Sports / oddity
        new(
            "sports",
            "Sports",
            [
                "an amateur chess club", "a local underdog team", "a 90-year-old marathoner",
                "a pet-friendly bowling league", "a one-armed darts champion",
            ],
            [
                "stunned the reigning champions", "set an unlikely world record", "finished the race in costume",
                "won on a last-second trick shot", "qualified for the national finals",
            ],
            [
                new("{a} {b}",
                    "Against all odds, {a} {b} over the weekend. Spectators called it one of the most unexpected results in years."),
            ]),

        // Food / local
        new(
            "food",
            "Food",
            [
                "a tiny bakery", "a roadside diner", "a school cafeteria", "a street-food vendor",
                "a centuries-old brewery",
            ],
            [
                "unveiled a mile-long sandwich", "ran out of pancakes in record time",
                "invented a chili-chocolate ice cream", "served its millionth customer",
                "won a surprise national award",
            ],
            [
                new("{a} {b}",
                    "Lines stretched around the block after {a} {b}. Regulars say the hype is, for once, completely justified."),
            ]),

        // Quirky business
        new(
            "business",
            "Business",
            [
                "an anonymous collector", "a group of pensioners", "a teenage entrepreneur",
                "a small island nation", "a co-op of beekeepers",
            ],
            [
                "cornered the market on rare buttons", "turned a hobby into a fortune",
                "bought every umbrella in town before the rains", "swapped cash for a barter economy",
                "made a small fortune trading vintage lunchboxes",
            ],
            [
                new("{a} {b}",
                    "Analysts are scratching their heads after {a} {b}. Whether it is genius or luck, the numbers are hard to argue with."),
            ]),

        // Bizarre / mystery
        new(
            "mystery",
            "Mystery",
            [
                "a mysterious statue", "an unsigned letter", "a glowing rock", "an antique clock",
                "a locked wooden box",
            ],
            [
                "appeared overnight in the town square", "predicted the weather with eerie accuracy",
                "drew treasure hunters from three counties", "started chiming at exactly midnight",
                "turned out to be older than the town itself",
            ],
            [
                new("{a} {b}",
                    "The whole town is talking after {a} {b}. No one has stepped forward to explain it, and the speculation only grows."),
            ]),
    ];
}
