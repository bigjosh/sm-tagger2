using System.Text;
using SmTagger.Engine;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class ChildBasenameTests
{
    // Keep the exact parent spelling and decimal recipient order in every live, retained, and logged child name.
    [Theory]
    [InlineData("42615432")]
    [InlineData("-42615432")]
    [InlineData("00042615432")]
    [InlineData("opaque-parentc3")]
    public void FanOutUsesLowercaseCAndUnpaddedDecimalOrdinals(string parent)
    {
        using var fixture = new ProcessorFixture();
        string[] ordinals = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15", "16"];
        string[] recipients = ["r01@example.net", "r02@example.net", "r03@example.net", "r04@example.net",
            "r05@example.net", "r06@example.net", "r07@example.net", "r08@example.net", "r09@example.net",
            "r10@example.net", "r11@example.net", "r12@example.net", "r13@example.net", "r14@example.net",
            "r15@example.net", "r16@example.net"];
        byte[] body = [0, 255, 13, 10, .. Encoding.ASCII.GetBytes("preserved message body")];
        var originals = fixture.WriteMessage(parent, ProcessorFixture.Header(string.Join(',', recipients.Reverse())), body: body);
        using var harness = fixture.Open(keep: true, log: true);

        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process(parent));
        string[] children = ordinals.Select(ordinal => parent + "c" + ordinal).ToArray();
        Assert.Equal(children.SelectMany(child => new[] { child + ".eml", child + ".hdr" }).Order(StringComparer.Ordinal),
            Directory.GetFiles(fixture.SpoolDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, parent + ".hdr.in")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, parent + ".eml.in")));
        string groupId = string.Join(';', recipients) + ";";
        var group = harness.Tags.Mappings[(ProcessorFixture.SenderId, groupId)];
        using var traceReader = new StreamReader(new FileStream(Path.Combine(fixture.DataDirectory, "log.txt"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        string trace = traceReader.ReadToEnd();
        for (int index = 0; index < children.Length; index++)
        {
            string child = children[index];
            byte[] hdr = File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, child + ".hdr"));
            byte[] eml = File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, child + ".eml"));
            Assert.Equal(hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, child + ".hdr.out")));
            Assert.Equal(eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, child + ".eml.out")));
            Assert.Equal(body, eml[^body.Length..]);
            Assert.Contains("\r\n" + recipients[index] + "\r\n", Encoding.ASCII.GetString(hdr));
            var mapping = harness.Tags.Mappings[(ProcessorFixture.SenderId, recipients[index] + ";")];
            Assert.Equal("20260905T123456Z " + child + "\r\n", File.ReadAllText(Path.Combine(mapping.DirectoryPath, "tag-log.txt")));
            Assert.Contains(TraceLog.Quote(Path.Combine(fixture.SpoolDirectory, child + ".hdr")), trace);
        }

        Assert.Equal(children.Select(child => "20260905T123456Z " + child),
            File.ReadAllLines(Path.Combine(group.DirectoryPath, "tag-log.txt")));
        Assert.Equal(34, Directory.GetFiles(fixture.ProcessDirectory).Length);
    }

    // A numeric parent's no-match pair keeps its original name and exact bytes without entering the child namespace.
    [Fact]
    public void PassThroughKeepsOriginalNumericBasename()
    {
        using var fixture = new ProcessorFixture();
        const string parent = "42615432";
        var originals = fixture.WriteMessage(parent, ProcessorFixture.Header(sender: "other@example.org"),
            ProcessorFixture.Message("From: other@example.org\r\n").Replace("<private@example.com>", "<>"));
        using var harness = fixture.Open(keep: true);

        Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process(parent));
        Assert.Equal(["42615432.eml", "42615432.hdr"],
            Directory.GetFiles(fixture.SpoolDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, parent + ".hdr")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, parent + ".eml")));
        Assert.Empty(harness.Tags.Mappings);
    }
}
