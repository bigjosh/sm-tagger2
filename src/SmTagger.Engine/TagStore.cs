using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SmTagger.Mail;
using SmTagger.Shared;

namespace SmTagger.Engine;

/// <summary>A permanent published identity record; its diagnostic log is never authoritative.</summary>
public sealed record TagMapping(string TagAddress, string SenderId, string RecipientId, string DirectoryPath);

/// <summary>A live mapping could not enter the running authority; restart must reload it.</summary>
public sealed class MappingAvailabilityException : Exception
{
    // Preserve the already-live directory so the process boundary can retain evidence and stop.
    public MappingAvailabilityException(string directoryPath, Exception innerException)
        : base($"Published mapping {directoryPath} could not be made available in memory; the tagger must stop.", innerException) { }
}

/// <summary>The singleton tagger's permanent mapping authority and publication-attempt logs.</summary>
public sealed class TagStore
{
    // VERSION-SENSITIVE-014: The accepted 25-bit attribution tradeoff depends on catch-all feedback hiding allocation.
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string dataDir;
    private readonly TraceLog trace;
    private readonly Func<DateTimeOffset> clock;
    private readonly Action<byte[]> randomBytes;
    private readonly Func<Guid> uuid;
    private readonly Dictionary<(string SenderId, string RecipientId), TagMapping> mappings;

    public IReadOnlyDictionary<(string SenderId, string RecipientId), TagMapping> Mappings { get; }
    internal Action<TagMapping>? BeforeCacheInsert { get; set; }

    // Retain the validated live authority and the small allocation dependencies.
    private TagStore(string dataDir, TraceLog trace, Func<DateTimeOffset> clock,
        Action<byte[]> randomBytes, Func<Guid> uuid,
        Dictionary<(string SenderId, string RecipientId), TagMapping> mappings)
    {
        this.dataDir = dataDir;
        this.trace = trace;
        this.clock = clock;
        this.randomBytes = randomBytes;
        this.uuid = uuid;
        this.mappings = mappings;
        Mappings = new ReadOnlyDictionary<(string SenderId, string RecipientId), TagMapping>(mappings);
    }

    // Validate every live identity, ignoring diagnostic logs and never adopting inert staging.
    public static TagStore Load(string dataDir, SenderConfiguration config, TraceLog trace,
        Func<DateTimeOffset>? clock = null, Action<byte[]>? randomBytes = null, Func<Guid>? uuid = null)
    {
        try
        {
            var mappings = new Dictionary<(string SenderId, string RecipientId), TagMapping>();
            string root = Path.Combine(dataDir, "tag-addresses");
            if (DirectoryPresent(root, rejectNonDirectory: true))
            {
                foreach (string directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
                {
                    string address = Path.GetFileName(directory);
                    if (AddressSyntax.Canonicalize(address) != address || !WindowsNames.IsUsableComponent(address))
                        throw new StartupConfigurationException($"Live tag directory must name a canonical usable address: {directory}");
                    string senderId = SenderConfiguration.ReadSenderId(Path.Combine(directory, "sender-id.txt"));
                    if (!config.Profiles.ContainsKey(senderId))
                        throw new StartupConfigurationException($"Live mapping {directory} refers to missing sender {senderId}.");
                    string recipientId = SenderConfiguration.TrimTerminalNewlines(
                        SenderConfiguration.ReadText(Path.Combine(directory, "recipient-id.txt")));
                    _ = RecipientIdentity.Decode(recipientId);
                    var mapping = new TagMapping(address, senderId, recipientId, directory);
                    if (!mappings.TryAdd((senderId, recipientId), mapping))
                        throw new StartupConfigurationException($"Duplicate live sender/recipient mapping in {directory}.");
                    trace.Event("-", "MAPPING_LOAD", "OK", ("tagAddress", address),
                        ("senderId", senderId), ("recipientId", recipientId), ("path", directory));
                }
            }
            string stagingRoot = Path.Combine(dataDir, "staging");
            if (DirectoryPresent(stagingRoot, rejectNonDirectory: true))
                SenderConfiguration.ReportInertEntries(stagingRoot, trace);
            return new TagStore(dataDir, trace, clock ?? (() => DateTimeOffset.UtcNow),
                randomBytes ?? FillSecureBytes, uuid ?? Guid.NewGuid, mappings);
        }
        catch (StartupConfigurationException) { throw; }
        catch (Exception exception)
        {
            throw new StartupConfigurationException($"Cannot load permanent tag mappings from {dataDir}.", exception);
        }
    }

    // Reuse a live mapping or atomically publish one complete identity with at most sixteen token proposals.
    public TagMapping GetOrCreate(SenderProfile profile, string recipientId, string context)
    {
        // VERSION-SENSITIVE-009: The approved sending route accepts new tags without per-address provisioning.
        // Construct the authoritative key before any live filesystem publication.
        var key = (profile.SenderId, recipientId);
        if (mappings.TryGetValue(key, out TagMapping? existing))
        {
            trace.Event(context, "MAPPING_LOOKUP", "FOUND", ("senderId", profile.SenderId),
                ("recipientId", recipientId), ("tagAddress", existing.TagAddress));
            return existing;
        }
        trace.Event(context, "MAPPING_LOOKUP", "MISSING", ("senderId", profile.SenderId), ("recipientId", recipientId));
        string stagingRoot = Path.Combine(dataDir, "staging");
        string finalRoot = Path.Combine(dataDir, "tag-addresses");
        trace.Transition(context, "create", null, stagingRoot, () => Directory.CreateDirectory(stagingRoot));
        trace.Transition(context, "create", null, finalRoot, () => Directory.CreateDirectory(finalRoot));
        string staging = Path.Combine(stagingRoot, uuid().ToString("D") + ".tagtmp");
        RequireAbsentStaging(staging);
        trace.Transition(context, "create", null, staging, () => Directory.CreateDirectory(staging));
        WriteIdentity(Path.Combine(staging, "sender-id.txt"), profile.SenderId, context);
        WriteIdentity(Path.Combine(staging, "recipient-id.txt"), recipientId, context);
        trace.WriteTagLog(Path.Combine(staging, "tag-log.txt"), [], createEmpty: true, context);

        var bytes = new byte[5];
        for (int proposal = 1; proposal <= 16; proposal++)
        {
            randomBytes(bytes);
            string token = string.Create(5, bytes, static (characters, values) =>
            {
                for (int index = 0; index < characters.Length; index++)
                    characters[index] = Alphabet[values[index] & 31];
            });
            string tagAddress = profile.Template.Replace("%", token, StringComparison.Ordinal);
            string final = Path.Combine(finalRoot, tagAddress);
            var mapping = new TagMapping(tagAddress, profile.SenderId, recipientId, final);
            trace.Event(context, "MAPPING_PROPOSAL", "ATTEMPT", ("proposal", proposal), ("tagAddress", tagAddress));
            try
            {
                // VERSION-SENSITIVE-004: the deployment guarantees an atomic same-volume NTFS directory move.
                trace.Transition(context, "move", staging, final, () => Directory.Move(staging, final), "complete identity becomes live");
            }
            catch (IOException)
            {
                // The failed move is the only probe before inspecting its proposed collision destination.
                if (!DirectoryPresent(final))
                    throw;
                trace.Event(context, "MAPPING_COLLISION", "RETRY", ("proposal", proposal), ("tagAddress", tagAddress));
                if (proposal == 16)
                    throw new IOException($"All sixteen tag proposals collided; inert identity remains at {staging}.");
                continue;
            }

            try
            {
                BeforeCacheInsert?.Invoke(mapping);
                mappings.Add(key, mapping);
            }
            catch (Exception exception)
            {
                throw new MappingAvailabilityException(final, exception);
            }
            trace.Event(context, "MAPPING_AVAILABLE", "OK", ("senderId", profile.SenderId),
                ("recipientId", recipientId), ("tagAddress", tagAddress), ("path", final));
            return mapping;
        }
        throw new InvalidOperationException("The bounded proposal loop ended without an outcome.");
    }

    // Attempt one identical UTC entry per distinct referenced tag immediately before the caller publishes its child.
    public void AppendPublicationLogs(IEnumerable<TagMapping> referencedMappings, string outputBasename, string context)
    {
        TagMapping[] ordered;
        try
        {
            ordered = referencedMappings.DistinctBy(mapping => mapping.TagAddress, StringComparer.Ordinal)
                .OrderBy(mapping => mapping.TagAddress, StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception)
        {
            trace.ReportError("Cannot enumerate publication log targets; publication will continue.", exception);
            return;
        }
        byte[] entry;
        try
        {
            if (string.IsNullOrEmpty(outputBasename) || outputBasename.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new FormatException("A tag-log output basename must be nonempty and contain no CR, LF, or NUL.");
            string timestamp = clock().ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            entry = StrictUtf8.GetBytes(timestamp + " " + outputBasename + "\r\n");
        }
        catch (Exception exception)
        {
            foreach (TagMapping mapping in ordered)
                trace.ReportLoggingFailure(Path.Combine(mapping.DirectoryPath, "tag-log.txt"), "encode publication entry", exception);
            return;
        }
        foreach (TagMapping mapping in ordered)
            trace.WriteTagLog(Path.Combine(mapping.DirectoryPath, "tag-log.txt"), entry, createEmpty: false, context);
    }

    // Finish and close each required identity file before publishing its containing directory.
    private void WriteIdentity(string path, string content, string context)
    {
        byte[] bytes = StrictUtf8.GetBytes(content);
        trace.Transition(context, "create", null, path, () =>
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            file.Write(bytes);
            file.Flush();
        }, "complete closed identity file");
    }

    // Distinguish a missing optional root from inaccessible paths or unexpected non-directory objects.
    private static bool DirectoryPresent(string path, bool rejectNonDirectory = false)
    {
        try
        {
            bool isDirectory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            if (!isDirectory && rejectNonDirectory)
                throw new StartupConfigurationException($"Expected a directory at {path}.");
            return isDirectory;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    // A fresh UUID receives one absence check; collisions are evidence, never retried or reused.
    private static void RequireAbsentStaging(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new IOException($"Proposed UUID staging path already exists: {path}");
    }

    // Obtain all five independent bytes from the managed cryptographic random source.
    private static void FillSecureBytes(byte[] bytes) => RandomNumberGenerator.Fill(bytes);
}
