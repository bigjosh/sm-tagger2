using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class ConsoleErrorsTests
{
    // Console exceptions retain real stack lines and literal path text rather than decoding escape sequences.
    [Fact]
    public void WriteExceptionPreservesActualNewlinesAndLiteralBackslashes()
    {
        using var writer = new StringWriter();
        Exception failure = CaptureException();

        ConsoleErrors.WriteException(writer, failure);

        Assert.Equal("ERROR " + failure + Environment.NewLine, writer.ToString());
        Assert.Contains(@"C:\new\temp", writer.ToString());
        Assert.Contains("first line\r\nsecond line", writer.ToString());
        Assert.Contains(Environment.NewLine + "   at ", writer.ToString());
    }

    // A readable quoted scalar retains literal backslashes without allowing its own controls to make new lines.
    [Fact]
    public void DisplayQuoteDoesNotInterpretLiteralBackslashSequences()
    {
        Assert.Equal("\"C:\\new\\temp\\r\\n\\t\\x00\\\"\"",
            ConsoleErrors.QuoteForDisplay("C:\\new\\temp\r\n\t\0\""));
    }

    // Exception conversion failure stays inside the final best-effort stderr boundary.
    [Fact]
    public void ExceptionFormattingFailureDoesNotEscape()
    {
        using var writer = new StringWriter();

        ConsoleErrors.WriteException(writer, new UnprintableException());

        Assert.Empty(writer.ToString());
    }

    // Produce authentic managed stack frames and a nested exception with intentional message line breaks.
    private static Exception CaptureException()
    {
        try { throw new IOException("Cannot read C:\\new\\temp: first line\r\nsecond line", new InvalidOperationException("inner failure")); }
        catch (Exception exception) { return exception; }
    }

    private sealed class UnprintableException : Exception
    {
        // Exercise stderr's formatting boundary independently of a failed writer.
        public override string ToString() => throw new InvalidOperationException("Cannot format exception.");
    }
}
