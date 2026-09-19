using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class ConciseInvocationTests
{
    // All three diagnostic switches are independent in both roles, including simultaneous console modes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlagsAreIndependentInEveryCombination(bool tagger)
    {
        for (int mask = 0; mask < 8; mask++)
        {
            var arguments = new List<string> { "data" };
            if ((mask & 1) != 0) arguments.Add("-c");
            if ((mask & 2) != 0) arguments.Add("-v");
            if ((mask & 4) != 0) arguments.AddRange(["-l", "test.log"]);
            arguments.AddRange(["spool", "-c"]);
            Invocation result = tagger ? Invocation.ParseTagger(arguments.ToArray()) : Invocation.ParseSorter(arguments.ToArray());
            Assert.Equal((mask & 1) != 0, result.Concise);
            Assert.Equal((mask & 2) != 0, result.Verbose);
            Assert.Equal((mask & 4) != 0 ? Path.GetFullPath("test.log") : null, result.LogPath);
            Assert.Equal("-c", result.Basename);
        }
    }

    // Accept concise after other switches without allowing a missing log path to consume it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConciseOrderAndMissingOperandsFollowExistingOptionRules(bool tagger)
    {
        Func<string[], Invocation> parse = tagger ? Invocation.ParseTagger : Invocation.ParseSorter;
        var arguments = new List<string> { "data", "-l", "test.log", "-v" };
        if (tagger) arguments.Add("-keep");
        arguments.AddRange(["-c", "spool"]);
        Invocation result = parse(arguments.ToArray());
        Assert.True(result.Concise);
        Assert.True(result.Verbose);
        Assert.True(result.IsWatchMode);
        Assert.Equal(tagger, result.Keep);
        Assert.Throws<ArgumentException>(() => parse(["data", "-c"]));
        Assert.Throws<ArgumentException>(() => parse(["data", "-c", "-c", "spool"]));
        Assert.Contains("requires a logfile", Assert.Throws<ArgumentException>(() => parse(["data", "-l", "-c", "spool"])).Message);
        Assert.Contains("[-c]", tagger ? Invocation.TaggerUsage : Invocation.SorterUsage);
    }
}
