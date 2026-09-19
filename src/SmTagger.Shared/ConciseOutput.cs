using System.Globalization;
using System.Reflection;
using System.Text;

namespace SmTagger.Shared;

/// <summary>Best-effort startup and completed-pair console summaries, independent of file logs and verbose traces.</summary>
public sealed class ConciseOutput
{
    private readonly TextWriter stdout;
    private readonly TextWriter stderr;
    private readonly Func<DateTimeOffset> clock;
    private bool enabled;

    // Bind an optional console sink without reading configuration or opening any files.
    public ConciseOutput(bool enabled, TextWriter? stdout = null, TextWriter? stderr = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.enabled = enabled;
        this.stdout = stdout ?? Console.Out;
        this.stderr = stderr ?? Console.Error;
        this.clock = clock ?? (() => DateTimeOffset.Now);
    }

    // Identify this invocation after its locks are held without claiming configuration or watcher readiness.
    public void Welcome(string application, Invocation invocation)
    {
        if (!enabled)
            return;
        try
        {
            string version = typeof(ConciseOutput).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion.Split('+')[0] ?? "unknown version";
            string mode = invocation.IsWatchMode ? "watch mode" : "one-shot mode, message " + SingleLine(invocation.Basename!);
            WriteLine($"{SingleLine(application)} {SingleLine(version)} starting, {mode}");
            WriteLine($"data \"{SingleLine(invocation.DataDirectory)}\", spool \"{SingleLine(invocation.SpoolDirectory)}\"");
        }
        catch (Exception error) { Disable(error); }
    }

    // Summarize successfully loaded authoritative records using the existing in-memory counts only.
    public void Loaded(int senderCount, int tagCount)
    {
        if (!enabled)
            return;
        try
        {
            WriteLine($"loaded {senderCount.ToString(CultureInfo.InvariantCulture)} sender records and " +
                $"{tagCount.ToString(CultureInfo.InvariantCulture)} tag mappings from disk");
        }
        catch (Exception error) { Disable(error); }
    }

    // Report only a completed pair transfer; formatting and console failures never change its outcome.
    public void Moved(string basename, byte[]? hdr, string destination, string? recipient = null,
        string? tag = null, bool created = false, string? action = null)
    {
        if (!enabled)
            return;
        try
        {
            string from = Address(ReadLine(hdr, 1), nullReversePath: true);
            string to = Recipients(recipient ?? ReadLine(hdr, 2));
            string auth = Authentication(hdr);
            string tagText = tag is null ? "" : $", {(created ? "created" : "used")} tag {Address(tag)}";
            string ending = action is null ? ", moved to " + SingleLine(destination) : "";
            WriteLine($"{SingleLine(basename)} {SingleLine(action ?? "from")} {from} to {to}, {auth}{tagText}{ending}");
        }
        catch (Exception error) { Disable(error); }
    }

    // Timestamp and flush one already escaped line within its caller's protected output boundary.
    private void WriteLine(string text)
    {
        string timestamp = clock().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        stdout.WriteLine(timestamp + ": " + text);
        stdout.Flush();
    }

    // Disable only concise reporting after a failure, leaving mail and other output sinks independent.
    private void Disable(Exception error)
    {
        enabled = false;
        try { ConsoleErrors.Write(stderr, "ERROR writing concise output: " + ConsoleErrors.FormatException(error)); }
        catch (Exception) { }
    }

    // Extract a positional display value without validating addresses or altering authoritative parsing.
    private static string? ReadLine(byte[]? hdr, int index)
    {
        if (hdr is null)
            return null;
        ReadOnlySpan<byte> remaining = hdr;
        for (int line = 0; line <= index; line++)
        {
            int end = remaining.IndexOf("\r\n"u8);
            if (end < 0)
                return null;
            if (line == index)
                return Encoding.Latin1.GetString(remaining[..end]);
            remaining = remaining[(end + 2)..];
        }
        return null;
    }

    // Reuse the independent HDR classifier for display, distinguishing unknown identity from proven absence.
    private static string Authentication(byte[]? hdr)
    {
        if (hdr is null)
            return "auth unknown";
        FastHdrResult result = FastHdrClassifier.Classify(hdr);
        return result.Kind switch
        {
            FastHdrKind.NoAuth => "no auth",
            FastHdrKind.ValidAuth => "auth " + Address(result.CanonicalAuth),
            _ => "auth unknown"
        };
    }

    // Display all positional recipients in their original order without adding recipient validation.
    private static string Recipients(string? value) => value is null ? "unknown"
        : string.Join(", ", value.Split(',').Select(item => Address(item)));

    // Add readable angle brackets while keeping missing data distinct from the SMTP null reverse path.
    private static string Address(string? value, bool nullReversePath = false)
    {
        if (value is null)
            return "unknown";
        value = value.Trim(' ', '\t');
        if (value.Length == 0)
            return nullReversePath ? "<>" : "unknown";
        string display = SingleLine(value);
        return value.StartsWith('<') && value.EndsWith('>') ? display : "<" + display + ">";
    }

    // Keep untrusted display text on one line without double-escaping ordinary Windows path separators.
    private static string SingleLine(string value) => ConsoleErrors.QuoteForDisplay(value)[1..^1]
        .Replace("\u2028", "\\u2028", StringComparison.Ordinal).Replace("\u2029", "\\u2029", StringComparison.Ordinal);
}
