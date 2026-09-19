using System.Text;
using SmTagger.Engine;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class TaggerFailureTests
{
    // Even an exception formatter failure must not escape the message-local diagnostic boundary.
    [Fact]
    public void DiagnosticFormattingFailurePreservesUnderlyingLocalOutcome()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("mail");
        using var harness = fixture.Open();
        harness.Processor.ObserveCheckpoint = checkpoint =>
        {
            if (checkpoint.Phase == "PARENT_CLAIMED")
            {
                throw new BrokenDiagnosticException();
            }
        };
        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.hdr.start")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.eml.start")));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        harness.Processor.ObserveCheckpoint = null;
        fixture.WriteMessage("fresh");
        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("fresh"));
    }

    // A missing owned EML is a local failure that keeps the already-inert HDR.
    [Fact]
    public void MissingEmlAfterOwnershipRetainsHdrStart()
    {
        using var fixture = new ProcessorFixture();
        File.WriteAllText(Path.Combine(fixture.ProcessDirectory, "mail.hdr"), ProcessorFixture.Header());
        using var harness = fixture.Open();
        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.hdr.start")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.err")));
        Assert.Empty(harness.Tags.Mappings);
    }

    // An ownership collision is fatal because the selected plain trigger was not made inert.
    [Fact]
    public void FirstClaimCollisionIsFatalAndPreservesPlainPair()
    {
        using var fixture = new ProcessorFixture();
        var original = fixture.WriteMessage("mail");
        File.WriteAllText(Path.Combine(fixture.ProcessDirectory, "mail.hdr.start"), "existing evidence");
        using var harness = fixture.Open();
        Assert.Throws<FatalProcessingException>(() => harness.Processor.Process("mail"));
        Assert.Equal(original.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.hdr")));
        Assert.Equal(original.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml")));
        Assert.Equal("existing evidence", File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "mail.hdr.start")));
    }

    // Stale scans produce no mail diagnostic while an absent one-shot request fails.
    [Fact]
    public void MissingPlainHdrDistinguishesWatchFromOneShot()
    {
        using var fixture = new ProcessorFixture();
        using var harness = fixture.Open();
        Assert.Equal(MessageOutcome.Stale, harness.Processor.Process("gone", watchMode: true));
        Assert.Equal("", fixture.Errors.ToString());
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("gone"));
        Assert.Contains("not found", fixture.Errors.ToString());
    }

    // From retention stops at the first collision and never overwrites the existing artifact.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FromRetentionCollisionPreservesExactPartialState(bool headerCollision)
    {
        using var fixture = new ProcessorFixture();
        var original = fixture.WriteMessage("mail", eml: ProcessorFixture.Message(""));
        var collision = Path.Combine(fixture.ProcessDirectory, headerCollision ? "mail.hdr.err" : "mail.eml.err");
        File.WriteAllText(collision, "existing evidence");
        using var harness = fixture.Open();
        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.Equal("existing evidence", File.ReadAllText(collision));
        Assert.Equal(original.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml.start")));
        var retainedHeader = headerCollision ? "mail.hdr.start" : "mail.hdr.err";
        Assert.Equal(original.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, retainedHeader)));
        Assert.Empty(harness.Tags.Mappings);
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
    }

    // A failed diagnostic creation does not turn rejected mail into a successful publication.
    [Fact]
    public void DiagnosticCollisionDoesNotReleaseFromContractError()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("mail", eml: ProcessorFixture.Message(""));
        File.WriteAllText(Path.Combine(fixture.ProcessDirectory, "mail.err"), "existing diagnostic");
        using var harness = fixture.Open(log: true);
        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.Equal("existing diagnostic", File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "mail.err")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.hdr.err")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.eml.err")));
        Assert.Contains("exactly one From", fixture.Errors.ToString());
        Assert.Contains("diagnostic", fixture.Errors.ToString());
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
    }

    // Stderr identifies the failing child and every retained path when both file diagnostics are unavailable.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterChildLengthFailureReportsCompleteRetainedStateToStandardError(bool log)
    {
        using var fixture = new ProcessorFixture();
        const string shortTag = "a@reply.example.com";
        fixture.WriteMapping(shortTag, "alice@example.net;");
        fixture.WriteMapping("aa@reply.example.com", "bob@example.net;");
        var padding = new string('N', 998 - Encoding.ASCII.GetByteCount($"From: \"\" <{shortTag}>"));
        fixture.WriteMessage("mail", ProcessorFixture.Header("alice@example.net,bob@example.net"),
            ProcessorFixture.Message($"From: \"{padding}\" <{ProcessorFixture.Private}>\r\n",
                "Reply-To: deliberate@example.org\r\n"));
        Directory.CreateDirectory(Path.Combine(fixture.DataDirectory, "log.txt"));
        Directory.CreateDirectory(Path.Combine(fixture.ProcessDirectory, "mail.err"));
        using var harness = fixture.Open(log: log);

        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        string stderr = fixture.Errors.ToString();
        var error = Assert.Single(stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries),
            line => line.Contains("basename=\"mail\"", StringComparison.Ordinal));
        Assert.Contains("activeChild=\"mailc2\"", error);
        Assert.Contains("operation=\"rewrite-eml-and-check-header-length\"", error);
        Assert.Contains("source=" + ConsoleErrors.QuoteForDisplay(Path.Combine(fixture.ProcessDirectory, "mail.eml.break")), error);
        Assert.Contains("destination=" + ConsoleErrors.QuoteForDisplay(Path.Combine(fixture.ProcessDirectory, "mailc2.eml.process")), error);
        Assert.Contains("parentHdr=" + ConsoleErrors.QuoteForDisplay(Path.Combine(fixture.ProcessDirectory, "mail.hdr.break")), error);
        Assert.Contains("parentEml=" + ConsoleErrors.QuoteForDisplay(Path.Combine(fixture.ProcessDirectory, "mail.eml.break")), error);
        Assert.Contains("Header From, physical line 1: 999 bytes exceeds the 998-byte limit", stderr);
        Assert.Contains(Environment.NewLine + "   at ", stderr);
        Assert.Contains("child=\"mailc1\" status=\"UNATTEMPTED\" recipient=\"alice@example.net\"", error);
        Assert.Contains("childHdr=" + ConsoleErrors.QuoteForDisplay(Path.Combine(fixture.ProcessDirectory, "mailc1.hdr.pend")), error);
        Assert.Contains("childEml=" + ConsoleErrors.QuoteForDisplay(Path.Combine(fixture.ProcessDirectory, "mailc1.eml.pend")), error);
        Assert.Contains("child=\"mailc2\" status=\"UNATTEMPTED\" recipient=\"bob@example.net\" childHdr=\"NOT_CREATED\" childEml=\"NOT_CREATED\"", error);
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.hdr.break")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.eml.break")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mailc1.hdr.pend")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mailc1.eml.pend")));
        Assert.False(File.Exists(Path.Combine(fixture.ProcessDirectory, "mailc2.eml.process")));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
    }

    // Stderr renders exception line breaks while the retained diagnostic escapes its reason and exception once.
    [Fact]
    public void FailureStateStandardErrorShowsExceptionLinesWhileFileEscapesControls()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("mail");
        using var harness = fixture.Open();
        var failure = new IOException("Synthetic \"failure\"\r\nforged=entry\tvalue");
        harness.Processor.ObserveCheckpoint = _ => throw failure;

        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        string error = fixture.Errors.ToString();
        string[] consoleLines = error.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("basename=\"mail\"", consoleLines[0]);
        Assert.Contains(failure.ToString(), error);
        Assert.Contains("Synthetic \"failure\"\r\nforged=entry\tvalue", error);
        Assert.Contains(Environment.NewLine + "   at ", error);
        string diagnostic = File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "mail.err"));
        Assert.StartsWith("Synthetic \"failure\"\\r\\nforged=entry\\tvalue\r\n", diagnostic);
        string exceptionLine = Assert.Single(diagnostic.Split("\r\n", StringSplitOptions.RemoveEmptyEntries),
            line => line.StartsWith("exception=", StringComparison.Ordinal));
        Assert.Equal("exception=" + TraceLog.Quote(failure.ToString()), exceptionLine);
        Assert.DoesNotContain('\r', exceptionLine);
        Assert.DoesNotContain('\n', exceptionLine);
        Assert.DoesNotContain('\t', exceptionLine);
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
    }

    // Failure on a later EML or HDR publication leaves earlier children possibly sent and later children untouched.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PublicationFailureRetainsParentAndExactChildStatuses(bool failEml)
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("mail", ProcessorFixture.Header("alice@example.net,bob@example.net,carol@example.net"),
            ProcessorFixture.Message(extra: "Reply-To: deliberate@example.org\r\n"));
        var collision = Path.Combine(fixture.SpoolDirectory, failEml ? "mailc2.eml" : "mailc2.hdr");
        File.WriteAllText(collision, "foreign destination");
        using var harness = fixture.Open(log: true);
        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.Equal("foreign destination", File.ReadAllText(collision));
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "mailc1.hdr")));
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "mailc1.eml")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.hdr.break")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.eml.break")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mailc2.hdr.pend")));
        Assert.Equal(failEml, File.Exists(Path.Combine(fixture.ProcessDirectory, "mailc2.eml.pend")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mailc3.hdr.pend")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mailc3.eml.pend")));
        Assert.False(File.Exists(Path.Combine(fixture.SpoolDirectory, "mailc3.hdr")));
        var diagnostic = File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "mail.err"));
        Assert.Contains("PUBLISHED", diagnostic);
        Assert.Contains(failEml ? "EML_MOVE_FAILED" : "HDR_MOVE_FAILED_EML_IN_SPOOL", diagnostic);
        Assert.Contains("UNATTEMPTED", diagnostic);
        var third = harness.Tags.Mappings[(ProcessorFixture.SenderId, "carol@example.net;")];
        Assert.Equal("", File.ReadAllText(Path.Combine(third.DirectoryPath, "tag-log.txt")));
        using var restarted = fixture.Open();
        restarted.Processor.ReportResiduals();
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mailc3.hdr.pend")));
    }

    // All requested output evidence must complete before any child is published.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void KeepCopyCollisionLeavesAllChildrenUnpublishedAndIdentifiesActiveChild(int childOrdinal)
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("mail", ProcessorFixture.Header("alice@example.net,bob@example.net"));
        var child = "mailc" + childOrdinal;
        File.WriteAllText(Path.Combine(fixture.ProcessDirectory, child + ".hdr.out"), "existing evidence");
        using var harness = fixture.Open(keep: true);
        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        Assert.Equal(4, Directory.GetFiles(fixture.ProcessDirectory, "*.pend").Length);
        Assert.Equal("existing evidence", File.ReadAllText(Path.Combine(fixture.ProcessDirectory, child + ".hdr.out")));
        Assert.Contains("activeChild=" + ConsoleErrors.QuoteForDisplay(child), fixture.Errors.ToString());
        Assert.Contains("activeChild=" + TraceLog.Quote(child), File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "mail.err")));
        Assert.All(harness.Tags.Mappings.Values,
            mapping => Assert.Equal("", File.ReadAllText(Path.Combine(mapping.DirectoryPath, "tag-log.txt"))));
    }

    // A child construction collision retains earlier ready files and the current partial pair.
    [Theory]
    [InlineData("mailc2.hdr.process")]
    [InlineData("mailc2.eml.pend")]
    public void ConstructionOrReadinessCollisionPublishesNothing(string artifact)
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("mail", ProcessorFixture.Header("alice@example.net,bob@example.net"));
        File.WriteAllText(Path.Combine(fixture.ProcessDirectory, artifact), "existing artifact");
        using var harness = fixture.Open();
        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.Equal("existing artifact", File.ReadAllText(Path.Combine(fixture.ProcessDirectory, artifact)));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mailc1.hdr.pend")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mailc1.eml.pend")));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mailc2.eml.process")));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
    }

    // Partial writes and failed closes retain the actual child artifact without advertising any readiness.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ChildWriteOrCloseFailureRetainsProcessFilesAndAllowsFreshMail(bool header, bool closeFailure)
    {
        using var fixture = new ProcessorFixture();
        var original = fixture.WriteMessage("mail");
        using var harness = fixture.Open();
        var failedPath = Path.Combine(fixture.ProcessDirectory, header ? "mailc1.hdr.process" : "mailc1.eml.process");
        FailingChildStream? failedStream = null;
        harness.Processor.OpenChildFileForTesting = path =>
        {
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return path == failedPath ? failedStream = new FailingChildStream(file, closeFailure) : file;
        };

        Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
        Assert.NotNull(failedStream);
        var retainedBytes = File.ReadAllBytes(failedPath);
        Assert.Equal(closeFailure ? failedStream.IntendedBytes : failedStream.IntendedBytes[..7], retainedBytes);
        Assert.Equal(original.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.hdr.break")));
        Assert.Equal(original.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "mail.eml.break")));
        Assert.Equal(header ? 2 : 1, Directory.GetFiles(fixture.ProcessDirectory, "*.process").Length);
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mailc1.eml.process")));
        Assert.Equal(header, File.Exists(Path.Combine(fixture.ProcessDirectory, "mailc1.hdr.process")));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory, "*.pend"));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        Assert.Contains("destination=" + ConsoleErrors.QuoteForDisplay(failedPath), fixture.Errors.ToString());

        harness.Processor.OpenChildFileForTesting = null;
        fixture.WriteMessage("fresh");
        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("fresh"));
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "freshc1.hdr")));
        Assert.Equal(retainedBytes, File.ReadAllBytes(failedPath));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.hdr.break")));
    }

    // Real sharing denial during cleanup cannot replay children that are already live.
    [Fact]
    public void ParentCleanupFailurePreservesOnlyRemainingParentAndDiagnostic()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("mail");
        using var harness = fixture.Open();
        FileStream? heldHeader = null;
        harness.Processor.ObserveCheckpoint = checkpoint =>
        {
            if (checkpoint.Phase == "BEFORE_PARENT_CLEANUP")
            {
                heldHeader = new FileStream(Path.Combine(fixture.ProcessDirectory, "mail.hdr.break"),
                    FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        };
        try
        {
            Assert.Equal(MessageOutcome.Failed, harness.Processor.Process("mail"));
            Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "mailc1.hdr")));
            Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.hdr.break")));
            Assert.False(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.eml.break")));
            Assert.Contains("PUBLISHED", File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "mail.err")));
            Assert.Contains("parentEml=\"ABSENT\"", fixture.Errors.ToString());
            Assert.Contains("parentHdr=" + ConsoleErrors.QuoteForDisplay(Path.Combine(fixture.ProcessDirectory, "mail.hdr.break")),
                fixture.Errors.ToString());
        }
        finally
        {
            heldHeader?.Dispose();
        }
    }

    // Already-absent parents satisfy cleanup and never turn success into a retained error.
    [Fact]
    public void AlreadyAbsentParentsAreSuccessfulCleanup()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("mail");
        using var harness = fixture.Open();
        harness.Processor.ObserveCheckpoint = checkpoint =>
        {
            if (checkpoint.Phase == "BEFORE_PARENT_CLEANUP")
            {
                File.Delete(Path.Combine(fixture.ProcessDirectory, "mail.hdr.break"));
                File.Delete(Path.Combine(fixture.ProcessDirectory, "mail.eml.break"));
            }
        };
        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("mail"));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
    }

    // A mapping that became live but failed to enter memory must stop the process and survive restart.
    [Fact]
    public void PublishedMappingCacheFailureIsFatalAndReloadable()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("mail");
        using (var harness = fixture.Open())
        {
            harness.Tags.BeforeCacheInsert = _ => throw new InvalidOperationException("cache insertion failure");
            Assert.Throws<MappingAvailabilityException>(() => harness.Processor.Process("mail"));
            Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.hdr.break")));
            Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.err")));
            Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        }

        using var restarted = fixture.Open(random: _ => throw new InvalidOperationException("must reuse live mapping"));
        Assert.Single(restarted.Tags.Mappings);
        fixture.WriteMessage("fresh");
        Assert.Equal(MessageOutcome.Succeeded, restarted.Processor.Process("fresh"));
        Assert.True(File.Exists(Path.Combine(fixture.ProcessDirectory, "mail.hdr.break")));
    }

    // An inaccessible trace and mapping log do not hold the same current child or change its successful outcome.
    [Fact]
    public void LoggingFailuresStillPublishTheCurrentChildAndCleanParent()
    {
        using var fixture = new ProcessorFixture();
        var mappingDirectory = fixture.WriteMapping("stable@reply.example.com", "alice@example.net;");
        Directory.CreateDirectory(Path.Combine(mappingDirectory, "tag-log.txt"));
        Directory.CreateDirectory(Path.Combine(fixture.DataDirectory, "log.txt"));
        fixture.WriteMessage("mail");
        using var harness = fixture.Open(log: true);
        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("mail"));
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "mailc1.hdr")));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        Assert.Contains("ERROR logging to", fixture.Errors.ToString());
    }

    // A failed group-log append cannot suppress the remaining individual-log attempt or child publication.
    [Fact]
    public void FailedOneOfSeveralTagLogsDoesNotStopRemainingLogsOrChildren()
    {
        using var fixture = new ProcessorFixture();
        var group = fixture.WriteMapping("a-group@reply.example.com", "alice@example.net;bob@example.net;");
        Directory.CreateDirectory(Path.Combine(group, "tag-log.txt"));
        fixture.WriteMessage("mail", ProcessorFixture.Header("alice@example.net,bob@example.net"));
        using var harness = fixture.Open();
        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("mail"));
        Assert.Equal(2, Directory.GetFiles(fixture.SpoolDirectory, "*.hdr").Length);
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        foreach (var mapping in harness.Tags.Mappings.Values.Where(mapping => mapping.DirectoryPath != group))
        {
            Assert.Single(File.ReadAllLines(Path.Combine(mapping.DirectoryPath, "tag-log.txt")));
        }
    }

    // Wraps an actual newly created child file while selecting only the failure boundary under test.
    private sealed class FailingChildStream(FileStream file, bool closeFailure) : Stream
    {
        public byte[] IntendedBytes { get; private set; } = [];
        public override bool CanRead => file.CanRead;
        public override bool CanSeek => file.CanSeek;
        public override bool CanWrite => file.CanWrite;
        public override long Length => file.Length;
        public override long Position { get => file.Position; set => file.Position = value; }

        // Preserve the actual filesystem buffer flush behavior.
        public override void Flush() => file.Flush();

        // Delegate reads without introducing a second in-memory storage implementation.
        public override int Read(byte[] buffer, int offset, int count) => file.Read(buffer, offset, count);

        // Preserve the wrapped file's positioning semantics.
        public override long Seek(long offset, SeekOrigin origin) => file.Seek(offset, origin);

        // Preserve the wrapped file's length operation.
        public override void SetLength(long value) => file.SetLength(value);

        // Route byte-buffer writes through the same observed span write boundary.
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        // Leave real partial bytes before a write failure or complete bytes for the close-failure case.
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            IntendedBytes = buffer.ToArray();
            if (!closeFailure)
            {
                file.Write(buffer[..7]);
                throw new IOException("Synthetic child write failure after seven bytes.");
            }

            file.Write(buffer);
        }

        // Close the real file before reporting a managed close failure to the production boundary.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                file.Dispose();
            }

            base.Dispose(disposing);
            if (disposing && closeFailure)
            {
                throw new IOException("Synthetic child close failure.");
            }
        }
    }

    private sealed class BrokenDiagnosticException : Exception
    {
        // Simulates a failure while formatting an existing operation error, not a second mail failure.
        public override string ToString()
        {
            throw new IOException("diagnostic formatting failed");
        }
    }
}
