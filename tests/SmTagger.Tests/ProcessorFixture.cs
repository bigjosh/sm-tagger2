using System.Text;
using SmTagger.Engine;

namespace SmTagger.Tests;

internal sealed class ProcessorFixture : IDisposable
{
    public const string SenderId = "11111111-1111-4111-8111-111111111111";
    public const string Auth = "auth@example.com";
    public const string Private = "private@example.com";
    private readonly string temporaryRoot;

    public string Root { get; }
    public string DataDirectory { get; }
    public string ProcessDirectory => Path.Combine(DataDirectory, "process");
    public string SpoolDirectory { get; }
    public string ProfileDirectory => Path.Combine(DataDirectory, "profiles", SenderId);
    public StringWriter Errors { get; } = new();

    // Creates a private isolated filesystem and one complete synthetic enrolled profile.
    public ProcessorFixture()
    {
        temporaryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sm-tagger2-tests"));
        Root = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        DataDirectory = Path.Combine(Root, "data");
        SpoolDirectory = Path.Combine(Root, "spool");
        Directory.CreateDirectory(ProcessDirectory);
        Directory.CreateDirectory(Path.Combine(SpoolDirectory, "proc"));
        Directory.CreateDirectory(ProfileDirectory);
        Directory.CreateDirectory(Path.Combine(DataDirectory, "senders", Auth));
        File.WriteAllText(Path.Combine(DataDirectory, "senders", Auth, "sender-id.txt"), SenderId);
        WriteProfile("auth-address.txt", Auth);
        WriteProfile("private-address.txt", Private);
        WriteProfile("retired-auth-addresses.txt", "");
        WriteProfile("retired-private-addresses.txt", "");
        WriteProfile("from-template.txt", "tag-%@reply.example.com\r\nSynthetic fixture");
        WriteProfile("allow-mdn.txt", "false");
    }

    // Replaces a fixture configuration file before constructing its startup snapshot.
    public void WriteProfile(string filename, string value)
    {
        File.WriteAllText(Path.Combine(ProfileDirectory, filename), value, new UTF8Encoding(false));
    }

    // Publishes a complete synthetic mapping to exercise stable reuse and logging independently of allocation.
    public string WriteMapping(string address, string recipientId)
    {
        var directory = Path.Combine(DataDirectory, "tag-addresses", address);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "sender-id.txt"), SenderId);
        File.WriteAllText(Path.Combine(directory, "recipient-id.txt"), recipientId);
        return directory;
    }

    // Supplies a complete plain pair while retaining exact bytes for preservation assertions.
    public (byte[] Hdr, byte[] Eml) WriteMessage(string basename, string? hdr = null, string? eml = null,
        byte[]? body = null)
    {
        var hdrBytes = Encoding.ASCII.GetBytes(hdr ?? Header());
        var emlBytes = Encoding.ASCII.GetBytes(eml ?? Message());
        if (body is not null)
        {
            emlBytes = [.. emlBytes, .. body];
        }

        File.WriteAllBytes(Path.Combine(ProcessDirectory, basename + ".hdr"), hdrBytes);
        File.WriteAllBytes(Path.Combine(ProcessDirectory, basename + ".eml"), emlBytes);
        return (hdrBytes, emlBytes);
    }

    // Opens the same production configuration and tag store with deterministic cryptographic-source test bytes.
    public ProcessorHarness Open(bool keep = false, bool log = false, Action<byte[]>? random = null)
    {
        var trace = TraceLog.Open(DataDirectory, log, Errors);
        try
        {
            var configuration = SenderConfiguration.Load(DataDirectory, trace);
            byte counter = 0;
            var tags = TagStore.Load(DataDirectory, configuration, trace,
                () => new DateTimeOffset(2026, 9, 5, 12, 34, 56, TimeSpan.Zero),
                random ?? (bytes => Array.Fill(bytes, ++counter)));
            return new ProcessorHarness(trace, configuration, tags,
                new TaggerProcessor(DataDirectory, SpoolDirectory, configuration, tags, trace, keep));
        }
        catch
        {
            trace.Dispose();
            throw;
        }
    }

    // Builds bounded HDR framing with optional opaque repeated metadata.
    public static string Header(string recipients = "alice@example.net", string sender = Private,
        string auth = Auth, string extra = "")
    {
        return $"Written \r\n{sender}\r\n{recipients}\r\nauth: {auth}\r\nfrom: {sender}\r\n{extra}\r\n";
    }

    // Builds a synthetic ASCII EML header block with caller-controlled supported fields.
    public static string Message(string? from = "From: \"Fixture Sender\" <private@example.com>\r\n",
        string extra = "", string contentType = "text/plain", string body = "")
    {
        return $"Return-Path: <{Private}>\r\n{from}To: Visible <visible@example.org>\r\n" +
            $"Subject: Synthetic fixture\r\nMessage-ID: <fixture@example.com>\r\n{extra}" +
            $"Content-Type: {contentType}\r\n\r\n{body}";
    }

    // Removes only this fixture's verified temporary subtree after all streams and child processes close.
    public void Dispose()
    {
        var resolved = Path.GetFullPath(Root);
        if (!resolved.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Test cleanup escaped its designated temporary root.");
        }

        Directory.Delete(resolved, recursive: true);
        Errors.Dispose();
    }
}

internal sealed class ProcessorHarness(TraceLog trace, SenderConfiguration configuration, TagStore tags,
    TaggerProcessor processor) : IDisposable
{
    public SenderConfiguration Configuration { get; } = configuration;
    public TagStore Tags { get; } = tags;
    public TaggerProcessor Processor { get; } = processor;

    // Closes the optional invocation trace before fixture cleanup.
    public void Dispose()
    {
        trace.Dispose();
    }
}
