using System.Text;
using SmSorter;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class AuthAliasTests
{
    // Both indexed aliases divert and share permanent individual/group tags without rewriting their distinct HDR auth bytes.
    [Theory]
    [InlineData("alice@example.net")]
    [InlineData("alice@example.net,bob@example.net")]
    public void IndexedAliasesShareTagsWithinInvocationAndAcrossRestart(string recipients)
    {
        using var fixture = new ProcessorFixture();
        string alias = Path.Combine(fixture.DataDirectory, "senders", "auth-addresses", "alias@example.com");
        Directory.CreateDirectory(alias);
        File.WriteAllText(Path.Combine(alias, "sender-id.txt"), ProcessorFixture.SenderId);
        var sorter = new SorterProcessor(fixture.DataDirectory, fixture.SpoolDirectory, fixture.Errors);
        Dictionary<string, string> tags;
        using (ProcessorHarness first = fixture.Open())
        {
            ProcessAlias(fixture, first, sorter, "first", "Auth@Example.COM", recipients);
            tags = first.Tags.Mappings.ToDictionary(item => item.Key.RecipientId, item => item.Value.TagAddress);
            ProcessAlias(fixture, first, sorter, "second", "Alias@Example.COM", recipients);
            AssertMappings(tags, first);
        }

        using (ProcessorHarness restarted = fixture.Open(random: _ => throw new InvalidOperationException("Aliases must reuse existing tags.")))
        {
            ProcessAlias(fixture, restarted, sorter, "third", "ALIAS@example.com", recipients);
            AssertMappings(tags, restarted);
        }
        Assert.Equal(recipients.Contains(',') ? 3 : 1, tags.Count);
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        Assert.Empty(fixture.Errors.ToString());
    }

    // Changing the sole private address leaves existing tags reusable and the previous value becomes ordinary pass-through data.
    [Fact]
    public void PrivateAddressChangePreservesMappingsAndDoesNotRetireOldAddress()
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMessage("before");
        string tag;
        using (ProcessorHarness before = fixture.Open())
        {
            Assert.Equal(MessageOutcome.Succeeded, before.Processor.Process("before"));
            tag = Assert.Single(before.Tags.Mappings).Value.TagAddress;
        }

        const string replacement = "new-private@example.com";
        fixture.WriteProfile("private-address.txt", replacement);
        var previous = fixture.WriteMessage("previous", ProcessorFixture.Header("unparsed recipients"));
        fixture.WriteMessage("current", ProcessorFixture.Header(sender: replacement),
            ProcessorFixture.Message().Replace(ProcessorFixture.Private, replacement, StringComparison.Ordinal));
        using ProcessorHarness restarted = fixture.Open(random: _ => throw new InvalidOperationException("Private rotation must reuse the stable sender mapping."));

        Assert.Equal(MessageOutcome.Succeeded, restarted.Processor.Process("previous"));
        Assert.Equal(previous.Hdr, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "previous.hdr")));
        Assert.Equal(previous.Eml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "previous.eml")));
        Assert.Equal(MessageOutcome.Succeeded, restarted.Processor.Process("current"));
        Assert.Equal(tag, Assert.Single(restarted.Tags.Mappings).Value.TagAddress);
        Assert.Contains($"From: \"Fixture Sender\" <{tag}>\r\n",
            File.ReadAllText(Path.Combine(fixture.SpoolDirectory, "currentc1.eml")));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        Assert.Empty(fixture.Errors.ToString());
    }

    // Route one complete synthetic pair through the actual sorter and tagger and verify its output authentication metadata.
    private static void ProcessAlias(ProcessorFixture fixture, ProcessorHarness harness, SorterProcessor sorter,
        string basename, string auth, string recipients)
    {
        var original = fixture.WriteMessage(basename, ProcessorFixture.Header(recipients, auth: auth));
        string proc = Path.Combine(fixture.SpoolDirectory, "proc");
        foreach (string extension in new[] { ".eml", ".hdr" })
            File.Move(Path.Combine(fixture.ProcessDirectory, basename + extension), Path.Combine(proc, basename + extension));

        Assert.Equal(MessageOutcome.Succeeded, sorter.Process(basename));
        Assert.Equal(original.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, basename + ".hdr")));
        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process(basename));
        string[] addresses = recipients.Split(',');
        for (int index = 0; index < addresses.Length; index++)
        {
            string tag = harness.Tags.Mappings[(ProcessorFixture.SenderId, addresses[index] + ";")].TagAddress;
            byte[] hdr = File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, $"{basename}c{index + 1}.hdr"));
            string eml = File.ReadAllText(Path.Combine(fixture.SpoolDirectory, $"{basename}c{index + 1}.eml"));
            Assert.Contains("\r\nauth: " + auth + "\r\n", Encoding.ASCII.GetString(hdr));
            Assert.Contains($"From: \"Fixture Sender\" <{tag}>\r\n", eml);
            if (addresses.Length > 1)
            {
                string group = harness.Tags.Mappings[(ProcessorFixture.SenderId, string.Join(';', addresses) + ";")].TagAddress;
                Assert.Contains($"Reply-To: \"Fixture Sender\" <{group}>\r\n", eml);
            }
        }
    }

    // Compare the complete permanent recipient-to-tag view after another alias or a fresh startup has used it.
    private static void AssertMappings(Dictionary<string, string> expected, ProcessorHarness harness)
    {
        Assert.Equal(expected.Count, harness.Tags.Mappings.Count);
        foreach ((string recipient, string address) in expected)
            Assert.Equal(address, harness.Tags.Mappings[(ProcessorFixture.SenderId, recipient)].TagAddress);
    }
}
