using System.Globalization;
using System.Text;
using SmTagger.Shared;

namespace SmSorter;

/// <summary>Independent best-effort sorter result logging and optional console diagnostics.</summary>
public sealed class SorterDiagnostics : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string? logPath;
    private readonly TextWriter stdout;
    private readonly TextWriter stderr;
    private readonly Func<DateTimeOffset> clock;
    private Stream? stream;
    private bool verbose;

    // Bind diagnostic destinations without performing filesystem or console operations.
    private SorterDiagnostics(string? logPath, bool verbose, TextWriter stdout, TextWriter stderr,
        Func<DateTimeOffset> clock)
    {
        this.logPath = logPath;
        this.verbose = verbose;
        this.stdout = stdout;
        this.stderr = stderr;
        this.clock = clock;
    }

    // Open only an explicitly requested append log; a failure leaves sorting and console diagnostics available.
    public static SorterDiagnostics Open(string? logPath, bool verbose, TextWriter? stdout = null, TextWriter? stderr = null) =>
        OpenCore(logPath, verbose, stdout ?? Console.Out, stderr ?? Console.Error, () => DateTimeOffset.UtcNow,
            path => new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));

    // Expose only internal clock and stream seams for deterministic diagnostic-failure tests.
    internal static SorterDiagnostics OpenForTesting(string? logPath, bool verbose, TextWriter stdout, TextWriter stderr,
        Func<DateTimeOffset> clock, Func<string, Stream> openLog) =>
        OpenCore(logPath, verbose, stdout, stderr, clock, openLog);

    // Attempt one file open without inspecting old log contents or creating missing parent directories.
    private static SorterDiagnostics OpenCore(string? logPath, bool verbose, TextWriter stdout, TextWriter stderr,
        Func<DateTimeOffset> clock, Func<string, Stream> openLog)
    {
        var diagnostics = new SorterDiagnostics(logPath, verbose, stdout, stderr, clock);
        if (logPath is not null)
        {
            try { diagnostics.stream = openLog(logPath); }
            catch (Exception error) { diagnostics.ReportLogFailure("open", error); }
        }
        return diagnostics;
    }

    // Attempt exactly one terminal record per selected message; never let diagnostics alter its outcome.
    public void Message(string basename, string result, string? auth, string reason, string operation,
        string hdr, string eml, string? destination = null, Exception? error = null)
    {
        if (stream is not null)
        {
            try { Append(FormatMessage(basename, result, auth, reason, operation, hdr, eml, destination, error)); }
            catch (Exception failure) { DisableLog("format message result", failure); }
        }
        if (verbose)
        {
            try { WriteConsole(FormatMessage(basename, result, auth, reason, operation, hdr, eml, destination, error)); }
            catch (Exception failure) { DisableConsole(failure); }
        }
    }

    // Describe lifecycle, scans, routing, and file operations only on the optional console channel.
    public void Debug(string basename, string eventName, params (string Name, object? Value)[] details)
    {
        if (!verbose)
            return;
        try
        {
            var line = new StringBuilder(Timestamp()).Append(" basename=").Append(ConsoleErrors.Quote(basename))
                .Append(" event=").Append(eventName);
            foreach ((string name, object? value) in details)
            {
                string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "-";
                line.Append(' ').Append(name).Append('=').Append(ConsoleErrors.Quote(text));
            }
            WriteConsole(line.ToString());
        }
        catch (Exception error) { DisableConsole(error); }
    }

    // Close the append handle without turning a logging error into a mail or process failure.
    public void Dispose()
    {
        Stream? acquired = stream;
        stream = null;
        if (acquired is null)
            return;
        try { acquired.Dispose(); }
        catch (Exception error) { ReportLogFailure("close", error); }
    }

    // Format an invariant UTC timestamp inside the caller's best-effort diagnostic boundary.
    private string Timestamp() => clock().ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    // Format each sink independently so a file-formatting failure cannot suppress available console output.
    private string FormatMessage(string basename, string result, string? auth, string reason, string operation,
        string hdr, string eml, string? destination, Exception? error) =>
        Timestamp() + " basename=" + ConsoleErrors.Quote(basename)
            + " result=" + result + " auth=" + ConsoleErrors.Quote(auth ?? "-")
            + " reason=" + ConsoleErrors.Quote(reason) + " operation=" + ConsoleErrors.Quote(operation)
            + " hdr=" + ConsoleErrors.Quote(hdr) + " eml=" + ConsoleErrors.Quote(eml)
            + " destination=" + ConsoleErrors.Quote(destination ?? "-")
            + " error=" + ConsoleErrors.Quote(error?.ToString() ?? "-");

    // Append and flush one UTF-8 CRLF record, preserving any bytes left by a failed write.
    private void Append(string line)
    {
        if (stream is null)
            return;
        try
        {
            byte[] bytes = StrictUtf8.GetBytes(line + "\r\n");
            stream.Write(bytes);
            stream.Flush();
        }
        catch (Exception error) { DisableLog("append message result", error); }
    }

    // Flush verbose output independently from the optional file log.
    private void WriteConsole(string line)
    {
        if (!verbose)
            return;
        try
        {
            stdout.WriteLine("DEBUG " + line);
            stdout.Flush();
        }
        catch (Exception error) { DisableConsole(error); }
    }

    // Detach a failed file sink permanently before reporting or disposing it, with no reopen or retry.
    private void DisableLog(string operation, Exception error)
    {
        Stream? failed = stream;
        stream = null;
        if (failed is null)
            return;
        ReportLogFailure(operation, error);
        try { failed.Dispose(); }
        catch (Exception closeError) { ReportLogFailure("close failed log", closeError); }
    }

    // Disable a failed verbose console while leaving the file log and mail processing independent.
    private void DisableConsole(Exception error)
    {
        if (!verbose)
            return;
        verbose = false;
        try { ConsoleErrors.Write(stderr, "ERROR writing sorter debug output: " + ConsoleErrors.FormatException(error)); }
        catch (Exception) { }
    }

    // Keep file-error reporting itself best effort and consistent with the searchable logging prefix.
    private void ReportLogFailure(string operation, Exception error)
    {
        try
        {
            ConsoleErrors.Write(stderr, "ERROR logging to " + ConsoleErrors.Quote(logPath ?? "-") + ": "
                + operation + " " + ConsoleErrors.FormatException(error));
        }
        catch (Exception) { }
    }
}
