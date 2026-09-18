namespace SmTagger.Shared;

/// <summary>Derives the application's inert mail workspace inside the selected SmarterMail Proc directory.</summary>
public static class MailQueuePaths
{
    // Keep working and retained mail with the spool rather than with sender configuration and mappings.
    public static string WorkDirectory(string spoolDirectory) => Path.Combine(spoolDirectory, "proc", "sm-tagger");

    // Locate the flat sorter-to-tagger queue and all tagger message-state files.
    public static string ProcessDirectory(string spoolDirectory) => Path.Combine(WorkDirectory(spoolDirectory), "process");

    // Locate inert upstream-failure retention beside the processing queue.
    public static string FailedDirectory(string spoolDirectory) => Path.Combine(WorkDirectory(spoolDirectory), "failed");

    // Serialize taggers sharing this mail queue even when they were given different data roots.
    public static string TaggerLockPath(string spoolDirectory) => Path.Combine(WorkDirectory(spoolDirectory), "sm-tagger.lock");
}
