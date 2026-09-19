namespace SmTagger.Shared;

public enum MessageOutcome
{
    Succeeded,
    Failed,
    Stale,
    Deferred
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
    bool Keep = false,
    string? LogPath = null,
    bool Verbose = false,
    bool Concise = false)
{
    public const string SorterUsage = """
        Usage: sm-sorter.exe <datadir> [-l <logfile>] [-v] [-c] <spooldir> [<basename>]

          <datadir>   Data root containing senders\auth-addresses (may be empty).
          <spooldir>  SmarterMail spool root, not its proc subdirectory.
          <basename> Optional message filename without .hdr or .eml; process that pair and exit.
                     Omit it to watch <spooldir>\proc continuously.
          -l <logfile> Append one best-effort result line per message to this file.
          -v         Print verbose routing and file-operation diagnostics to stdout.
          -c         Print startup details and one concise stdout line per completed pair move.
                     Includes failed retention; startup shows version, mode, data and spool paths.
                     -c and -v are independent; specifying both prints both. Neither changes -l.
                     Put options immediately after <datadir>, before <spooldir>.
                     Use a dedicated log file outside mail queues and configuration records.

        Examples (PowerShell; quote paths containing spaces):
          .\sm-sorter.exe "D:\Tagger Data" -l "D:\Logs\sorter.log" -v "D:\SmarterMail\Spool"
          .\sm-sorter.exe "D:\Tagger Data" "D:\SmarterMail\Spool" message123
        """;

    public const string TaggerUsage = """
        Usage: sm-tagger.exe <datadir> [-l <logfile>] [-v] [-c] [-keep] <spooldir> [<basename>]

          <datadir>   Data root containing sender configuration and permanent tag mappings.
          <spooldir>  SmarterMail spool root where completed messages are returned.
          <basename> Optional message filename without .hdr or .eml; process that pair and exit.
                     Omit it to watch <spooldir>\proc\sm-tagger\process continuously.
          -l <logfile> Append the best-effort detailed execution trace to this file.
          -v         Print verbose execution-trace diagnostics to stdout.
          -c         Print startup details, loaded sender/tag counts, and one line per output pair.
                     Startup shows version, mode, data and spool paths.
                     -c and -v are independent; specifying both prints both. Neither changes -l.
          -keep      Retain original and output copies in <spooldir>\proc\sm-tagger\process.
                     Put these options immediately after <datadir>, before <spooldir>.
                     Use a dedicated log file outside mail queues and configuration records.

        Examples (PowerShell; quote paths containing spaces):
          .\sm-tagger.exe "D:\Tagger Data" -l "D:\Logs\tagger.log" -v "D:\SmarterMail\Spool"
          .\sm-tagger.exe "D:\Tagger Data" -v -keep "D:\SmarterMail\Spool" message123
        """;

    public bool IsWatchMode => Basename is null;

    // Recognize sorter diagnostics options before spooldir while preserving literal basename hyphens.
    public static Invocation ParseSorter(string[] arguments)
    {
        if (arguments.Length < 2)
        {
            throw new ArgumentException("Expected at least datadir and spooldir.");
        }

        string? logPath = null;
        bool verbose = false;
        bool concise = false;
        int index = 1;
        while (index < arguments.Length && arguments[index] is "-l" or "-v" or "-c")
        {
            if (arguments[index] == "-l")
            {
                if (logPath is not null)
                    throw new ArgumentException("The -l option may be supplied only once.");
                if (++index == arguments.Length || arguments[index] is "-l" or "-v" or "-c")
                    throw new ArgumentException("The -l option requires a logfile path.");
                logPath = arguments[index];
                ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
            }
            else if (arguments[index] == "-v")
            {
                if (verbose)
                    throw new ArgumentException("The -v option may be supplied only once.");
                verbose = true;
            }
            else
            {
                if (concise)
                    throw new ArgumentException("The -c option may be supplied only once.");
                concise = true;
            }

            index++;
        }

        if (arguments.Length - index is not (1 or 2))
            throw new ArgumentException("Expected spooldir and an optional basename after datadir and any -l/-v/-c options.");

        return Create(arguments[0], arguments[index], arguments.Length - index == 2 ? arguments[index + 1] : null,
            false, logPath, verbose, concise);
    }

    // Recognize explicit tagger diagnostics and retention options before spooldir, rejecting the removed flag.
    public static Invocation ParseTagger(string[] arguments)
    {
        if (arguments.Length < 2)
        {
            throw new ArgumentException("Expected at least datadir and spooldir.");
        }

        string? logPath = null;
        bool verbose = false;
        bool concise = false;
        bool keep = false;
        int index = 1;
        while (index < arguments.Length && arguments[index] is "-l" or "-v" or "-c" or "-keep" or "-log")
        {
            if (arguments[index] == "-log")
                throw new ArgumentException("The -log option was replaced by -l <logfile>; use -v for console diagnostics.");

            if (arguments[index] == "-l")
            {
                if (logPath is not null)
                    throw new ArgumentException("The -l option may be supplied only once.");
                if (++index == arguments.Length || arguments[index] is "-l" or "-v" or "-c" or "-keep" or "-log")
                    throw new ArgumentException("The -l option requires a logfile path.");
                logPath = arguments[index];
                ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
            }
            else if (arguments[index] == "-v")
            {
                if (verbose)
                    throw new ArgumentException("The -v option may be supplied only once.");
                verbose = true;
            }
            else if (arguments[index] == "-c")
            {
                if (concise)
                    throw new ArgumentException("The -c option may be supplied only once.");
                concise = true;
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
            throw new ArgumentException("Expected spooldir and an optional basename after datadir and any -l/-v/-c/-keep options.");
        }

        return Create(arguments[0], arguments[index], arguments.Length - index == 2 ? arguments[index + 1] : null,
            keep, logPath, verbose, concise);
    }

    // Validate invocation boundaries before any queue or singleton file is touched.
    private static Invocation Create(string dataDirectory, string spoolDirectory, string? basename, bool keep,
        string? logPath = null, bool verbose = false, bool concise = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolDirectory);
        if (basename is not null && !WindowsNames.IsUsableComponent(basename))
        {
            throw new ArgumentException("The basename must be one usable literal Windows filename component.", nameof(basename));
        }

        return new Invocation(Path.GetFullPath(dataDirectory), Path.GetFullPath(spoolDirectory), basename, keep,
            logPath is null ? null : Path.GetFullPath(logPath), verbose, concise);
    }
}
