using System.Collections.ObjectModel;
using System.Text;
using SmTagger.Shared;

namespace SmTagger.Engine;

/// <summary>The immutable sending identity selected by one or more authentication indexes.</summary>
public sealed record SenderProfile(
    string SenderId,
    string PrivateAddress,
    string Template,
    bool AllowMdn);

/// <summary>A configuration or persistent identity cannot establish an unambiguous startup view.</summary>
public sealed class StartupConfigurationException : Exception
{
    // Preserve the location and underlying reason for a failed startup validation.
    public StartupConfigurationException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

/// <summary>The complete validated profile and authentication snapshot for one invocation.</summary>
public sealed class SenderConfiguration
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IReadOnlyDictionary<string, SenderProfile> authIndexes;

    public IReadOnlyDictionary<string, SenderProfile> Profiles { get; }

    // Freeze the dictionaries after the configuration relationships have been validated.
    private SenderConfiguration(Dictionary<string, SenderProfile> profiles,
        Dictionary<string, SenderProfile> authIndexes)
    {
        Profiles = new ReadOnlyDictionary<string, SenderProfile>(profiles);
        this.authIndexes = new ReadOnlyDictionary<string, SenderProfile>(authIndexes);
    }

    // Load stable sender records and resolve every published auth index to its existing sender.
    public static SenderConfiguration Load(string dataDir, TraceLog trace)
    {
        try
        {
            string senderIdsRoot = Path.Combine(dataDir, "senders", "sender-ids");
            string authAddressesRoot = Path.Combine(dataDir, "senders", "auth-addresses");
            string[] profileDirectories = Directory.GetDirectories(senderIdsRoot);
            string[] authDirectories = Directory.GetDirectories(authAddressesRoot);
            Array.Sort(profileDirectories, StringComparer.Ordinal);
            Array.Sort(authDirectories, StringComparer.Ordinal);
            var profiles = new Dictionary<string, SenderProfile>(StringComparer.Ordinal);
            var authIndexes = new Dictionary<string, SenderProfile>(StringComparer.Ordinal);
            var allAddresses = new Dictionary<string, (string SenderId, string Role)>(StringComparer.Ordinal);

            foreach (string directory in profileDirectories)
            {
                string senderId = Path.GetFileName(directory);
                ValidateSenderId(senderId, directory);
                string privateAddress = ReadAddress(Path.Combine(directory, "private-address.txt"));
                string template = ReadTemplate(Path.Combine(directory, "from-template.txt"));
                string policy = ReadSetting(Path.Combine(directory, "allow-mdn.txt"));
                bool allowMdn = policy.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (!allowMdn && !policy.Equals("false", StringComparison.OrdinalIgnoreCase))
                    throw new StartupConfigurationException(
                        $"Invalid allow-mdn.txt in {directory}; expected true or false, ignoring case and surrounding whitespace.");

                var profile = new SenderProfile(senderId, privateAddress, template, allowMdn);
                AddIdentity(allAddresses, privateAddress, senderId, "private");
                profiles.Add(senderId, profile);
                trace.Event("-", "PROFILE", "OK", ("senderId", senderId),
                    ("privateAddress", privateAddress), ("template", template), ("allowMdn", profile.AllowMdn));
            }

            foreach (string directory in authDirectories)
            {
                string address = Path.GetFileName(directory);
                if (address == ".staging")
                {
                    ReportInertEntries(directory, trace);
                    continue;
                }
                string canonical = AddressSyntax.Canonicalize(address);
                if (canonical != address || !WindowsNames.IsUsableComponent(address))
                    throw new StartupConfigurationException($"Auth index directory is not a canonical usable address: {directory}");
                string senderId = ReadSenderId(Path.Combine(directory, "sender-id.txt"));
                if (!profiles.TryGetValue(senderId, out SenderProfile? profile))
                    throw new StartupConfigurationException($"Auth index {directory} refers to missing sender-id {senderId}.");
                AddIdentity(allAddresses, address, senderId, "auth");
                authIndexes.Add(address, profile);
                trace.Event("-", "AUTH_INDEX", "OK", ("auth", address), ("senderId", senderId));
            }

            return new SenderConfiguration(profiles, authIndexes);
        }
        catch (StartupConfigurationException) { throw; }
        catch (Exception exception)
        {
            throw new StartupConfigurationException($"Cannot load sender configuration from {dataDir}.", exception);
        }
    }

    // Resolve indexed accounts through the startup snapshot; unknown queued auth fails closed.
    public SenderProfile Resolve(string canonicalAuth)
    {
        if (authIndexes.TryGetValue(canonicalAuth, out SenderProfile? profile))
            return profile;
        throw new MailContractException($"Authenticated address {canonicalAuth} is unconfigured in this invocation's configuration.");
    }

    // Parse only the first whitespace-delimited token and validate the invariant token substitution.
    internal static string ParseTemplate(string text)
    {
        int start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;
        int end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;
        string token = text[start..end];
        if (token.Count(character => character == '%') != 1)
            throw new StartupConfigurationException("A from-template must contain exactly one % in its first token.");
        string example = token.Replace("%", "00000", StringComparison.Ordinal);
        string canonical = AddressSyntax.Canonicalize(example);
        if (!WindowsNames.IsUsableComponent(canonical))
            throw new StartupConfigurationException("The from-template cannot produce a literal tag-address directory name.");
        // All permitted replacement bytes are ASCII letters/digits in the same grammar positions.
        // ASSUMPTION-TEMPLATE-LENGTH deliberately excludes a transport-address-length gate here.
        return token.ToLowerInvariant();
    }

    // Tolerate undecodable annotations while the parser still rejects non-ASCII template-token characters.
    private static string ReadTemplate(string path) =>
        ParseTemplate(Encoding.UTF8.GetString(File.ReadAllBytes(path)).TrimStart('\uFEFF'));

    // Read strict UTF-8 without hiding a BOM or replacing corrupted identity bytes.
    internal static string ReadText(string path) => StrictUtf8.GetString(File.ReadAllBytes(path));

    // Allow editor-added leading BOM characters and outer whitespace only in human-edited settings.
    private static string ReadSetting(string path) => ReadText(path).TrimStart('\uFEFF').Trim();

    // Accept only terminal CR/LF in single-value persistent identity files.
    internal static string TrimTerminalNewlines(string text) => text.TrimEnd('\r', '\n');

    // Read one stable opaque sender identifier without changing its spelling.
    internal static string ReadSenderId(string path)
    {
        string value = TrimTerminalNewlines(ReadText(path));
        ValidateSenderId(value, path);
        return value;
    }

    // Require only one usable directory component, without imposing a sender-specific format.
    internal static void ValidateSenderId(string value, string location)
    {
        if (!WindowsNames.IsUsableComponent(value))
            throw new StartupConfigurationException($"Expected a nonempty literal Windows directory name for sender-id at {location}.");
    }

    // Report inert evidence without reading its contents or attempting recovery.
    internal static void ReportInertEntries(string directory, TraceLog trace)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            trace.ReportError($"WARNING inert staging entry retained: {entry}");
            trace.Event("-", "INERT_ENTRY", "WARNING", ("path", entry));
        }
    }

    // Parse one configured address after removing harmless editor formatting.
    private static string ReadAddress(string path) => AddressSyntax.Canonicalize(ReadSetting(path));

    // Permit shared auth/private roles within one sender while rejecting ownership by another sender.
    private static void AddIdentity(Dictionary<string, (string SenderId, string Role)> addresses,
        string address, string senderId, string role)
    {
        if (addresses.TryGetValue(address, out var owner))
        {
            if (!string.Equals(owner.SenderId, senderId, StringComparison.Ordinal))
                throw new StartupConfigurationException(
                    $"Duplicate identity {address}: {owner.SenderId} ({owner.Role}) and {senderId} ({role}).");
            return;
        }
        addresses.Add(address, (senderId, role));
    }
}
