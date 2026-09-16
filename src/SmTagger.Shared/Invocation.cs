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
    public bool IsWatchMode => Basename is null;

    // Parse the sorter's positional arguments without interpreting basename hyphens as options.
    public static Invocation ParseSorter(string[] arguments)
    {
        if (arguments.Length is not (2 or 3))
        {
            throw new ArgumentException("Usage: sm-sorter.exe <datadir> <spooldir> [<basename>]");
        }

        return Create(arguments[0], arguments[1], arguments.Length == 3 ? arguments[2] : null, false, false);
    }

    // Recognize tagger flags only between the datadir and spooldir positional arguments.
    public static Invocation ParseTagger(string[] arguments)
    {
        if (arguments.Length < 2)
        {
            throw new ArgumentException("Usage: sm-tagger.exe <datadir> [-log] [-keep] <spooldir> [<basename>]");
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
            throw new ArgumentException("Usage: sm-tagger.exe <datadir> [-log] [-keep] <spooldir> [<basename>]");
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
