using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class InvocationTests
{
    // Preserve a leading-hyphen basename rather than interpreting it as a sorter option.
    [Theory]
    [InlineData("-log")]
    [InlineData("-l")]
    [InlineData("-v")]
    public void SorterAcceptsLiteralHyphenBasename(string basename)
    {
        Invocation result = Invocation.ParseSorter(["data", "spool", basename]);
        Assert.Equal(basename, result.Basename);
        Assert.False(result.IsWatchMode);
        Assert.False(result.Log);
        Assert.False(result.Keep);
        Assert.Null(result.SorterLogPath);
        Assert.False(result.Verbose);
        Assert.True(Path.IsPathFullyQualified(result.DataDirectory));
        Assert.True(Path.IsPathFullyQualified(result.SpoolDirectory));
    }

    // Accept sorter-only flags in either order before spooldir and resolve the explicit logfile once.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SorterParsesLogAndVerboseOptionsInEitherOrder(bool verboseFirst)
    {
        string logfile = Path.Combine("diagnostics", "sorter trace.txt");
        string[] arguments = verboseFirst
            ? ["data", "-v", "-l", logfile, "spool", "message"]
            : ["data", "-l", logfile, "-v", "spool", "message"];

        Invocation result = Invocation.ParseSorter(arguments);

        Assert.Equal(Path.GetFullPath(logfile), result.SorterLogPath);
        Assert.True(result.Verbose);
        Assert.Equal(Path.GetFullPath("spool"), result.SpoolDirectory);
        Assert.Equal("message", result.Basename);
        Assert.False(result.Log);
        Assert.False(result.Keep);
    }

    // Keep file logging and stdout verbosity independent without consuming a final flag-shaped basename.
    [Fact]
    public void SorterDiagnosticFlagsAreIndependentAndStopAtSpoolDirectory()
    {
        Invocation fileOnly = Invocation.ParseSorter(["data", "-l", "sorter.log", "spool", "-v"]);
        Assert.Equal(Path.GetFullPath("sorter.log"), fileOnly.SorterLogPath);
        Assert.False(fileOnly.Verbose);
        Assert.Equal("-v", fileOnly.Basename);

        Invocation verboseOnly = Invocation.ParseSorter(["data", "-v", "spool", "-l"]);
        Assert.Null(verboseOnly.SorterLogPath);
        Assert.True(verboseOnly.Verbose);
        Assert.Equal("-l", verboseOnly.Basename);

        Invocation watcher = Invocation.ParseSorter(["data", "-v", "-l", "sorter.log", "spool"]);
        Assert.True(watcher.IsWatchMode);
        Assert.True(watcher.Verbose);
        Assert.Equal(Path.GetFullPath("sorter.log"), watcher.SorterLogPath);
    }

    // Reject missing operands, repeated sorter options, and options misplaced after the spool argument.
    [Fact]
    public void InvalidSorterDiagnosticOptionsFailInvocation()
    {
        string[][] invalidArguments =
        [
            ["data", "-l"],
            ["data", "-l", "sorter.log"],
            ["data", "-l", "-v", "spool"],
            ["data", "-v"],
            ["data", "-v", "-l"],
            ["data", "-l", "", "spool"],
            ["data", "-v", "-v", "spool"],
            ["data", "-l", "first.log", "-l", "second.log", "spool"],
            ["data", "-v", "-l", "sorter.log", "-v", "spool"],
            ["data", "spool", "-l", "sorter.log"]
        ];

        Assert.All(invalidArguments, arguments => Assert.Throws<ArgumentException>(() => Invocation.ParseSorter(arguments)));
    }

    // Recognize only flags before spooldir and leave later hyphens as literal message names.
    [Fact]
    public void TaggerParsesFlagsOnlyBeforeSpoolDirectory()
    {
        Invocation result = Invocation.ParseTagger(["data", "-keep", "-log", "spool", "-keep"]);
        Assert.True(result.Log);
        Assert.True(result.Keep);
        Assert.Equal("-keep", result.Basename);
        Assert.Null(result.SorterLogPath);
        Assert.False(result.Verbose);
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
