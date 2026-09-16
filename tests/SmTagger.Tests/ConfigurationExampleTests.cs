using SmTagger.Engine;

namespace SmTagger.Tests;

public sealed class ConfigurationExampleTests
{
    // Keep the checked-in onboarding example compatible with the actual startup contract.
    [Fact]
    public void CompleteSyntheticExampleLoadsWithMdnDisabled()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SmTagger.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string dataDirectory = Path.Combine(directory.FullName, "examples", "datadir");
        using var trace = TraceLog.Open(dataDirectory, enabled: false);
        SenderConfiguration configuration = SenderConfiguration.Load(dataDirectory, trace);
        SenderProfile profile = Assert.Single(configuration.Profiles).Value;
        Assert.Same(profile, configuration.Resolve("auth@example.com"));
        Assert.Equal("22222222-2222-4222-8222-222222222222", profile.SenderId);
        Assert.Equal("replace-with-private-alias@example.com", profile.PrivateAddress);
        Assert.Equal("tag-%@reply.example.com", profile.Template);
        Assert.False(profile.AllowMdn);
        Assert.Empty(profile.RetiredAuthAddresses);
        Assert.Empty(profile.RetiredPrivateAddresses);
    }
}
