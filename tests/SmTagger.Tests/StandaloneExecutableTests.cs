using System.Diagnostics;
using System.Text;

namespace SmTagger.Tests;

public sealed class StandaloneExecutableTests
{
    // Proves one relocated sorter EXE preserves nonenrolled mail without any adjacent runtime files.
    [StandaloneFact]
    public async Task StandaloneSorterPassesNonenrolledMailByteForByte()
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-sorter");
        byte[] body = [0, 255, 13, 10, .. Encoding.ASCII.GetBytes("Opaque synthetic body")];
        var originals = fixture.WriteMessage("unrelated",
            ProcessorFixture.Header(auth: "not-enrolled@example.com"), body: body);
        MoveToSorter(fixture, "unrelated");

        var result = await RunAsync(binaryDirectory, "sm-sorter", fixture,
            [fixture.DataDirectory, fixture.SpoolDirectory, "unrelated"]);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Empty(result.Error);
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "unrelated.hdr")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, "unrelated.eml")));
        Assert.Empty(Directory.GetFiles(fixture.ProcessDirectory));
        Assert.False(Directory.Exists(Path.Combine(fixture.DataDirectory, "tag-addresses")));
        Assert.Equal(["sm-sorter.exe"], Directory.GetFiles(binaryDirectory).Select(Path.GetFileName));
    }

    // Exercises relocated EXEs through byte-preserving diversion, real rewriting, and two-recipient fan-out.
    [StandaloneFact]
    public async Task StandaloneExecutablesDivertAndRewriteEnrolledMail()
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-sorter", "sm-tagger");
        byte[] body = [0, 255, 13, 10, 10, .. Encoding.ASCII.GetBytes("private@example.com stays in the body")];
        var originals = fixture.WriteMessage("-standalone",
            ProcessorFixture.Header("bob@example.net,alice@example.net"), body: body);
        MoveToSorter(fixture, "-standalone");

        var sorter = await RunAsync(binaryDirectory, "sm-sorter", fixture,
            [fixture.DataDirectory, fixture.SpoolDirectory, "-standalone"]);
        Assert.True(sorter.ExitCode == 0, sorter.Error);
        Assert.Empty(sorter.Error);
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "-standalone.hdr")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "-standalone.eml")));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));

        var tagger = await RunAsync(binaryDirectory, "sm-tagger", fixture,
            [fixture.DataDirectory, "-keep", fixture.SpoolDirectory, "-standalone"]);
        Assert.True(tagger.ExitCode == 0, tagger.Error);
        Assert.Empty(tagger.Error);
        Assert.Equal(2, Directory.GetFiles(fixture.SpoolDirectory, "*.hdr").Length);
        Assert.Equal(2, Directory.GetFiles(fixture.SpoolDirectory, "*.eml").Length);
        using var loaded = fixture.Open();
        Assert.Equal(3, loaded.Tags.Mappings.Count);
        string group = loaded.Tags.Mappings[(ProcessorFixture.SenderId, "alice@example.net;bob@example.net;")].TagAddress;
        string[] recipients = ["alice@example.net", "bob@example.net"];
        for (int index = 0; index < recipients.Length; index++)
        {
            string individual = loaded.Tags.Mappings[(ProcessorFixture.SenderId, recipients[index] + ";")].TagAddress;
            string basename = "-standalone-" + (index + 1);
            Assert.Equal(Encoding.ASCII.GetBytes(ProcessorFixture.Header(recipients[index], individual)),
                File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, basename + ".hdr")));
            string expectedHeaders = ProcessorFixture.Message()
                .Replace("Return-Path: <private@example.com>\r\n", "", StringComparison.Ordinal)
                .Replace("From: \"Fixture Sender\" <private@example.com>\r\n",
                    $"From: \"Fixture Sender\" <{individual}>\r\nReply-To: \"Fixture Sender\" <{group}>\r\n",
                    StringComparison.Ordinal);
            byte[] expectedEml = [.. Encoding.ASCII.GetBytes(expectedHeaders), .. body];
            Assert.Equal(expectedEml, File.ReadAllBytes(Path.Combine(fixture.SpoolDirectory, basename + ".eml")));
        }

        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "-standalone.hdr.in")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "-standalone.eml.in")));
        Assert.Equal(["sm-sorter.exe", "sm-tagger.exe"],
            Directory.GetFiles(binaryDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    // Checks retained originals on a real contract failure and continued publication when logging cannot open.
    [StandaloneFact]
    public async Task StandaloneTaggerRetainsInvalidFromAndSurvivesLoggingFailure()
    {
        using var fixture = new ProcessorFixture();
        string binaryDirectory = CopyExecutables(fixture, "sm-tagger");
        Directory.CreateDirectory(Path.Combine(fixture.DataDirectory, "log.txt"));
        var originals = fixture.WriteMessage("invalid", eml: ProcessorFixture.Message(""));

        var invalid = await RunAsync(binaryDirectory, "sm-tagger", fixture,
            [fixture.DataDirectory, "-log", fixture.SpoolDirectory, "invalid"]);

        Assert.Equal(1, invalid.ExitCode);
        Assert.Contains("exactly one From", invalid.Error);
        Assert.Contains("ERROR logging to", invalid.Error);
        Assert.Equal(originals.Hdr, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "invalid.hdr.err")));
        Assert.Equal(originals.Eml, File.ReadAllBytes(Path.Combine(fixture.ProcessDirectory, "invalid.eml.err")));
        Assert.Contains("exactly one From", File.ReadAllText(Path.Combine(fixture.ProcessDirectory, "invalid.err")));
        Assert.Empty(Directory.GetFiles(fixture.SpoolDirectory));
        Assert.False(Directory.Exists(Path.Combine(fixture.DataDirectory, "tag-addresses")));

        fixture.WriteMessage("valid");
        var valid = await RunAsync(binaryDirectory, "sm-tagger", fixture,
            [fixture.DataDirectory, "-log", fixture.SpoolDirectory, "valid"]);

        Assert.True(valid.ExitCode == 0, valid.Error);
        Assert.Contains("ERROR logging to", valid.Error);
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "valid-1.hdr")));
        Assert.True(File.Exists(Path.Combine(fixture.SpoolDirectory, "valid-1.eml")));
        Assert.False(File.Exists(Path.Combine(fixture.ProcessDirectory, "valid.err")));
    }

    // Copies only requested EXEs into a fresh isolated directory, leaving all publish-folder sidecars behind.
    private static string CopyExecutables(ProcessorFixture fixture, params string[] applications)
    {
        string directory = Path.Combine(fixture.Root, "standalone-bin");
        Directory.CreateDirectory(directory);
        foreach (string application in applications)
        {
            File.Copy(Path.Combine(ReleaseFactAttribute.ReleaseDirectory("standalone"), application + ".exe"),
                Path.Combine(directory, application + ".exe"));
        }

        return directory;
    }

    // Publishes the synthetic input to the sorter queue with EML present before its plain HDR.
    private static void MoveToSorter(ProcessorFixture fixture, string basename)
    {
        foreach (string extension in new[] { ".eml", ".hdr" })
        {
            File.Move(Path.Combine(fixture.ProcessDirectory, basename + extension),
                Path.Combine(fixture.SpoolDirectory, "proc", basename + extension));
        }
    }

    // Launches a relocated bundle with bounded lifetime, drained pipes, and its own fresh extraction root.
    private static async Task<ProcessResult> RunAsync(string binaryDirectory, string application,
        ProcessorFixture fixture, string[] arguments)
    {
        string extractionDirectory = Path.Combine(fixture.Root, "extract-" + Guid.NewGuid().ToString("N"));
        string hostTracePath = extractionDirectory + ".host-trace.txt";
        Directory.CreateDirectory(extractionDirectory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(extractionDirectory));
        var info = new ProcessStartInfo(Path.Combine(binaryDirectory, application + ".exe"))
        {
            WorkingDirectory = fixture.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (string variable in info.Environment.Keys.Where(name => name.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            info.Environment.Remove(variable);
        }

        info.Environment.Remove("DOTNET_HOST_PATH");
        info.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = extractionDirectory;
        info.Environment["DOTNET_HOST_TRACE"] = "1";
        info.Environment["DOTNET_HOST_TRACEFILE"] = hostTracePath;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start standalone executable.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(10));
            var result = new ProcessResult(process.ExitCode, await output, await error);
            string hostTrace = File.Exists(hostTracePath) ? File.ReadAllText(hostTracePath) : "";
            string details = $"Standalone {application} exit code: {result.ExitCode}. " +
                $"Standard error: {result.Error}. Standard output: {result.Output}. " +
                "Host trace: " + string.Join(Environment.NewLine, hostTrace.Split('\n').Where(line =>
                    line.Contains("bundle", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("self-contained", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("CoreCLR", StringComparison.Ordinal)).TakeLast(20));
            Assert.True(hostTrace.Contains("Detected Single-File app bundle", StringComparison.Ordinal), details);
            Assert.True(hostTrace.Contains("Executing as a self-contained app", StringComparison.Ordinal), details);
            // Some runtime packs embed CoreCLR in the native host instead of extracting a separate DLL.
            bool extractedRuntime = Directory.EnumerateFiles(extractionDirectory, "coreclr.dll", SearchOption.AllDirectories).Any();
            bool embeddedRuntime = hostTrace.Contains("CoreCLR path = '', CoreCLR dir = ''", StringComparison.Ordinal);
            Assert.True(extractedRuntime || embeddedRuntime, details);
            return result;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(cleanupTimeout.Token);
            }

            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}

internal sealed class StandaloneFactAttribute : FactAttribute
{
    // Skips explicitly until both Windows standalone release bundles have been published.
    public StandaloneFactAttribute()
    {
        string directory = ReleaseFactAttribute.ReleaseDirectory("standalone");
        if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(directory, "sm-sorter.exe")) ||
            !File.Exists(Path.Combine(directory, "sm-tagger.exe")))
        {
            Skip = "Run scripts/Publish-Release.ps1 on Windows before standalone executable checks.";
        }
    }
}
