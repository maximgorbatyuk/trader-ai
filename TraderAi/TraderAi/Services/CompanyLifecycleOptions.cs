namespace TraderAi.Services;

public sealed class CompanyLifecycleOptions
{
    public const string SectionName = "CompanyLifecycle";

    // Opt-in: new-company appearance and delisting stay off unless this is set to true.
    public bool Enabled { get; set; }
}
