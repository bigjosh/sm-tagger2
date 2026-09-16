namespace SmTagger.Shared;

public enum MessageOutcome
{
    Succeeded,
    Failed,
    Stale
}

public sealed class FatalProcessingException : Exception
{
    // Preserve the failed process boundary and the operation that caused it.
    public FatalProcessingException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed record Invocation(
    string DataDirectory,
    string SpoolDirectory,
    string? Basename,
    bool Log = false,
    bool Keep = false)
{
    public const string SorterUsage = """
        Usage: sm-sorter.exe <datadir> <spooldir> [<basename>]

          <datadir>   Data root containing the senders directory.
          <spooldir>  SmarterMail spool root, not its proc subdirectory.
          <basename> Optional message filename without .hdr or .eml; process that pair and exit.
                     Omit it to watch <spooldir>\proc continuously.

        Examples (PowerShell; quote paths containing spaces):
          .\sm-sorter.exe "D:\Tagger Data" "D:\SmarterMail\Spool"
          .\sm-sorter.exe "D:\Tagger Data" "D:\SmarterMail\Spool" message123
        """;

    public const string TaggerUsage = """
        Usage: sm-tagger.exe <datadir> [-log] [-keep] <spooldir> [<basename>]

          <datadir>   Data root containing senders, profiles, and the process queue.
          <spooldir>  SmarterMail spool root where completed messages are returned.
          <basename> Optional message filename without .hdr or .eml; process that pair and exit.
                     Omit it to watch <datadir>\process continuously.
          -log       Write the best-effort execution trace to <datadir>\log.txt.
          -keep      Retain original and output copies in <datadir>\process.
                     Put these options immediately after <datadir>, before <spooldir>.

        Examples (PowerShell; quote paths containing spaces):
          .\sm-tagger.exe "D:\Tagger Data" -log "D:\SmarterMail\Spool"
          .\sm-tagger.exe "D:\Tagger Data" -log -keep "D:\SmarterMail\Spool" message123
        """;

    public bool IsWatchMode => Basename is null;

    // Parse the sorter's positional arguments without interpreting basename hyphens as options.
    public static Invocation ParseSorter(string[] arguments)
    {
        if (arguments.Length is not (2 or 3))
        {
            throw new ArgumentException("Expected datadir and spooldir, with an optional basename (2 or 3 arguments).");
        }

        return Create(arguments[0], arguments[1], arguments.Length == 3 ? arguments[2] : null, false, false);
    }

    // Recognize tagger flags only between the datadir and spooldir positional arguments.
    public static Invocation ParseTagger(string[] arguments)
    {
        if (arguments.Length < 2)
        {
            throw new ArgumentException("Expected at least datadir and spooldir.");
        }

        bool log = false;
        bool keep = false;
        int index = 1;
        while (index < arguments.Length && arguments[index] is "-log" or "-keep")
        {
            if (arguments[index] == "-log")
            {
                if (log)
                {
                    throw new ArgumentException("The -log option may be supplied only once.");
                }

                log = true;
            }
            else
            {
                if (keep)
                {
                    throw new ArgumentException("The -keep option may be supplied only once.");
                }

                keep = true;
            }

            index++;
        }

        if (arguments.Length - index is not (1 or 2))
        {
            throw new ArgumentException("Expected spooldir and an optional basename after datadir and any -log/-keep options.");
        }

        return Create(arguments[0], arguments[index], arguments.Length - index == 2 ? arguments[index + 1] : null, log, keep);
    }

    // Validate invocation boundaries before any queue or singleton file is touched.
    private static Invocation Create(string dataDirectory, string spoolDirectory, string? basename, bool log, bool keep)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolDirectory);
        if (basename is not null && !WindowsNames.IsUsableComponent(basename))
        {
            throw new ArgumentException("The basename must be one usable literal Windows filename component.", nameof(basename));
        }

        return new Invocation(Path.GetFullPath(dataDirectory), Path.GetFullPath(spoolDirectory), basename, log, keep);
    }
}
