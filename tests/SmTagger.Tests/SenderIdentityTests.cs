using System.Text;
using SmTagger.Engine;

namespace SmTagger.Tests;

public sealed class SenderIdentityTests
{
    // Administrator-chosen names stay exact through alias resolution, allocation, and a fresh mapping load.
    [Theory]
    [InlineData("Sales")]
    [InlineData("Sales Team")]
    [InlineData("Équipe 東京")]
    [InlineData(" leading-space")]
    [InlineData(".sender")]
    [InlineData("\uFEFFSales")]
    [InlineData("A1234567-89AB-4CDE-8F01-23456789ABCD")]
    [InlineData(StoreTestFixture.SenderId)]
    public void CleanSenderIdsPreserveTheirSpellingAcrossAliasesAndRestart(string senderId)
    {
        using var fixture = new StoreTestFixture();
        if (senderId != StoreTestFixture.SenderId)
        {
            string staging = Path.Combine(fixture.Root, "renamed-record");
            Directory.Move(fixture.ProfileDirectory, staging);
            Directory.Move(staging, Path.Combine(fixture.Root, "senders", "sender-ids", senderId));
        }
        fixture.WriteIndex("auth@example.com", senderId + "\r\n");
        fixture.WriteIndex("alias@example.com", senderId);
        SenderConfiguration configuration = fixture.LoadConfiguration();
        SenderProfile profile = configuration.Resolve("auth@example.com");
        Assert.Equal(senderId, profile.SenderId);
        Assert.Same(profile, configuration.Resolve("alias@example.com"));
        int allocations = 0;
        TagStore store = TagStore.Load(fixture.Root, configuration, fixture.Trace, randomBytes: bytes =>
        {
            allocations++;
            Array.Fill(bytes, (byte)1);
        });

        TagMapping mapping = store.GetOrCreate(profile, "person@example.net;", "first");
        Assert.Same(mapping, store.GetOrCreate(configuration.Resolve("alias@example.com"), "person@example.net;", "alias"));
        Assert.Equal(1, allocations);
        byte[] expectedIdentity = Encoding.UTF8.GetBytes(senderId);
        Assert.Equal(expectedIdentity, File.ReadAllBytes(Path.Combine(mapping.DirectoryPath, "sender-id.txt")));
        SenderConfiguration restartedConfiguration = fixture.LoadConfiguration();
        TagStore restarted = TagStore.Load(fixture.Root, restartedConfiguration, fixture.Trace,
            randomBytes: _ => throw new InvalidOperationException("Restart must reuse the published mapping."));
        TagMapping reused = restarted.GetOrCreate(restartedConfiguration.Resolve("alias@example.com"), "person@example.net;", "restart");
        Assert.Equal(mapping.TagAddress, reused.TagAddress);
        Assert.Equal(senderId, Assert.Single(restarted.Mappings).Key.SenderId);
        Assert.Equal(expectedIdentity, File.ReadAllBytes(Path.Combine(reused.DirectoryPath, "sender-id.txt")));
    }

    // Clean but different references never acquire case folding, whitespace trimming, or Unicode normalization.
    [Theory]
    [InlineData("Sales", "sales")]
    [InlineData("Sales", " Sales")]
    [InlineData("Équipe", "E\u0301quipe")]
    [InlineData("Sales", "Other")]
    [InlineData("Sales", "\uFEFFSales")]
    public void PointerAndMappingMustNameTheExactSenderId(string senderId, string differentId)
    {
        using var fixture = new StoreTestFixture();
        Directory.Move(fixture.ProfileDirectory, Path.Combine(fixture.Root, "senders", "sender-ids", senderId));
        fixture.WriteIndex("auth@example.com", senderId);
        SenderConfiguration configuration = fixture.LoadConfiguration();
        fixture.WriteMapping("sender-00000@tags.example.com", "person@example.net;", differentId);
        Assert.Contains("missing sender", Assert.Throws<StartupConfigurationException>(() =>
            TagStore.Load(fixture.Root, configuration, fixture.Trace)).Message);

        fixture.WriteIndex("auth@example.com", differentId);
        Assert.Contains("missing sender-id", Assert.Throws<StartupConfigurationException>(fixture.LoadConfiguration).Message);
    }

    // Unsafe text is tested inside files so fixture setup never attempts to create an invalid Windows path.
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("C:\\outside")]
    [InlineData("name:stream")]
    [InlineData("name\0tail")]
    [InlineData("name\ntail")]
    [InlineData("name\ttail")]
    [InlineData("name*")]
    [InlineData("name?")]
    [InlineData("name\"tail")]
    [InlineData("name|tail")]
    [InlineData("name<tail")]
    [InlineData("name>tail")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("LPT²")]
    [InlineData("name.")]
    [InlineData("name ")]
    public void UnsafeSenderReferencesAreRejectedInAuthAndMappingFiles(string senderId)
    {
        using var fixture = new StoreTestFixture();
        SenderConfiguration configuration = fixture.LoadConfiguration();
        string mapping = fixture.WriteMapping("sender-00000@tags.example.com", "person@example.net;", senderId);
        Assert.Throws<StartupConfigurationException>(() =>
            SenderConfiguration.ReadSenderId(Path.Combine(mapping, "sender-id.txt")));
        Assert.Throws<StartupConfigurationException>(() => TagStore.Load(fixture.Root, configuration, fixture.Trace));

        fixture.WriteIndex("auth@example.com", senderId);
        Assert.Throws<StartupConfigurationException>(fixture.LoadConfiguration);
    }

    // Filesystem length limits remain filesystem outcomes rather than a new sender-identity format restriction.
    [Fact]
    public void ReadingSenderIdDoesNotImposeAnApplicationLengthLimit()
    {
        using var fixture = new StoreTestFixture();
        string senderId = new('a', 500);
        fixture.WriteIndex("auth@example.com", senderId + "\r\n");
        Assert.Equal(senderId, SenderConfiguration.ReadSenderId(
            Path.Combine(fixture.Root, "senders", "auth-addresses", "auth@example.com", "sender-id.txt")));
    }
}
