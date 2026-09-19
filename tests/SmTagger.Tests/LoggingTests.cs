using System.Text;
using SmTagger.Engine;

namespace SmTagger.Tests;

public sealed class LoggingTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 9, 5, 12, 34, 56, 789, TimeSpan.FromHours(2));

    // A disabled trace neither creates output nor formats details with potentially failing conversions.
    [Fact]
    public void DisabledTraceDoesNotOpenOrFormatAnything()
    {
        using var fixture = new StoreTestFixture();
        int opens = 0;
        using var trace = TraceLog.OpenForTesting(null, false, fixture.Errors, () => FixedTime,
            (_, _) => { opens++; return new MemoryStream(); });
        trace.Event("context", "EVENT", "OK", ("bad", new ThrowingValue()));
        Assert.Equal(0, opens);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "log.txt")));
    }

    // A disabled trace never obtains an invocation UUID or otherwise initializes unused file tracing.
    [Fact]
    public void DisabledTraceDoesNotRequestRunUuid()
    {
        using var fixture = new StoreTestFixture();
        using var trace = TraceLog.OpenForTesting(null, false, fixture.Errors,
            () => throw new InvalidOperationException("Disabled trace must not obtain the time."),
            (_, _) => throw new InvalidOperationException("Disabled trace must not open a file."),
            runUuid: () => throw new InvalidOperationException("Disabled trace must not obtain a UUID."));
        trace.Event("unused", "NO_OUTPUT", "OK");
        Assert.False(trace.IsEnabled);
        Assert.False(trace.IsVerbose);
        Assert.Equal("", fixture.Errors.ToString());
    }

    // UUID creation belongs to the best-effort logging boundary rather than startup configuration.
    [Fact]
    public void TraceUuidFailureReportsAndContinuesWithoutOpeningLog()
    {
        using var fixture = new StoreTestFixture();
        int opens = 0;
        using var trace = TraceLog.OpenForTesting(Path.Combine(fixture.Root, "log.txt"), false, fixture.Errors, () => FixedTime,
            (_, _) => { opens++; return new MemoryStream(); },
            runUuid: () => throw new IOException("injected UUID failure"));
        Assert.False(trace.IsEnabled);
        Assert.Equal(0, opens);
        Assert.Contains("injected UUID failure", fixture.Errors.ToString());
        int mutations = 0;
        trace.Transition("parent", "create", null, "child", () => mutations++);
        Assert.Equal(1, mutations);
    }

    // Newly appended events use stable UTC prefixes, exact CRLF, and escaped arbitrary values.
    [Fact]
    public void TraceHasStablePrefixAndPreventsLineInjection()
    {
        using var fixture = new StoreTestFixture();
        var bytes = new MemoryStream();
        using (var trace = TraceLog.OpenForTesting(Path.Combine(fixture.Root, "log.txt"), false, fixture.Errors, () => FixedTime, (_, _) => bytes))
        {
            trace.Event("message\r\nforged", "VALUE", "OK", ("text", "quote\" slash\\ tab\t nul\0"),
                ("number", 42), ("raw", new byte[] { 0xff, 0xc3, 0xa9, 10 }));
            trace.Event("next", "VALUE", "OK");
        }
        string text = new UTF8Encoding(false, true).GetString(bytes.ToArray());
        Assert.StartsWith("2026-09-05T10:34:56.789Z run=", text);
        Assert.Matches("run=[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12} seq=1", text);
        Assert.Contains("context=\"message\\r\\nforged\"", text);
        Assert.Contains("text=\"quote\\\" slash\\\\ tab\\t nul\\x00\"", text);
        Assert.Contains("number=42", text);
        Assert.Contains("raw=\"\\xFFé\\n\"", text);
        Assert.Contains("seq=2", text);
        Assert.Equal(2, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.EndsWith("\r\n", text);
    }

    // Existing malformed bytes and unfinished tails are retained with no repair separator.
    [Fact]
    public void CorruptTraceIsAppendedWithoutReadingOrRepair()
    {
        using var fixture = new StoreTestFixture();
        string path = Path.Combine(fixture.Root, "log.txt");
        byte[] original = [255, 0, 99, 13];
        File.WriteAllBytes(path, original);
        using (var trace = TraceLog.Open(path, stderr: fixture.Errors, clock: () => FixedTime))
            trace.Event("-", "STARTUP", "OK");
        byte[] actual = File.ReadAllBytes(path);
        Assert.Equal(original, actual[..original.Length]);
        Assert.Equal((byte)'2', actual[original.Length]);
        Assert.Contains("2026-09-05T10:34:56.789Z", Encoding.UTF8.GetString(actual[original.Length..]));
    }

    // Every operational trace failure disables only that invocation's trace and leaves mutations executable.
    [Theory]
    [InlineData("open")]
    [InlineData("write")]
    [InlineData("flush")]
    public void TraceFailureNeverGatesMutationAndNeverReopens(string stage)
    {
        using var fixture = new StoreTestFixture();
        int opens = 0;
        using var trace = TraceLog.OpenForTesting(Path.Combine(fixture.Root, "log.txt"), false, fixture.Errors, () => FixedTime, (_, _) =>
        {
            opens++;
            if (stage == "open")
                throw new IOException("injected open failure");
            return new FailingLogStream(stage);
        });
        int mutations = 0;
        trace.Transition("first", "move", "source", "destination", () => mutations++);
        trace.Transition("later", "move", "source", "destination", () => mutations++);
        Assert.Equal(2, mutations);
        Assert.False(trace.IsEnabled);
        Assert.Equal(1, opens);
        Assert.Contains("ERROR logging to", fixture.Errors.ToString());
    }

    // A detail conversion failure has the same nonblocking policy as file I/O failure.
    [Fact]
    public void TraceFormattingFailureDisablesOnlyTrace()
    {
        using var fixture = new StoreTestFixture();
        using var trace = TraceLog.OpenForTesting(Path.Combine(fixture.Root, "log.txt"), false, fixture.Errors, () => FixedTime, (_, _) => new MemoryStream());
        trace.Event("message", "FORMAT", "ATTEMPT", ("value", new ThrowingValue()));
        Assert.False(trace.IsEnabled);
        Assert.Contains("injected formatting failure", fixture.Errors.ToString());
    }

    // Raw memory and collection details are formatted only after entering the trace's nonblocking boundary.
    [Fact]
    public void TraceFormatsRawMemoryAndCollectionsWithoutLosingEscaping()
    {
        using var fixture = new StoreTestFixture();
        var bytes = new MemoryStream();
        using (var trace = TraceLog.OpenForTesting(Path.Combine(fixture.Root, "log.txt"), false, fixture.Errors, () => FixedTime, (_, _) => bytes))
        {
            trace.Event("parent", "DETAILS", "OK", ("marker", new ReadOnlyMemory<byte>([0xff, 0xc3, 0xa9, 10])),
                ("addresses", new[] { "first@example.com", "second\r\nforged@example.com" }), ("counts", new[] { 1, 2, 3 }));
        }
        string text = new UTF8Encoding(false, true).GetString(bytes.ToArray());
        Assert.Contains("marker=\"\\xFFé\\n\"", text);
        Assert.Contains("addresses=\"first@example.com,second\\r\\nforged@example.com\"", text);
        Assert.Contains("counts=\"1,2,3\"", text);
        Assert.Single(text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }

    // Enumeration performed solely for logging cannot escape into mail processing if it fails.
    [Fact]
    public void TraceCollectionFormattingFailureIsNonblocking()
    {
        using var fixture = new StoreTestFixture();
        using var trace = TraceLog.OpenForTesting(Path.Combine(fixture.Root, "log.txt"), false, fixture.Errors, () => FixedTime, (_, _) => new MemoryStream());
        trace.Event("parent", "DETAILS", "ATTEMPT", ("addresses", FailingStringSequence()));
        Assert.False(trace.IsEnabled);
        Assert.Contains("injected collection formatting failure", fixture.Errors.ToString());
        int mutations = 0;
        trace.Transition("parent", "move", "source", "destination", () => mutations++);
        Assert.Equal(1, mutations);
    }

    // Even a secondary failure while formatting the failure report cannot keep a failed writer attached or open.
    [Fact]
    public void FailureReportFormattingDoesNotPreventTraceDetachmentOrCleanup()
    {
        using var fixture = new StoreTestFixture();
        var bytes = new MemoryStream();
        using var trace = TraceLog.OpenForTesting(Path.Combine(fixture.Root, "log.txt"), false, fixture.Errors, () => FixedTime, (_, _) => bytes);
        trace.Event(null!, "DETAILS", "ATTEMPT");
        Assert.False(trace.IsEnabled);
        Assert.False(bytes.CanWrite);
        trace.ReportLoggingFailure(null!, "append", new IOException("original logging failure"));
        trace.WriteDiagnostic(null!, "original contract error", []);
    }

    // Disposing a completed invocation cannot promote a logging cleanup failure to process failure.
    [Fact]
    public void TraceCloseFailureIsReportedAndDoesNotThrow()
    {
        using var fixture = new StoreTestFixture();
        TraceLog trace = TraceLog.OpenForTesting(Path.Combine(fixture.Root, "log.txt"), false, fixture.Errors, () => FixedTime,
            (_, _) => new FailingLogStream("close"));
        trace.Event("-", "SHUTDOWN", "OK");
        trace.Dispose();
        trace.Dispose();
        Assert.Contains("injected close failure", fixture.Errors.ToString());
    }

    // Best-effort stderr has no recursive diagnostic channel and never prevents ordinary work.
    [Fact]
    public void UnwritableStderrDoesNotEscapeLoggingBoundary()
    {
        using var fixture = new StoreTestFixture();
        using var trace = TraceLog.OpenForTesting(Path.Combine(fixture.Root, "log.txt"), false, new ThrowingTextWriter(), () => FixedTime,
            (_, _) => throw new IOException("unavailable log"));
        trace.ReportError("also unavailable", new IOException("original error"));
        trace.WriteDiagnostic(Path.Combine(fixture.Root, "missing", "parent.err"), "original contract error", []);
        Assert.False(trace.IsEnabled);
    }

    // The same event reaches both sinks with one clock read and identical invocation and sequence identity.
    [Fact]
    public void VerboseAndFileShareOneStructuredEventAndLeaveConsoleOpen()
    {
        using var fixture = new StoreTestFixture();
        using var output = new StringWriter();
        var bytes = new MemoryStream();
        int clockReads = 0;
        int uuidReads = 0;
        using (var trace = TraceLog.OpenForTesting(Path.Combine(fixture.Root, "chosen.log"), true, fixture.Errors,
            () => { clockReads++; return FixedTime; }, (_, _) => bytes,
            runUuid: () => { uuidReads++; return Guid.Parse("12345678-1234-1234-1234-123456789abc"); }, stdout: output))
        {
            Assert.True(trace.IsEnabled);
            Assert.True(trace.IsVerbose);
            trace.Event("parent", "FIRST", "OK", ("input", "line\r\nforged"));
            trace.Event("parent", "SECOND", "OK");
        }

        string[] fileLines = Encoding.UTF8.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        string[] consoleLines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(fileLines.Select(line => "DEBUG " + line), consoleLines);
        Assert.Equal(2, clockReads);
        Assert.Equal(1, uuidReads);
        Assert.Contains("seq=2", fileLines[1]);
        output.WriteLine("caller still owns stdout");
        Assert.Contains("caller still owns stdout", output.ToString());
        Assert.Empty(fixture.Errors.ToString());
    }

    // Console exception text preserves real newlines and literal paths while file events stay single-line records.
    [Fact]
    public void ExceptionHasReadableConsoleLinesAndExactlyOneFileEscape()
    {
        using var fixture = new StoreTestFixture();
        using var output = new StringWriter();
        var bytes = new MemoryStream();
        const string path = @"C:\new\temp\sender";
        Exception failure = CaptureException("Cannot load " + path + ".\r\nCheck the sender record.");
        string exceptionText = failure.ToString();
        using var trace = TraceLog.OpenForTesting(Path.Combine(fixture.Root, "chosen.log"), true, fixture.Errors,
            () => FixedTime, (_, _) => bytes, stdout: output);

        trace.Event("parent", "FATAL", "ERROR", ("path", path), ("exception", failure));

        string file = Encoding.UTF8.GetString(bytes.ToArray());
        Assert.Single(file.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("path=" + TraceLog.Quote(path), file);
        Assert.Contains("exception=" + TraceLog.Quote(exceptionText), file);
        Assert.DoesNotContain("exception=" + TraceLog.Quote(TraceLog.Quote(exceptionText)), file);
        Assert.Contains("path=\"" + path + "\"", output.ToString());
        Assert.Contains("exception=" + exceptionText, output.ToString());
        Assert.Contains("\r\nCheck the sender record.", output.ToString());
        Assert.Contains(Environment.NewLine + "   at ", output.ToString());
        Assert.Empty(fixture.Errors.ToString());
    }

    // Dual output enumerates and converts each value once without interpreting literal backslash escape spellings.
    [Fact]
    public void ReadableConsoleKeepsScalarControlsEscapedAndConvertsValuesOnce()
    {
        using var fixture = new StoreTestFixture();
        using var output = new StringWriter();
        var bytes = new MemoryStream();
        var value = new CountingValue();
        int enumerations = 0;
        using var trace = TraceLog.OpenForTesting(Path.Combine(fixture.Root, "chosen.log"), true, fixture.Errors,
            () => FixedTime, (_, _) => bytes, stdout: output);

        trace.Event(@"C:\new\temp", "VALUES", "OK", ("value", value), ("sequence", Values()),
            ("raw", Encoding.UTF8.GetBytes("C:\\new\\temp\r\nnext")));

        Assert.Equal(1, value.Conversions);
        Assert.Equal(1, enumerations);
        Assert.Contains("context=\"C:\\new\\temp\"", output.ToString());
        Assert.Contains("value=\"C:\\new\\temp\\r\\nnext\"", output.ToString());
        Assert.Contains("raw=\"C:\\new\\temp\\r\\nnext\"", output.ToString());
        Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Single(Encoding.UTF8.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries));

        // Count enumeration so the two renderings cannot consume mutable input independently.
        IEnumerable<string> Values()
        {
            enumerations++;
            yield return @"C:\new\temp";
        }
    }

    // Human stderr prints a complete exception chain below escaped scalar context without literal newline codes.
    [Fact]
    public void ReportErrorPrintsExceptionLinesAndRetainsLiteralBackslashNames()
    {
        using var fixture = new StoreTestFixture();
        Exception failure = CaptureException(@"Cannot read C:\new\temp.");

        fixture.Trace.ReportError("Message failed; context=bad\r\nvalue", failure);

        string error = fixture.Errors.ToString();
        Assert.StartsWith("ERROR Message failed; context=bad\\r\\nvalue" + Environment.NewLine, error);
        Assert.Contains(failure.ToString(), error);
        Assert.Contains(@"C:\new\temp", error);
        Assert.Contains(Environment.NewLine + "   at ", error);
    }

    // Diagnostic exception details are passed as values and escaped once, never as already quoted strings.
    [Fact]
    public void DiagnosticExceptionIsEscapedOnce()
    {
        using var fixture = new StoreTestFixture();
        string path = Path.Combine(fixture.Root, "parent.err");
        Exception failure = CaptureException(@"Cannot read C:\new\temp.");

        fixture.Trace.WriteDiagnostic(path, "Message failed.", [("exception", failure)]);

        Assert.Equal("Message failed.\r\nexception=" + TraceLog.Quote(failure.ToString()) + "\r\n", File.ReadAllText(path));
    }

    // Console-only tracing must expose full detail without opening an implicit data-root trace file.
    [Fact]
    public void VerboseWithoutFileNeverOpensTraceAndFlushesEveryEvent()
    {
        using var fixture = new StoreTestFixture();
        using var output = new ObservedTextWriter();
        using var trace = TraceLog.OpenForTesting(null, true, fixture.Errors, () => FixedTime,
            (_, _) => throw new InvalidOperationException("Verbose-only tracing must not open a file."), stdout: output);

        trace.Event("parent", "DETAIL", "OK", ("address", "private@example.com"));
        trace.Transition("parent", "move", "source", "target", () => { });

        Assert.False(trace.IsEnabled);
        Assert.True(trace.IsVerbose);
        Assert.Contains("DEBUG ", output.ToString());
        Assert.Contains("private@example.com", output.ToString());
        Assert.Equal(3, output.FlushCount);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "log.txt")));
        Assert.Empty(fixture.Errors.ToString());
    }

    // A selected file's open, append, flush, or close failure leaves console diagnostics and mail work independent.
    [Theory]
    [InlineData("open")]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("close")]
    public void FileFailureNeverDisablesVerboseOrGatesMutation(string stage)
    {
        using var fixture = new StoreTestFixture();
        using var output = new StringWriter();
        int opens = 0;
        using var trace = TraceLog.OpenForTesting(Path.Combine(fixture.Root, "chosen.log"), true, fixture.Errors,
            () => FixedTime, (_, _) =>
            {
                opens++;
                return stage == "open" ? throw new IOException("injected open failure") : new FailingLogStream(stage);
            }, stdout: output);
        int mutations = 0;

        trace.Transition("first", "move", "source", "target", () => mutations++);
        trace.Transition("second", "move", "source", "target", () => mutations++);

        Assert.Equal(2, mutations);
        Assert.Equal(1, opens);
        Assert.True(trace.IsVerbose);
        Assert.Equal(stage == "close", trace.IsEnabled);
        Assert.Contains("context=\"first\"", output.ToString());
        Assert.Contains("context=\"second\"", output.ToString());
        Assert.Equal(4, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        trace.Dispose();
        Assert.Contains($"injected {stage} failure", fixture.Errors.ToString());
        output.WriteLine("stdout remains owned by caller");
    }

    // A broken stdout write or flush disables that sink once while later file trace and mutations continue.
    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    public void VerboseFailureLeavesFileTraceAndMutationsActive(string stage)
    {
        using var fixture = new StoreTestFixture();
        using var output = new ObservedTextWriter(stage);
        var bytes = new MemoryStream();
        using var trace = TraceLog.OpenForTesting(Path.Combine(fixture.Root, "chosen.log"), true, fixture.Errors,
            () => FixedTime, (_, _) => bytes, stdout: output);
        int mutations = 0;

        trace.Transition("first", "move", "source", "target", () => mutations++);
        trace.Transition("later", "move", "source", "target", () => mutations++);

        Assert.Equal(2, mutations);
        Assert.True(trace.IsEnabled);
        Assert.False(trace.IsVerbose);
        Assert.Equal(1, output.WriteCount);
        Assert.Equal(stage == "flush" ? 1 : 0, output.FlushCount);
        string text = Encoding.UTF8.GetString(bytes.ToArray());
        Assert.Contains("context=\"later\"", text);
        Assert.Contains("seq=4", text);
        Assert.Contains($"injected stdout {stage} failure", fixture.Errors.ToString());
    }

    // Failure of shared event rendering disables both unusable sinks without suppressing the underlying work.
    [Theory]
    [InlineData("uuid")]
    [InlineData("clock")]
    [InlineData("format")]
    public void SharedEventPreparationFailureIsNonblockingForBothSinks(string stage)
    {
        using var fixture = new StoreTestFixture();
        using var output = new StringWriter();
        var bytes = new MemoryStream();
        using var trace = TraceLog.OpenForTesting(Path.Combine(fixture.Root, "chosen.log"), true, fixture.Errors,
            () => stage == "clock" ? throw new IOException("injected clock failure") : FixedTime, (_, _) => bytes,
            runUuid: () => stage == "uuid" ? throw new IOException("injected UUID failure") : Guid.NewGuid(), stdout: output);
        trace.Event("parent", "DETAILS", "ATTEMPT", ("value", new ThrowingValue()));
        int mutations = 0;
        trace.Transition("parent", "move", "source", "destination", () => mutations++);

        Assert.Equal(1, mutations);
        Assert.False(trace.IsEnabled);
        Assert.False(trace.IsVerbose);
        Assert.Empty(output.ToString());
        Assert.Empty(bytes.ToArray());
        Assert.Contains("injected", fixture.Errors.ToString());
    }

    // Explicit log paths never create their parent directories or silently fall back to the former default file.
    [Fact]
    public void MissingLogParentReportsFailureWithoutCreatingDirectoriesAndVerboseSurvives()
    {
        using var fixture = new StoreTestFixture();
        using var output = new StringWriter();
        string missingParent = Path.Combine(fixture.Root, "missing");
        using var trace = TraceLog.Open(Path.Combine(missingParent, "chosen.log"), true, fixture.Errors, output);

        trace.Event("parent", "CONTINUES", "OK");

        Assert.False(trace.IsEnabled);
        Assert.True(trace.IsVerbose);
        Assert.False(Directory.Exists(missingParent));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "log.txt")));
        Assert.Contains("ERROR logging to", fixture.Errors.ToString());
        Assert.Contains("event=CONTINUES", output.ToString());
    }

    // Parent diagnostics preserve collision evidence and never replace an existing reason file.
    [Fact]
    public void DiagnosticCreateFailurePreservesExistingEvidence()
    {
        using var fixture = new StoreTestFixture();
        string path = Path.Combine(fixture.Root, "parent.err");
        File.WriteAllText(path, "previous reason");
        fixture.Trace.WriteDiagnostic(path, "new contract error", [("value", "secret@example.com")]);
        Assert.Equal("previous reason", File.ReadAllText(path));
        Assert.Contains("Cannot write diagnostic", fixture.Errors.ToString());
    }

    // Runtime diagnostics retain relevant identities while escaping message-derived controls.
    [Fact]
    public void DiagnosticEscapesValuesAndRetainsUnredactedIdentity()
    {
        using var fixture = new StoreTestFixture();
        string path = Path.Combine(fixture.Root, "parent.err");
        fixture.Trace.WriteDiagnostic(path, "bad header\r\nforged reason", [("privateAddress", "secret@example.com"), ("raw", "x\ny")]);
        string text = File.ReadAllText(path);
        Assert.StartsWith("bad header\\r\\nforged reason\r\n", text);
        Assert.Contains("privateAddress=\"secret@example.com\"", text);
        Assert.Contains("raw=\"x\\ny\"", text);
        Assert.Equal(3, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    // Each child attempts one identical entry per distinct tag, ordered by ordinal tag address.
    [Fact]
    public void PublicationLogsAreDistinctOrdinalAndShareOneTimestamp()
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteMapping("sender-zzzzz@tags.example.com", "z@example.net;");
        fixture.WriteMapping("sender-00000@tags.example.com", "a@example.net;");
        var opens = new List<string>();
        var buffers = new List<MemoryStream>();
        using var trace = TraceLog.OpenForTesting(null, false, fixture.Errors, () => FixedTime, (path, _) =>
        {
            opens.Add(path);
            var stream = new MemoryStream();
            buffers.Add(stream);
            return stream;
        });
        int clockReads = 0;
        TagStore store = TagStore.Load(fixture.Root, fixture.LoadConfiguration(), trace, clock: () => { clockReads++; return FixedTime; });
        TagMapping[] maps = store.Mappings.Values.OrderByDescending(mapping => mapping.TagAddress, StringComparer.Ordinal).ToArray();
        store.AppendPublicationLogs([maps[0], maps[1], maps[0]], "parentc2", "parent");
        Assert.Equal(1, clockReads);
        Assert.Equal(2, opens.Count);
        Assert.Contains("sender-00000", opens[0]);
        Assert.Contains("sender-zzzzz", opens[1]);
        Assert.All(buffers, buffer => Assert.Equal("20260905T103456Z parentc2\r\n", Encoding.UTF8.GetString(buffer.ToArray())));
    }

    // Failed first-tag appends do not suppress remaining tags, and later children attempt their own entries normally.
    [Fact]
    public void FirstTagLogFailureContinuesRemainingTagsAndLaterChildren()
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteMapping("sender-00000@tags.example.com", "a@example.net;");
        fixture.WriteMapping("sender-11111@tags.example.com", "b@example.net;");
        var opens = new List<string>();
        using var trace = TraceLog.OpenForTesting(null, false, fixture.Errors, () => FixedTime, (path, _) =>
        {
            opens.Add(path);
            if (opens.Count == 1)
                throw new IOException("first log unavailable");
            return new MemoryStream();
        });
        TagStore store = TagStore.Load(fixture.Root, fixture.LoadConfiguration(), trace, clock: () => FixedTime);
        TagMapping[] maps = store.Mappings.Values.ToArray();
        store.AppendPublicationLogs(maps, "parentc1", "parent");
        store.AppendPublicationLogs(maps, "parentc2", "parent");
        Assert.Equal(4, opens.Count);
        Assert.Contains("sender-00000", opens[0]);
        Assert.Contains("sender-11111", opens[1]);
        Assert.Contains("sender-00000", opens[2]);
        Assert.Contains("ERROR logging to", fixture.Errors.ToString());
    }

    // Empty tag-log creation is independent of closing and publishing both required identity files.
    [Fact]
    public void EmptyLogCreationFailureDoesNotBlockAllocationAndLaterAppendCanCreateIt()
    {
        using var fixture = new StoreTestFixture();
        using var trace = TraceLog.OpenForTesting(null, false, fixture.Errors, () => FixedTime, (path, mode) =>
        {
            if (mode == FileMode.CreateNew)
                throw new IOException("empty log creation unavailable");
            return new FileStream(path, mode, FileAccess.Write, FileShare.Read);
        });
        SenderConfiguration configuration = fixture.LoadConfiguration();
        TagStore store = TagStore.Load(fixture.Root, configuration, trace, clock: () => FixedTime, randomBytes: Array.Clear);
        TagMapping mapping = store.GetOrCreate(configuration.Profiles[StoreTestFixture.SenderId], "person@example.net;", "parent");
        Assert.Single(store.Mappings);
        Assert.False(File.Exists(Path.Combine(mapping.DirectoryPath, "tag-log.txt")));
        store.AppendPublicationLogs([mapping], "parentc1", "parent");
        Assert.Equal("20260905T103456Z parentc1\r\n", File.ReadAllText(Path.Combine(mapping.DirectoryPath, "tag-log.txt")));
        Assert.Contains("empty log creation unavailable", fixture.Errors.ToString());
    }

    // Tag-log write, flush, and close failures do not become permanent disablement or mapping failures.
    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("close")]
    public void TagLogOperationFailureIsNonblockingAndLaterEntryRetriesNormally(string stage)
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteMapping("sender-00000@tags.example.com", "person@example.net;");
        int opens = 0;
        using var trace = TraceLog.OpenForTesting(null, false, fixture.Errors, () => FixedTime, (_, _) =>
        {
            opens++;
            return new FailingLogStream(stage);
        });
        TagStore store = TagStore.Load(fixture.Root, fixture.LoadConfiguration(), trace, clock: () => FixedTime);
        store.AppendPublicationLogs(store.Mappings.Values, "parentc1", "parent");
        store.AppendPublicationLogs(store.Mappings.Values, "parentc2", "parent");
        Assert.Equal(2, opens);
        Assert.Single(store.Mappings);
        Assert.Contains($"injected {stage} failure", fixture.Errors.ToString());
    }

    // Encoding failure reports each affected log and returns control to the same child's publication caller.
    [Fact]
    public void TagLogEncodingFailureDoesNotThrow()
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteMapping("sender-00000@tags.example.com", "person@example.net;");
        TagStore store = TagStore.Load(fixture.Root, fixture.LoadConfiguration(), fixture.Trace, clock: () => FixedTime);
        store.AppendPublicationLogs(store.Mappings.Values, "bad\ud800basename", "parent");
        Assert.Contains("encode publication entry", fixture.Errors.ToString());
        Assert.Single(store.Mappings);
    }

    // Corrupt tag-log bytes receive a direct append without a separator, decoding, or startup validation.
    [Fact]
    public void CorruptTagLogReceivesDirectAppend()
    {
        using var fixture = new StoreTestFixture();
        string directory = fixture.WriteMapping("sender-00000@tags.example.com", "person@example.net;");
        string path = Path.Combine(directory, "tag-log.txt");
        File.WriteAllBytes(path, [255, 99]);
        TagStore store = TagStore.Load(fixture.Root, fixture.LoadConfiguration(), fixture.Trace, clock: () => FixedTime);
        store.AppendPublicationLogs(store.Mappings.Values, "parentc1", "parent");
        byte[] actual = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 255, 99 }, actual[..2]);
        Assert.Equal("20260905T103456Z parentc1\r\n", Encoding.UTF8.GetString(actual[2..]));
    }

    private sealed class ThrowingValue
    {
        // Force the event formatting path to fail independently of filesystem operations.
        public override string ToString() => throw new InvalidOperationException("injected formatting failure");
    }

    // Capture a real managed stack and inner exception instead of synthesizing stack-trace text.
    private static Exception CaptureException(string message)
    {
        try { throw new IOException(message, new InvalidOperationException("inner failure")); }
        catch (Exception exception) { return exception; }
    }

    private sealed class CountingValue
    {
        public int Conversions { get; private set; }

        // Detect duplicate event conversion while supplying both a literal path and message-derived newlines.
        public override string ToString()
        {
            Conversions++;
            return "C:\\new\\temp\r\nnext";
        }
    }

    // Yield one ordinary value before simulating an exception during lazy collection formatting.
    private static IEnumerable<string> FailingStringSequence()
    {
        yield return "first@example.com";
        throw new InvalidOperationException("injected collection formatting failure");
    }

    private sealed class ThrowingTextWriter : StringWriter
    {
        // Model an unavailable operator stderr channel without adding any fallback channel.
        public override void WriteLine(string? value) => throw new IOException("stderr unavailable");
    }

    private sealed class ObservedTextWriter : StringWriter
    {
        private readonly string? failingStage;
        public int WriteCount { get; private set; }
        public int FlushCount { get; private set; }

        // Observe caller-owned console use and optionally fail one ordinary output operation.
        public ObservedTextWriter(string? failingStage = null) => this.failingStage = failingStage;

        // Count writes so a detached console cannot be retried by subsequent events.
        public override void WriteLine(string? value)
        {
            WriteCount++;
            if (failingStage == "write")
                throw new IOException("injected stdout write failure");
            base.WriteLine(value);
        }

        // Count immediate flushing and exercise a console failure after its text was accepted.
        public override void Flush()
        {
            FlushCount++;
            if (failingStage == "flush")
                throw new IOException("injected stdout flush failure");
            base.Flush();
        }
    }

    private sealed class FailingLogStream : MemoryStream
    {
        private readonly string stage;

        // Select one ordinary managed logging operation to fail.
        internal FailingLogStream(string stage) => this.stage = stage;

        // Fail either the buffer write or retain its bytes for later flush/close failure cases.
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (stage == "write")
                throw new IOException("injected write failure");
            base.Write(buffer);
        }

        // Cover byte-array dispatch as well as the span-based call used by production.
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (stage == "write")
                throw new IOException("injected write failure");
            base.Write(buffer, offset, count);
        }

        // Model buffered data that cannot be flushed before the related state transition.
        public override void Flush()
        {
            if (stage == "flush")
                throw new IOException("injected flush failure");
            base.Flush();
        }

        // Release the buffer before reporting a simulated managed close failure.
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && stage == "close")
                throw new IOException("injected close failure");
        }
    }
}
