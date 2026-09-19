using System.Globalization;
using System.Text;
using SmTagger.Engine;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class ConciseOutputTests
{
    // Read only the envelope positions, preserving the null sender and never substituting metadata From.
    [Fact]
    public void UsesLocalTimestampEnvelopeRecipientsAndCanonicalAuth()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var instant = new DateTimeOffset(2026, 9, 18, 19, 36, 0, TimeSpan.Zero);
        var concise = new ConciseOutput(true, output, errors, () => instant);
        byte[] hdr = Encoding.ASCII.GetBytes("Written \r\n\r\nBob@example.net,alice@example.org\r\n" +
            "auth: AUTH@EXAMPLE.COM\r\nfrom: metadata@example.com\r\n\r\n");

        concise.Moved("42615432", hdr, "process");

        string timestamp = instant.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.Equal(timestamp + ": 42615432 from <> to <Bob@example.net>, <alice@example.org>, " +
            "auth <auth@example.com>, moved to process" + Environment.NewLine, output.ToString());
        Assert.Empty(errors.ToString());
    }

    // Show known positional data even when malformed metadata prevents any trustworthy auth classification.
    [Theory]
    [InlineData("Failed\r\n<>\r\nnot an address\r\nbroken\r\n\r\n", "from <> to <not an address>, auth unknown")]
    [InlineData("Failed\r\n", "from unknown to unknown, auth unknown")]
    [InlineData("Written\r\nsender@example.com\r\n\r\n\r\n", "from <sender@example.com> to unknown, no auth")]
    public void MissingOrMalformedDisplayValuesAreNotNewContractErrors(string hdr, string expected)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        new ConciseOutput(true, output, errors).Moved("42", Encoding.ASCII.GetBytes(hdr), "failed");
        Assert.Contains(expected + ", moved to failed", output.ToString());
        Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Empty(errors.ToString());
    }

    // A child displays its own recipient and tag while retaining the original envelope sender.
    [Theory]
    [InlineData(true, "created")]
    [InlineData(false, "used")]
    public void DisplaysChildLookupResultWithoutRememberingEarlierCalls(bool created, string verb)
    {
        using var output = new StringWriter();
        new ConciseOutput(true, output).Moved("42c1", Encoding.ASCII.GetBytes(ProcessorFixture.Header("a@example.net,b@example.net")),
            "spool", "a@example.net", "tag-12345@example.org", created);
        Assert.Contains("42c1 from <private@example.com> to <a@example.net>, auth <auth@example.com>, " +
            verb + " tag <tag-12345@example.org>, moved to spool", output.ToString());
        Assert.DoesNotContain("b@example.net", output.ToString());
    }

    // Raw controls and Unicode separators cannot forge extra physical summary lines.
    [Fact]
    public void EscapesUntrustedControlsAndSeparators()
    {
        using var output = new StringWriter();
        byte[] hdr = Encoding.ASCII.GetBytes("Written\r\nbad\nsender\r\nto\tvalue\r\n\r\n");
        var concise = new ConciseOutput(true, output);
        concise.Welcome("sm-sorter", new Invocation("D:\\data\r\nforged", "D:\\spool\u2028forged", "message\nforged"));
        concise.Moved("base\r\n\u2028forged\u2029", hdr, "spool");
        string text = output.ToString();
        Assert.Contains(@"one-shot mode, message message\nforged", text);
        Assert.Contains("data \"D:\\data\\r\\nforged\", spool \"D:\\spool\\u2028forged\"", text);
        Assert.Contains(@"base\r\n\u2028forged\u2029 from <bad\nsender> to <to\tvalue>, auth unknown", text);
        Assert.Equal(3, text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    // Disable only the failed concise sink after either writing or flushing fails, without recursive errors.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BrokenConsoleIsReportedOnceAndNeverThrows(bool failFlush)
    {
        using var output = new FailingWriter(failFlush);
        using var errors = new StringWriter();
        var concise = new ConciseOutput(true, output, errors);
        concise.Moved("first", null, "spool");
        concise.Moved("second", null, "spool");
        Assert.Equal(1, output.Writes);
        Assert.Equal(failFlush ? 1 : 0, output.Flushes);
        Assert.Equal(1, errors.ToString().Split("ERROR writing concise output:").Length - 1);

        using var brokenErrors = new FailingWriter(false);
        new ConciseOutput(true, new FailingWriter(false), brokenErrors).Moved("third", null, "spool");
    }

    // A broken welcome disables later startup and message summaries through the same best-effort sink.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BrokenWelcomeIsReportedOnceAndSuppressesLaterOutput(bool failFlush)
    {
        using var output = new FailingWriter(failFlush);
        using var errors = new StringWriter();
        var concise = new ConciseOutput(true, output, errors);
        concise.Welcome("sm-tagger", new Invocation("data", "spool", null));
        concise.Loaded(1, 2);
        concise.Moved("42", null, "spool");
        Assert.Equal(1, output.Writes);
        Assert.Equal(failFlush ? 1 : 0, output.Flushes);
        Assert.Equal(1, errors.ToString().Split("ERROR writing concise output:").Length - 1);
    }

    // An unrequested summary never even attempts formatting or clock access.
    [Fact]
    public void DisabledOutputDoesNoWork()
    {
        using var output = new FailingWriter(false);
        using var errors = new StringWriter();
        var concise = new ConciseOutput(false, output, errors, () => throw new InvalidOperationException());
        concise.Welcome("sm-tagger", null!);
        concise.Loaded(1, 2);
        concise.Moved("42", null, "spool");
        Assert.Equal(0, output.Writes);
        Assert.Empty(errors.ToString());
    }

    // Disabled or failed startup reporting cannot stop configuration loading or subsequent mail publication.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupReportingDoesNotChangeSuccessfulProcessing(bool enabled)
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("startup-console");
        using var output = new FailingWriter(false);
        using var trace = TraceLog.Open(null, stderr: fixture.Errors);
        var concise = new ConciseOutput(enabled, output, fixture.Errors);
        concise.Welcome("sm-tagger", new Invocation(fixture.DataDirectory, fixture.SpoolDirectory, "startup-console"));
        var configuration = SenderConfiguration.Load(fixture.DataDirectory, trace);
        var tags = TagStore.Load(fixture.DataDirectory, configuration, trace);
        concise.Loaded(configuration.Profiles.Count, tags.Mappings.Count);
        var processor = new TaggerProcessor(fixture.SpoolDirectory, configuration, tags, trace, concise: concise);

        Assert.Equal(MessageOutcome.Succeeded, processor.Process("startup-console"));

        Assert.Equal(enabled ? 1 : 0, output.Writes);
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "startup-consolec1.hdr")));
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "startup-consolec1.eml")));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        Assert.Equal(enabled ? 1 : 0, fixture.Errors.ToString().Split("ERROR writing concise output:").Length - 1);
    }

    private sealed class FailingWriter(bool failFlush) : StringWriter
    {
        public int Writes { get; private set; }
        public int Flushes { get; private set; }

        // Fail at a controlled console boundary without exposing a runtime fault option.
        public override void WriteLine(string? value)
        {
            Writes++;
            if (!failFlush)
                throw new IOException("Console write failed.");
            base.WriteLine(value);
        }

        // Exercise output that accepts a line but cannot flush it.
        public override void Flush()
        {
            Flushes++;
            throw new IOException("Console flush failed.");
        }
    }
}
