using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class InvocationTests
{
    // Preserve a leading-hyphen basename rather than interpreting it as a sorter option.
    [Fact]
    public void SorterAcceptsLiteralHyphenBasename()
    {
        Invocation result = Invocation.ParseSorter(["data", "spool", "-log"]);
        Assert.Equal("-log", result.Basename);
        Assert.False(result.IsWatchMode);
        Assert.False(result.Log);
        Assert.False(result.Keep);
        Assert.True(Path.IsPathFullyQualified(result.DataDirectory));
        Assert.True(Path.IsPathFullyQualified(result.SpoolDirectory));
    }

    // Recognize only flags before spooldir and leave later hyphens as literal message names.
    [Fact]
    public void TaggerParsesFlagsOnlyBeforeSpoolDirectory()
    {
        Invocation result = Invocation.ParseTagger(["data", "-keep", "-log", "spool", "-keep"]);
        Assert.True(result.Log);
        Assert.True(result.Keep);
        Assert.Equal("-keep", result.Basename);
    }

    // Omitting the optional basename selects the long-running discovery mode.
    [Fact]
    public void BothInvocationsSupportWatchMode()
    {
        Assert.True(Invocation.ParseSorter(["data", "spool"]).IsWatchMode);
        Assert.True(Invocation.ParseTagger(["data", "-log", "spool"]).IsWatchMode);
    }

    // Reject paths and unusable Windows components before any queue files can be touched.
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../message")]
    [InlineData("folder/message")]
    [InlineData("folder\\message")]
    [InlineData("C:message")]
    [InlineData("CON")]
    [InlineData("aux.txt")]
    [InlineData("message.")]
    [InlineData("message ")]
    [InlineData("message\nforged")]
    public void InvalidBasenamesFailInvocation(string basename)
    {
        Assert.Throws<ArgumentException>(() => Invocation.ParseSorter(["data", "spool", basename]));
        Assert.Throws<ArgumentException>(() => Invocation.ParseTagger(["data", "spool", basename]));
    }

    // Fail incomplete or extra positional arguments instead of silently processing another queue.
    [Fact]
    public void InvalidArgumentCountsFailInvocation()
    {
        Assert.Throws<ArgumentException>(() => Invocation.ParseSorter([]));
        Assert.Throws<ArgumentException>(() => Invocation.ParseSorter(["data"]));
        Assert.Throws<ArgumentException>(() => Invocation.ParseSorter(["data", "spool", "a", "b"]));
        Assert.Throws<ArgumentException>(() => Invocation.ParseTagger(["data", "-keep"]));
        Assert.Throws<ArgumentException>(() => Invocation.ParseTagger(["data", "spool", "a", "b"]));
    }

    // An exclusive persistent handle excludes another owner and can be reacquired after disposal.
    [Fact]
    public void SingletonContentionDoesNotDependOnFileExistence()
    {
        using SorterTestDirectory tree = new();
        string lockPath = Path.Combine(tree.Root, "singleton.lock");
        using (SingletonLock owner = SingletonLock.Acquire(lockPath))
        {
            Assert.Throws<IOException>(() => SingletonLock.Acquire(lockPath));
        }

        Assert.True(File.Exists(lockPath));
        using SingletonLock nextOwner = SingletonLock.Acquire(lockPath);
    }

    // Keep untrusted values within one quoted diagnostic value while preserving complete text.
    [Fact]
    public void DiagnosticValuesEscapeLineBreaksAndControls()
    {
        Assert.Equal("\"name\\r\\n\\t\\x00\\\\\\\"\"", ConsoleErrors.Quote("name\r\n\t\0\\\""));
    }
}
