namespace TraderAi.Services;

public sealed class ArchiveOptions
{
    public const string SectionName = "Archive";

    // Opt-out: aged rows are moved to the archive tables unless this is set to false.
    public bool Enabled { get; set; } = true;

    // Rows whose cycle is older than this many cycles behind the current one are moved out of the live
    // tables so the working set the cycle reads stays small. Must exceed any look-back the live logic
    // does (long-range price window, auditor history, recent-dividend window), which are all well under this.
    public int RetentionCycles { get; set; } = 500;
}
