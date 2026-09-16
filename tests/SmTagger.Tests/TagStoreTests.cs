using SmTagger.Engine;

namespace SmTagger.Tests;

public sealed class TagStoreTests
{
    // A mapping is immediately reusable and reloadable using only its exact closed identity bytes.
    [Fact]
    public void PublishesCompleteIdentityAndReusesItWithinInvocationAndAfterRestart()
    {
        using var fixture = new StoreTestFixture();
        SenderConfiguration configuration = fixture.LoadConfiguration();
        int randomCalls = 0;
        TagStore store = TagStore.Load(fixture.Root, configuration, fixture.Trace, randomBytes: bytes =>
        {
            randomCalls++;
            new byte[] { 0, 33, 66, 99, 255 }.CopyTo(bytes, 0);
        });
        SenderProfile profile = configuration.Profiles[StoreTestFixture.SenderId];
        TagMapping first = store.GetOrCreate(profile, "person@example.net;", "parent");
        Assert.Equal("sender-0123z@tags.example.com", first.TagAddress);
        Assert.Equal(StoreTestFixture.SenderId, File.ReadAllText(Path.Combine(first.DirectoryPath, "sender-id.txt")));
        Assert.Equal("person@example.net;", File.ReadAllText(Path.Combine(first.DirectoryPath, "recipient-id.txt")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(first.DirectoryPath, "tag-log.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.Root, "staging")));
        Assert.Same(first, store.GetOrCreate(profile, "person@example.net;", "later"));
        Assert.Equal(1, randomCalls);
        TagStore restarted = TagStore.Load(fixture.Root, configuration, fixture.Trace,
            randomBytes: _ => throw new InvalidOperationException("Reloaded identity should avoid randomness."));
        Assert.Equal(first.TagAddress, restarted.GetOrCreate(profile, "person@example.net;", "restart").TagAddress);
    }

    // Collision retries move the same complete staging directory without changing an existing live record.
    [Fact]
    public void CollisionRetriesWithoutAdoptingOrReplacingExistingMapping()
    {
        using var fixture = new StoreTestFixture();
        string previous = fixture.WriteMapping("sender-00000@tags.example.com", "old@example.net;");
        SenderConfiguration configuration = fixture.LoadConfiguration();
        int proposals = 0;
        TagStore store = TagStore.Load(fixture.Root, configuration, fixture.Trace,
            randomBytes: bytes => Array.Fill(bytes, (byte)proposals++));
        TagMapping result = store.GetOrCreate(configuration.Profiles[StoreTestFixture.SenderId], "new@example.net;", "parent");
        Assert.Equal("sender-11111@tags.example.com", result.TagAddress);
        Assert.Equal(2, proposals);
        Assert.Equal("old@example.net;", File.ReadAllText(Path.Combine(previous, "recipient-id.txt")));
        Assert.Equal(2, store.Mappings.Count);
    }

    // The token proposal bound is exactly sixteen and exhaustion retains one inert identity record.
    [Fact]
    public void SixteenCollisionsFailWithoutPublishingASecondIdentity()
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteMapping("sender-00000@tags.example.com", "old@example.net;");
        SenderConfiguration configuration = fixture.LoadConfiguration();
        int proposals = 0;
        TagStore store = TagStore.Load(fixture.Root, configuration, fixture.Trace, randomBytes: bytes =>
        {
            proposals++;
            Array.Clear(bytes);
        });
        Assert.Throws<IOException>(() => store.GetOrCreate(configuration.Profiles[StoreTestFixture.SenderId], "new@example.net;", "parent"));
        Assert.Equal(16, proposals);
        Assert.Single(store.Mappings);
        string staging = Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Root, "staging")));
        Assert.Equal("new@example.net;", File.ReadAllText(Path.Combine(staging, "recipient-id.txt")));
        Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Root, "tag-addresses")));
    }

    // A non-directory destination is a publication failure, not a tag collision worth retrying.
    [Fact]
    public void NonDirectoryPublicationCollisionFailsOnFirstProposal()
    {
        using var fixture = new StoreTestFixture();
        string root = Path.Combine(fixture.Root, "tag-addresses");
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, "sender-00000@tags.example.com");
        File.WriteAllText(destination, "must survive");
        SenderConfiguration configuration = fixture.LoadConfiguration();
        int proposals = 0;
        TagStore store = TagStore.Load(fixture.Root, configuration, fixture.Trace, randomBytes: bytes =>
        {
            proposals++;
            Array.Clear(bytes);
        });
        Assert.Throws<IOException>(() => store.GetOrCreate(configuration.Profiles[StoreTestFixture.SenderId], "new@example.net;", "parent"));
        Assert.Equal(1, proposals);
        Assert.Equal("must survive", File.ReadAllText(destination));
        Assert.Empty(store.Mappings);
        Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Root, "staging")));
    }

    // The single proposed UUID is never reused or retried when staging evidence already exists.
    [Fact]
    public void ExistingUuidStagingFailsWithoutTouchingEvidence()
    {
        using var fixture = new StoreTestFixture();
        Guid id = Guid.Parse("c1234567-89ab-4cde-8f01-23456789abcd");
        string staging = Path.Combine(fixture.Root, "staging", id.ToString("D") + ".tagtmp");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "sender-id.txt"), "prior incomplete evidence");
        SenderConfiguration configuration = fixture.LoadConfiguration();
        TagStore store = TagStore.Load(fixture.Root, configuration, fixture.Trace,
            randomBytes: _ => throw new InvalidOperationException("Randomness must not run after a UUID collision."), uuid: () => id);
        Assert.Throws<IOException>(() => store.GetOrCreate(configuration.Profiles[StoreTestFixture.SenderId], "new@example.net;", "parent"));
        Assert.Equal("prior incomplete evidence", File.ReadAllText(Path.Combine(staging, "sender-id.txt")));
        Assert.Single(Directory.GetFileSystemEntries(staging));
    }

    // A cryptographic source failure has no weaker fallback and leaves completed staging inert.
    [Fact]
    public void RandomFailureRetainsCompleteUnpublishedIdentity()
    {
        using var fixture = new StoreTestFixture();
        SenderConfiguration configuration = fixture.LoadConfiguration();
        TagStore store = TagStore.Load(fixture.Root, configuration, fixture.Trace,
            randomBytes: _ => throw new InvalidOperationException("random source unavailable"));
        Assert.Throws<InvalidOperationException>(() => store.GetOrCreate(configuration.Profiles[StoreTestFixture.SenderId], "new@example.net;", "parent"));
        Assert.Empty(store.Mappings);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.Root, "tag-addresses")));
        string staging = Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Root, "staging")));
        Assert.Equal(StoreTestFixture.SenderId, File.ReadAllText(Path.Combine(staging, "sender-id.txt")));
        Assert.Equal("new@example.net;", File.ReadAllText(Path.Combine(staging, "recipient-id.txt")));
    }

    // Once a mapping is live, failure to update the in-memory authority is fatal and restart repairs only the view.
    [Fact]
    public void CacheInsertionFailureIsFatalButRestartLoadsThePublishedIdentity()
    {
        using var fixture = new StoreTestFixture();
        SenderConfiguration configuration = fixture.LoadConfiguration();
        TagStore store = TagStore.Load(fixture.Root, configuration, fixture.Trace, randomBytes: Array.Clear);
        store.BeforeCacheInsert = _ => throw new InvalidOperationException("insertion unavailable");
        Assert.Throws<MappingAvailabilityException>(() => store.GetOrCreate(configuration.Profiles[StoreTestFixture.SenderId], "new@example.net;", "parent"));
        Assert.Empty(store.Mappings);
        string published = Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Root, "tag-addresses")));
        Assert.Equal("new@example.net;", File.ReadAllText(Path.Combine(published, "recipient-id.txt")));
        TagStore restarted = TagStore.Load(fixture.Root, configuration, fixture.Trace);
        Assert.Single(restarted.Mappings);
        Assert.Equal("sender-00000@tags.example.com", restarted.GetOrCreate(configuration.Profiles[StoreTestFixture.SenderId], "new@example.net;", "restart").TagAddress);
    }

    // Live identity completeness does not depend on tag-log presence, format, or accessibility.
    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("directory")]
    public void UnusableTagLogsNeverInvalidateLiveMappings(string state)
    {
        using var fixture = new StoreTestFixture();
        string directory = fixture.WriteMapping("sender-00000@tags.example.com", "person@example.net;\r\n");
        string log = Path.Combine(directory, "tag-log.txt");
        if (state == "corrupt")
            File.WriteAllBytes(log, [255, 0, 13, 99]);
        if (state == "directory")
            Directory.CreateDirectory(log);
        TagStore store = TagStore.Load(fixture.Root, fixture.LoadConfiguration(), fixture.Trace);
        Assert.Single(store.Mappings);
        Assert.Equal("person@example.net;", Assert.Single(store.Mappings).Value.RecipientId);
        if (state == "corrupt")
            Assert.Equal(new byte[] { 255, 0, 13, 99 }, File.ReadAllBytes(log));
    }

    // Required identity fields determine completeness independently of any log that happens to exist.
    [Theory]
    [InlineData("sender-id.txt")]
    [InlineData("recipient-id.txt")]
    public void IncompleteLiveIdentityFailsStartup(string missing)
    {
        using var fixture = new StoreTestFixture();
        string directory = fixture.WriteMapping("sender-00000@tags.example.com", "person@example.net;");
        File.Delete(Path.Combine(directory, missing));
        File.WriteAllText(Path.Combine(directory, "tag-log.txt"), "20260905T010203Z old-child\r\n");
        Assert.Throws<StartupConfigurationException>(() => TagStore.Load(fixture.Root, fixture.LoadConfiguration(), fixture.Trace));
    }

    // Every persistent recipient representation must be canonical and unambiguous.
    [Theory]
    [InlineData("")]
    [InlineData("person@example.net")]
    [InlineData("PERSON@example.net;")]
    [InlineData("b@example.net;a@example.net;")]
    [InlineData("a@example.net;a@example.net;")]
    [InlineData("a%x@example.net;")]
    [InlineData("a@example.net;;")]
    [InlineData("a@example.net; ")]
    public void InvalidLiveRecipientIdentityFailsStartup(string recipientId)
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteMapping("sender-00000@tags.example.com", recipientId);
        Assert.Throws<StartupConfigurationException>(() => TagStore.Load(fixture.Root, fixture.LoadConfiguration(), fixture.Trace));
    }

    // Different tag addresses cannot claim the same stable sender/recipient pair.
    [Fact]
    public void DuplicateLiveKeysFailStartup()
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteMapping("sender-00000@tags.example.com", "person@example.net;");
        fixture.WriteMapping("sender-11111@tags.example.com", "person@example.net;");
        Assert.Contains("Duplicate live", Assert.Throws<StartupConfigurationException>(() =>
            TagStore.Load(fixture.Root, fixture.LoadConfiguration(), fixture.Trace)).Message);
    }

    // A live identity may refer only to a profile that survived complete startup validation.
    [Fact]
    public void OrphanLiveSenderFailsStartup()
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteMapping("sender-00000@tags.example.com", "person@example.net;", StoreTestFixture.SecondSenderId);
        Assert.Throws<StartupConfigurationException>(() => TagStore.Load(fixture.Root, fixture.LoadConfiguration(), fixture.Trace));
    }

    // A file at the mapping root is not evidence that no permanent mappings exist.
    [Fact]
    public void ExistingNonDirectoryMappingRootFailsStartup()
    {
        using var fixture = new StoreTestFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "tag-addresses"), "unexpected object");
        Assert.Throws<StartupConfigurationException>(() => TagStore.Load(fixture.Root, fixture.LoadConfiguration(), fixture.Trace));
    }

    // Inert tag staging is named but never validated, resumed, or deleted by startup.
    [Fact]
    public void CorruptInertStagingDoesNotJoinTheLiveAuthority()
    {
        using var fixture = new StoreTestFixture();
        string directory = Path.Combine(fixture.Root, "staging", "incomplete.tagtmp");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "sender-id.txt"), [255]);
        TagStore store = TagStore.Load(fixture.Root, fixture.LoadConfiguration(), fixture.Trace);
        Assert.Empty(store.Mappings);
        Assert.Contains("incomplete.tagtmp", fixture.Errors.ToString());
        Assert.Equal(new byte[] { 255 }, File.ReadAllBytes(Path.Combine(directory, "sender-id.txt")));
    }
}
