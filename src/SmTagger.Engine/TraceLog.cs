using System.Globalization;
using System.Text;

namespace SmTagger.Engine;

/// <summary>Best-effort operational diagnostics, with a once-opened invocation trace.</summary>
public sealed class TraceLog : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string path;
    private readonly TextWriter stderr;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<string, FileMode, Stream> openLog;
    private string runId = string.Empty;
    private Stream? stream;
    private long sequence;

    public bool IsEnabled => stream is not null;

    // Retain the logging dependencies without opening or mutating the data tree.
    private TraceLog(string dataDir, TextWriter stderr, Func<DateTimeOffset> clock,
        Func<string, FileMode, Stream> openLog)
    {
        path = Path.Combine(dataDir, "log.txt");
        this.stderr = stderr;
        this.clock = clock;
        this.openLog = openLog;
    }

    // Attempt the optional trace once; its failure never prevents normal startup.
    public static TraceLog Open(string dataDir, bool enabled, TextWriter? stderr = null,
        Func<DateTimeOffset>? clock = null) => OpenCore(dataDir, enabled, stderr ?? Console.Error,
            clock ?? (() => DateTimeOffset.UtcNow), OpenFile, Guid.NewGuid);

    // Provide a narrow stream seam for write, flush, and disposal failure tests.
    internal static TraceLog OpenForTesting(string dataDir, bool enabled, TextWriter stderr,
        Func<DateTimeOffset> clock, Func<string, FileMode, Stream> openLog, Func<Guid>? runUuid = null) =>
        OpenCore(dataDir, enabled, stderr, clock, openLog, runUuid ?? Guid.NewGuid);

    // Open an invocation trace using the selected file operation.
    private static TraceLog OpenCore(string dataDir, bool enabled, TextWriter stderr,
        Func<DateTimeOffset> clock, Func<string, FileMode, Stream> openLog, Func<Guid> runUuid)
    {
        var trace = new TraceLog(dataDir, stderr, clock, openLog);
        if (enabled)
        {
            try
            {
                trace.runId = runUuid().ToString("D");
                trace.stream = openLog(trace.path, FileMode.Append);
            }
            catch (Exception exception) { trace.ReportLoggingFailure(trace.path, "initialize/open", exception); }
        }
        return trace;
    }

    // Append one fully formatted UTF-8 event and disable only this trace on any failure.
    public void Event(string context, string eventName, string result,
        params (string Name, object? Value)[] details)
    {
        if (stream is null)
            return;
        try
        {
            sequence++;
            RequireToken(eventName);
            RequireToken(result);
            string timestamp = clock().ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            var text = new StringBuilder(timestamp).Append(" run=").Append(runId)
                .Append(" seq=").Append(sequence.ToString(CultureInfo.InvariantCulture))
                .Append(" context=").Append(Quote(context)).Append(" event=").Append(eventName)
                .Append(" result=").Append(result);
            foreach ((string name, object? value) in details)
            {
                RequireFieldName(name);
                text.Append(' ').Append(name).Append('=').Append(FormatValue(value));
            }
            text.Append("\r\n");
            byte[] bytes = StrictUtf8.GetBytes(text.ToString());
            stream.Write(bytes);
            stream.Flush();
        }
        catch (Exception exception)
        {
            Disable(context, eventName, exception);
        }
    }

    // Surround a persistent mutation with attempted intent/result events without changing its outcome.
    public void Transition(string context, string operation, string? source, string? destination,
        Action mutation, string? expected = null)
    {
        Event(context, "STATE_INTENT", "ATTEMPT", ("operation", operation), ("source", source),
            ("destination", destination), ("expected", expected));
        try
        {
            mutation();
            Event(context, "STATE_RESULT", "OK", ("operation", operation), ("source", source),
                ("destination", destination), ("residual", "operation completed"));
        }
        catch (Exception exception)
        {
            Event(context, "STATE_RESULT", "ERROR", ("operation", operation), ("source", source),
                ("destination", destination), ("exceptionType", exception.GetType().FullName),
                ("hresult", exception.HResult), ("error", exception), ("residual", "UNKNOWN"));
            throw;
        }
    }

    // Emit one protected stderr line; stderr failure deliberately has no recursive fallback.
    public void ReportError(string message, Exception? exception = null)
    {
        try
        {
            string prefix = message.StartsWith("ERROR", StringComparison.Ordinal) || message.StartsWith("WARNING", StringComparison.Ordinal)
                ? "" : "ERROR ";
            string reason = exception is null ? "" : " exception=" + Quote(exception.ToString());
            stderr.WriteLine(prefix + SingleLine(message) + reason);
        }
        catch (Exception) { }
    }

    // Create a nonreplacing human diagnostic while preserving the underlying message outcome.
    public void WriteDiagnostic(string diagnosticPath, string reason,
        IEnumerable<(string Name, object? Value)> details)
    {
        try
        {
            var text = new StringBuilder(SingleLine(reason)).Append("\r\n");
            foreach ((string name, object? value) in details)
                text.Append(SingleLine(name)).Append('=').Append(FormatValue(value)).Append("\r\n");
            byte[] bytes = StrictUtf8.GetBytes(text.ToString());
            Event("-", "STATE_INTENT", "ATTEMPT", ("operation", "create"), ("destination", diagnosticPath));
            using (var diagnostic = new FileStream(diagnosticPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                diagnostic.Write(bytes);
                diagnostic.Flush();
            }
            Event("-", "STATE_RESULT", "OK", ("operation", "create"), ("destination", diagnosticPath));
        }
        catch (Exception exception)
        {
            Event("-", "STATE_RESULT", "ERROR", ("operation", "create"), ("destination", diagnosticPath),
                ("error", exception), ("residual", "UNKNOWN"));
            try { ReportError($"Cannot write diagnostic {Quote(diagnosticPath)}", exception); }
            catch (Exception) { }
        }
    }

    // Append or create one independent tag-log buffer, catching every stage including disposal.
    internal void WriteTagLog(string logPath, byte[] bytes, bool createEmpty, string context)
    {
        string operation = createEmpty ? "create" : "append";
        Event(context, "STATE_INTENT", "ATTEMPT", ("operation", operation), ("destination", logPath));
        try
        {
            using (Stream tagLog = openLog(logPath, createEmpty ? FileMode.CreateNew : FileMode.Append))
            {
                tagLog.Write(bytes);
                tagLog.Flush();
            }
            Event(context, "STATE_RESULT", "OK", ("operation", operation), ("destination", logPath));
        }
        catch (Exception exception)
        {
            Event(context, "STATE_RESULT", "ERROR", ("operation", operation), ("destination", logPath),
                ("error", exception), ("residual", "UNKNOWN"));
            ReportLoggingFailure(logPath, operation, exception);
        }
    }

    // Report a log failure with the stable searchable prefix required by the specification.
    internal void ReportLoggingFailure(string logPath, string operation, Exception exception)
    {
        try { ReportError($"ERROR logging to {Quote(logPath)}: {operation}", exception); }
        catch (Exception) { }
    }

    // Quote arbitrary values without allowing diagnostic line injection or malformed UTF-8.
    public static string Quote(string value)
    {
        var text = new StringBuilder(value.Length + 2).Append('"');
        AppendEscaped(text, value, quote: true);
        return text.Append('"').ToString();
    }

    // Close a trace without converting cleanup trouble into a mail-processing failure.
    public void Dispose()
    {
        Stream? acquired = stream;
        stream = null;
        if (acquired is null)
            return;
        try { acquired.Dispose(); }
        catch (Exception exception) { ReportLoggingFailure(path, "close", exception); }
    }

    // Permanently detach the failed stream before attempting its best-effort cleanup.
    private void Disable(string context, string eventName, Exception exception)
    {
        Stream? failed = stream;
        stream = null;
        try { ReportLoggingFailure(path, $"event={eventName} context={Quote(context)}", exception); }
        catch (Exception) { }
        try { failed?.Dispose(); }
        catch (Exception cleanupException) { ReportLoggingFailure(path, "close failed trace", cleanupException); }
    }

    // Use append/create modes with read sharing; existing bytes are never read or repaired.
    private static Stream OpenFile(string filePath, FileMode mode) =>
        new FileStream(filePath, mode, FileAccess.Write, FileShare.Read);

    // Keep integer details searchable while quoting all arbitrary strings and other values.
    private static string FormatValue(object? value) => value switch
    {
        null => Quote(""),
        byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        bool boolean => boolean ? "TRUE" : "FALSE",
        byte[] bytes => QuoteBytes(bytes),
        ReadOnlyMemory<byte> bytes => QuoteBytes(bytes.Span),
        IEnumerable<string> strings => Quote(string.Join(",", strings)),
        IEnumerable<int> integers => Quote(string.Join(",", integers.Select(integer => integer.ToString(CultureInfo.InvariantCulture)))),
        _ => Quote(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
    };

    // Retain invalid raw bytes explicitly while preserving complete valid UTF-8 sequences.
    private static string QuoteBytes(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder().Append('"');
        ReadOnlySpan<byte> remaining = bytes;
        while (!remaining.IsEmpty)
        {
            System.Buffers.OperationStatus status = Rune.DecodeFromUtf8(remaining, out Rune rune, out int consumed);
            if (status == System.Buffers.OperationStatus.Done)
            {
                AppendEscaped(text, rune.ToString(), quote: true);
                remaining = remaining[consumed..];
            }
            else
            {
                text.Append("\\x").Append(remaining[0].ToString("X2", CultureInfo.InvariantCulture));
                remaining = remaining[1..];
            }
        }
        return text.Append('"').ToString();
    }

    // Make a readable reason stay on one physical line without escaping its intentional punctuation.
    private static string SingleLine(string value)
    {
        var text = new StringBuilder(value.Length);
        AppendEscaped(text, value, quote: false);
        return text.ToString();
    }

    // Escape controls and malformed surrogate code units while retaining valid Unicode text.
    private static void AppendEscaped(StringBuilder text, string value, bool quote)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            switch (character)
            {
                case '\r': text.Append("\\r"); break;
                case '\n': text.Append("\\n"); break;
                case '\t': text.Append("\\t"); break;
                case '\\' when quote: text.Append("\\\\"); break;
                case '"' when quote: text.Append("\\\""); break;
                default:
                    if (char.IsControl(character))
                        text.Append("\\x").Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                        text.Append(character).Append(value[++index]);
                    else if (char.IsSurrogate(character))
                    {
                        // A malformed UTF-16 value has no UTF-8 form; retain both original code-unit bytes.
                        text.Append("\\x").Append(((int)character >> 8).ToString("X2", CultureInfo.InvariantCulture))
                            .Append("\\x").Append(((int)character & 255).ToString("X2", CultureInfo.InvariantCulture));
                    }
                    else
                        text.Append(character);
                    break;
            }
        }
    }

    // Keep the fixed event/result grammar separate from arbitrary quoted diagnostic data.
    private static void RequireToken(string value)
    {
        if (value.Length == 0 || value.Any(character => character is not (>= 'A' and <= 'Z') and not '_'))
            throw new FormatException("Trace event and result names must be uppercase ASCII tokens.");
    }

    // Avoid giving a malformed field name an opportunity to forge additional trace fields.
    private static void RequireFieldName(string value)
    {
        if (value.Length == 0 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new FormatException("Trace detail names must be ASCII alphanumeric identifiers.");
    }
}
