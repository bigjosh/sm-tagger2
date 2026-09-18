using System.Diagnostics;
using System.Text;

namespace SmTagger.Tests;

public sealed class BomExecutableTests
{
    // Exercise the published application folder through real process startup and BOM-preserving fan-out.
    [ReleaseFact]
    public Task ApplicationFolderTagsBomMessage() => VerifyAsync(ReleaseFactAttribute.ReleaseDirectory("sm-tagger"));

    // Exercise the self-contained single-file package through the same observable mail boundary.
    [StandaloneFact]
    public Task StandaloneTagsBomMessage() => VerifyAsync(ReleaseFactAttribute.ReleaseDirectory("standalone"));

    // Run one fresh two-recipient message and compare both emitted EMLs against an independent exact-byte expectation.
    private static async Task VerifyAsync(string binaryDirectory)
    {
        using var fixture = new ProcessorFixture();
        fixture.WriteMapping("tag-aaaaa@reply.example.com", "alice@example.net;");
        fixture.WriteMapping("tag-bbbbb@reply.example.com", "bob@example.net;");
        fixture.WriteMapping("tag-ccccc@reply.example.com", "alice@example.net;bob@example.net;");
        fixture.WriteMessage("bom", ProcessorFixture.Header("alice@example.net,bob@example.net"));
        const string from = "From: Synthetic <private@example.com>\r\n";
        const string tail = "To: visible@example.org\r\nSubject: published BOM regression\r\n\r\n";
        byte[] body = [0, 0xff, 0xef, 0xbb, 0xbf, 13, 10];
        byte[] original = [0xef, 0xbb, 0xbf, .. Encoding.ASCII.GetBytes("Return-Path: <private@example.com>\r\n" + from + tail), .. body];
        File.WriteAllBytes(Path.Combine(fixture.ProcessDirectory, "bom.eml"), original);
        var info = new ProcessStartInfo(Path.Combine(binaryDirectory, "sm-tagger.exe"))
        {
            WorkingDirectory = fixture.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { fixture.DataDirectory, "-keep", fixture.SpoolDirectory, "bom" })
            info.ArgumentList.Add(argument);
        info.Environment.Remove("DOTNET_ROOT");
        info.Environment.Remove("DOTNET_ROOT_X64");
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start published tagger.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await stdout);
            Assert.Empty(await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(10_000))
                    throw new TimeoutException("Owned published tagger did not terminate.");
            }
        }

        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "bom.eml.in")));
        string[] individualTags = ["tag-aaaaa@reply.example.com", "tag-bbbbb@reply.example.com"];
        for (int index = 0; index < individualTags.Length; index++)
        {
            string reply = from.Replace("From:", "Reply-To:", StringComparison.Ordinal)
                .Replace(ProcessorFixture.Private, "tag-ccccc@reply.example.com", StringComparison.Ordinal);
            byte[] expected = [0xef, 0xbb, 0xbf,
                .. Encoding.ASCII.GetBytes(from.Replace(ProcessorFixture.Private, individualTags[index], StringComparison.Ordinal) + reply + tail), .. body];
            string basename = "bom-" + (index + 1);
            Assert.Equal(expected, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, basename + ".eml")));
            Assert.Equal(expected, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, basename + ".eml.out")));
        }
        Assert.Equal(2, Directory.GetFiles(fixture.SpoolDirectory, "*.hdr").Length);
        Assert.Equal(2, Directory.GetFiles(fixture.SpoolDirectory, "*.eml").Length);
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory, "*.err"));
    }
}
