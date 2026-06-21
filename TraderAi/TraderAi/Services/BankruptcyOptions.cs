namespace TraderAi.Services;

public sealed class BankruptcyOptions
{
    public const string SectionName = "Bankruptcy";

    // Opt-in: bankruptcies stay off unless this is set to true.
    public bool Enabled { get; set; }
}
