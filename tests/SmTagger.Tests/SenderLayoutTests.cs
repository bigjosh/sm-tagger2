using SmSorter;
using SmTagger.Engine;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class SenderLayoutTests
{
    // A complete former layout cannot replace either required nested configuration root.
    [Fact]
    public void OldLayoutOnlyFailsTaggerStartupWithoutFallback()
    {
        using var fixture = new StoreTestFixture();
        MoveToOldLayout(fixture);

        Assert.Throws<StartupConfigurationException>(fixture.LoadConfiguration);

        Assert.Equal(StoreTestFixture.SenderId,
            File.ReadAllText(Path.Combine(fixture.Root, "senders", "auth@example.com", "sender-id.txt")));
        Assert.Equal("secret@example.com",
            File.ReadAllText(Path.Combine(fixture.Root, "profiles", StoreTestFixture.SenderId, "private-address.txt")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "senders", "auth-addresses")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "senders", "sender-ids")));
    }

    // The tagger requires both nested roots even when the existing counterpart has no entries.
    [Theory]
    [InlineData("auth-addresses", "sender-ids")]
    [InlineData("sender-ids", "auth-addresses")]
    public void TaggerRequiresBothNestedRootsForEmptyConfiguration(string presentRoot, string missingRoot)
    {
        using SorterTestDirectory tree = new();
        Directory.CreateDirectory(tree.Data("senders/" + presentRoot));
        using TraceLog trace = TraceLog.Open(tree.Data(""), enabled: false, stderr: tree.Errors);

        StartupConfigurationException error = Assert.Throws<StartupConfigurationException>(() =>
            SenderConfiguration.Load(tree.Data(""), trace));

        DirectoryNotFoundException missing = Assert.IsType<DirectoryNotFoundException>(error.InnerException);
        Assert.Contains(missingRoot, missing.Message);
        Assert.False(Directory.Exists(tree.Data("senders/" + missingRoot)));
    }

    // Old routing evidence cannot turn an unavailable required auth-address root into a safe classification.
    [Fact]
    public void OldLayoutOnlyFailsSorterRoutingWithoutFallback()
    {
        using var fixture = new StoreTestFixture();
        MoveToOldLayout(fixture);
        using SorterTestDirectory tree = new();
        byte[] hdr = tree.AddMessage("old-layout", "auth: auth@example.com\r\n");
        byte[] eml = File.ReadAllBytes(tree.Input("old-layout.eml"));
        var processor = new SorterProcessor(fixture.Root, tree.Spool(""), tree.Errors);

        Assert.Equal(MessageOutcome.Failed, processor.Process("old-layout"));

        Assert.Equal(hdr, File.ReadAllBytes(tree.Input("old-layout.hdr.sort")));
        Assert.Equal(eml, File.ReadAllBytes(tree.Input("old-layout.eml")));
        Assert.Contains("operation=\"look up authenticated enrollment\"", File.ReadAllText(tree.Input("old-layout.sort.err")));
        Assert.False(Directory.Exists(tree.Work("process")));
        Assert.False(File.Exists(tree.Spool("old-layout.hdr")));
    }

    // Once the required index exists, unrelated former-layout entries cannot opt an address into tagging.
    [Fact]
    public void EmptyNewIndexDoesNotFallBackToOldAuthDirectory()
    {
        using SorterTestDirectory tree = new();
        Directory.CreateDirectory(tree.Data("senders/auth-addresses"));
        Directory.CreateDirectory(tree.Data("senders/account@example.com"));
        File.WriteAllText(tree.Data("senders/account@example.com/sender-id.txt"), StoreTestFixture.SenderId);
        byte[] hdr = tree.AddMessage("new-index", "auth: account@example.com\r\n");

        Assert.Equal(MessageOutcome.Succeeded, tree.Processor.Process("new-index"));

        Assert.Equal(hdr, File.ReadAllBytes(tree.Spool("new-index.hdr")));
        Assert.False(Directory.Exists(tree.Data("senders/sender-ids")));
        Assert.False(Directory.Exists(tree.Work("process")));
        Assert.Equal(StoreTestFixture.SenderId, File.ReadAllText(tree.Data("senders/account@example.com/sender-id.txt")));
    }

    // Auth indexes authoritatively select their target record without a second auth declaration in that record.
    [Fact]
    public void NewAuthIndexSelectsAnyExistingSenderRecord()
    {
        using var fixture = new StoreTestFixture();
        fixture.AddProfile(StoreTestFixture.SecondSenderId, "other@example.com", "other-private@example.com");
        fixture.WriteIndex("auth@example.com", StoreTestFixture.SecondSenderId);

        SenderConfiguration configuration = fixture.LoadConfiguration();

        Assert.Equal(StoreTestFixture.SecondSenderId, configuration.Resolve("auth@example.com").SenderId);
        Assert.Same(configuration.Resolve("other@example.com"), configuration.Resolve("auth@example.com"));
        Assert.Equal(2, configuration.Profiles.Count);
    }

    // The sorter examines only the nested index directory even when its identity file cannot be read.
    [Fact]
    public void SorterDoesNotReadSenderPointerFromNewIndex()
    {
        using SorterTestDirectory tree = new();
        Directory.CreateDirectory(tree.Data("senders/auth-addresses/account@example.com"));
        string pointer = tree.Data("senders/auth-addresses/account@example.com/sender-id.txt");
        File.WriteAllBytes(pointer, [255, 0, 42]);
        byte[] hdr = tree.AddMessage("locked-pointer", "auth: account@example.com\r\n");

        using (FileStream unavailablePointer = new(pointer, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(MessageOutcome.Succeeded, tree.Processor.Process("locked-pointer"));
        }

        Assert.Equal(hdr, File.ReadAllBytes(tree.Work("process/locked-pointer.hdr")));
        Assert.Equal(new byte[] { 255, 0, 42 }, File.ReadAllBytes(pointer));
        Assert.False(Directory.Exists(tree.Data("senders/sender-ids")));
        Assert.Empty(tree.Errors.ToString());
    }

    // Relocate only this isolated fixture into the former structure without changing its identity bytes.
    private static void MoveToOldLayout(StoreTestFixture fixture)
    {
        string indexRoot = Path.Combine(fixture.Root, "senders", "auth-addresses");
        Directory.Move(Path.Combine(indexRoot, "auth@example.com"), Path.Combine(fixture.Root, "senders", "auth@example.com"));
        Directory.Delete(indexRoot);
        Directory.Move(Path.Combine(fixture.Root, "senders", "sender-ids"), Path.Combine(fixture.Root, "profiles"));
    }
}
