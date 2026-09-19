using System.Text;
using SmTagger.Engine;
using SmTagger.Mail;
using SmTagger.Shared;
using Xunit.Sdk;

namespace SmTagger.Tests;

public sealed class LocalSamplesFactAttribute : FactAttribute
{
    // Keep private server captures optional and outside portable source-controlled fixtures.
    public LocalSamplesFactAttribute()
    {
        if (LocalSampleFiles.FindDirectory() is null)
        {
            Skip = "Private local samples are not present; portable synthetic fixtures run separately.";
        }
    }
}

public sealed class SampleCompatibilityTests
{
    // Check every captured pair with production framing, classification, mailbox, and MDN parsers.
    [LocalSamplesFact]
    [Trait("Category", "PrivateLocalSamples")]
    public void CapturesConformToClassifierAndMailFraming()
    {
        LocalSampleFiles.RunWithoutPrivateDiagnostics(() =>
        {
            IReadOnlyList<LocalSampleFiles.Capture> captures = LocalSampleFiles.Load();
            Assert.Equal(5, captures.Count);
            int authenticated = 0;
            int noAuth = 0;
            foreach (LocalSampleFiles.Capture capture in captures)
            {
                Assert.NotEmpty(PhysicalLines.ReadHdr(capture.Hdr).Lines);
                Assert.True(capture.Hdr.AsSpan().EndsWith("\r\n\r\n"u8));
                FastHdrResult classification = FastHdrClassifier.Classify(capture.Hdr);
                Assert.NotEqual(FastHdrKind.Unsafe, classification.Kind);
                if (classification.Kind == FastHdrKind.ValidAuth)
                {
                    authenticated++;
                    HdrDocument hdr = HdrDocument.Parse(capture.Hdr);
                    Assert.True(hdr.AuthAddress == classification.CanonicalAuth);
                    Assert.NotEmpty(hdr.ParseRecipients());
                }
                else
                {
                    noAuth++;
                }

                EmlDocument eml = EmlDocument.Parse(capture.Eml);
                Assert.Single(eml.FromFields);
                Assert.Single(eml.FromFields[0].Addresses);
                Assert.Single(eml.ReplyToFields);
                Assert.Single(eml.ReplyToFields[0].Addresses);
                Assert.False(eml.IsMdn());
                capture.AssertOriginalsUnchanged();
            }

            Assert.Equal(3, authenticated);
            Assert.Equal(2, noAuth);
        });
    }

    // Pass authenticated raw pairs byte-for-byte with an unrelated synthetic private identity.
    [LocalSamplesFact]
    [Trait("Category", "PrivateLocalSamples")]
    public void AuthenticatedCapturesPassUnchangedWithoutPrivateMatch()
    {
        LocalSampleFiles.RunWithoutPrivateDiagnostics(() =>
        {
            foreach (LocalSampleFiles.Capture capture in LocalSampleFiles.LoadAuthenticated())
            {
                HdrDocument hdr = HdrDocument.Parse(capture.Hdr);
                const string privateAddress = "synthetic-private@fixture.example";
                Assert.DoesNotContain(privateAddress, hdr.SenderAddresses);
                Assert.DoesNotContain(privateAddress, EmlDocument.Parse(capture.Eml).SenderAddresses);
                using LocalSampleFixture fixture = new(hdr.AuthAddress, privateAddress);
                fixture.WriteCapture(capture);
                using ProcessorHarness harness = fixture.Open();
                Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("capture"));
                Assert.True(File.ReadAllBytes(fixture.Spool("capture.hdr")).AsSpan().SequenceEqual(capture.Hdr));
                Assert.True(File.ReadAllBytes(fixture.Spool("capture.eml")).AsSpan().SequenceEqual(capture.Eml));
                Assert.Empty(harness.Tags.Mappings);
                Assert.Empty(Directory.EnumerateFiles(fixture.ProcessDirectory));
                capture.AssertOriginalsUnchanged();
            }
        });
    }

    // Activate via the distinct captured Reply-To identity while preserving current-client formatting and bodies.
    [LocalSamplesFact]
    [Trait("Category", "PrivateLocalSamples")]
    public void CapturedReplyToActivatesSingleAndMultipleRecipientPublication()
    {
        LocalSampleFiles.RunWithoutPrivateDiagnostics(() =>
        {
            foreach (LocalSampleFiles.Capture capture in LocalSampleFiles.LoadAuthenticated())
            {
                HdrDocument originalHdr = HdrDocument.Parse(capture.Hdr);
                EmlDocument originalEml = EmlDocument.Parse(capture.Eml);
                string privateAddress = originalEml.ReplyToFields.Single().Addresses.Single().CanonicalAddress;
                Assert.True(privateAddress != originalHdr.AuthAddress);
                IReadOnlyList<Recipient> recipients = originalHdr.ParseRecipients();
                using LocalSampleFixture fixture = new(originalHdr.AuthAddress, privateAddress);
                fixture.WriteCapture(capture);
                using ProcessorHarness harness = fixture.Open();
                Assert.Equal(MessageOutcome.Succeeded, harness.Processor.Process("capture"));
                string? firstReplyTag = null;
                for (int index = 0; index < recipients.Count; index++)
                {
                    string basename = "capturec" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    byte[] outputBytes = File.ReadAllBytes(fixture.Spool(basename + ".eml"));
                    EmlDocument output = EmlDocument.Parse(outputBytes);
                    HdrDocument childHdr = HdrDocument.Parse(File.ReadAllBytes(fixture.Spool(basename + ".hdr")));
                    Assert.True(LocalSampleFiles.Body(outputBytes).SequenceEqual(LocalSampleFiles.Body(capture.Eml)));
                    Assert.True(childHdr.AuthAddress == originalHdr.AuthAddress);
                    Assert.Single(childHdr.ParseRecipients());
                    Assert.True(childHdr.ParseRecipients()[0].CanonicalAddress == recipients[index].CanonicalAddress);
                    Assert.True(output.FromFields[0].Addresses[0].CanonicalAddress == originalEml.FromFields[0].Addresses[0].CanonicalAddress);
                    Assert.DoesNotContain(output.Fields, field => field.Name.Equals("Return-Path", StringComparison.OrdinalIgnoreCase));
                    Assert.DoesNotContain(privateAddress, output.SenderAddresses);
                    string replyTag = output.ReplyToFields.Single().Addresses.Single().CanonicalAddress;
                    Assert.EndsWith("@reply.example", replyTag, StringComparison.Ordinal);
                    if (firstReplyTag is not null && recipients.Count > 1)
                    {
                        Assert.True(replyTag == firstReplyTag);
                    }

                    firstReplyTag = replyTag;
                }

                Assert.Equal(recipients.Count + (recipients.Count > 1 ? 1 : 0), harness.Tags.Mappings.Count);
                Assert.Empty(Directory.EnumerateFiles(fixture.ProcessDirectory));
                capture.AssertOriginalsUnchanged();
            }
        });
    }
}

internal static class LocalSampleFiles
{
    // Locate the private source directory from the test output without embedding a user's absolute path.
    public static string? FindDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string samples = Path.Combine(directory.FullName, "samples");
            if (File.Exists(Path.Combine(directory.FullName, "spec.md")) && Directory.Exists(samples))
            {
                return samples;
            }
        }

        return null;
    }

    // Read captures in place without copying private content into repository fixtures or build output.
    public static IReadOnlyList<Capture> Load()
    {
        string directory = FindDirectory() ?? throw new InvalidOperationException("Local samples are absent.");
        return Directory.EnumerateFiles(directory)
            .Where(path => Path.GetExtension(path).Equals(".hdr", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(path => new Capture(path, Path.ChangeExtension(path, ".eml"),
                File.ReadAllBytes(path), File.ReadAllBytes(Path.ChangeExtension(path, ".eml"))))
            .ToArray();
    }

    // Select only captured authenticated pairs for the tagger's private input queue tests.
    public static IEnumerable<Capture> LoadAuthenticated()
    {
        return Load().Where(capture => FastHdrClassifier.Classify(capture.Hdr).Kind == FastHdrKind.ValidAuth);
    }

    // Compare untouched body bytes without decoding or printing their private content.
    public static ReadOnlySpan<byte> Body(byte[] eml)
    {
        int boundary = eml.AsSpan().IndexOf("\r\n\r\n"u8);
        if (boundary < 0)
        {
            throw new InvalidDataException("A sample has no header/body boundary.");
        }

        return eml.AsSpan(boundary + 4);
    }

    // Ensure unexpected parser/configuration failures cannot disclose identities through test output.
    public static void RunWithoutPrivateDiagnostics(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            throw new XunitException("Private local sample compatibility failed (" + exception.GetType().Name
                + "); inspect the private captures locally. Raw values and diagnostic messages are omitted.");
        }
    }

    internal sealed record Capture(string HdrPath, string EmlPath, byte[] Hdr, byte[] Eml)
    {
        // Verify that every processor experiment leaves both original private capture files byte-identical.
        public void AssertOriginalsUnchanged()
        {
            Assert.True(File.ReadAllBytes(HdrPath).AsSpan().SequenceEqual(Hdr));
            Assert.True(File.ReadAllBytes(EmlPath).AsSpan().SequenceEqual(Eml));
        }
    }
}

internal sealed class LocalSampleFixture : IDisposable
{
    private const string SenderId = "11111111-1111-4111-8111-111111111111";
    private readonly string temporaryRoot;
    private readonly string root;
    private readonly string dataDirectory;
    private readonly string spoolDirectory;
    private readonly StringWriter errors = new();
    public string ProcessDirectory { get; }

    // Provision raw auth plus a distinct chosen private identity only inside an isolated temporary tree.
    public LocalSampleFixture(string authAddress, string privateAddress)
    {
        temporaryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sm-tagger2-private-sample-tests"));
        root = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        dataDirectory = Path.Combine(root, "data");
        spoolDirectory = Path.Combine(root, "spool");
        ProcessDirectory = Path.Combine(spoolDirectory, "proc", "sm-tagger", "process");
        Directory.CreateDirectory(ProcessDirectory);
        Directory.CreateDirectory(spoolDirectory);
        string profile = Path.Combine(dataDirectory, "senders", "sender-ids", SenderId);
        string authIndex = Path.Combine(dataDirectory, "senders", "auth-addresses", authAddress);
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(authIndex);
        File.WriteAllText(Path.Combine(authIndex, "sender-id.txt"), SenderId);
        File.WriteAllText(Path.Combine(profile, "private-address.txt"), privateAddress);
        File.WriteAllText(Path.Combine(profile, "allow-mdn.txt"), "false");
        File.WriteAllText(Path.Combine(profile, "from-template.txt"), "sample-%@reply.example", new UTF8Encoding(false));
    }

    // Supply one working queue pair outside the repository while preserving original source bytes.
    public void WriteCapture(LocalSampleFiles.Capture capture)
    {
        File.WriteAllBytes(Path.Combine(ProcessDirectory, "capture.hdr"), capture.Hdr);
        File.WriteAllBytes(Path.Combine(ProcessDirectory, "capture.eml"), capture.Eml);
    }

    // Open production configuration, mapping, and processing code with deterministic test-only token bytes.
    public ProcessorHarness Open()
    {
        TraceLog trace = TraceLog.Open(null, stderr: errors);
        try
        {
            SenderConfiguration configuration = SenderConfiguration.Load(dataDirectory, trace);
            byte token = 0;
            TagStore tags = TagStore.Load(dataDirectory, configuration, trace,
                () => new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero),
                bytes => Array.Fill(bytes, ++token));
            return new ProcessorHarness(trace, configuration, tags,
                new TaggerProcessor(spoolDirectory, configuration, tags, trace));
        }
        catch
        {
            trace.Dispose();
            throw;
        }
    }

    // Resolve one generated output path without ever publishing to an actual server queue.
    public string Spool(string filename) => Path.Combine(spoolDirectory, filename);

    // Remove private temporary working files only after confirming the allocated cleanup boundary.
    public void Dispose()
    {
        string resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Private sample cleanup escaped its temporary root.");
        }

        Directory.Delete(resolved, recursive: true);
        errors.Dispose();
    }
}
