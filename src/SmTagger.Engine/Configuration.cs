using System.Collections.ObjectModel;
using System.Text;
using SmTagger.Shared;

namespace SmTagger.Engine;

/// <summary>The immutable sending identity selected by a current authenticated address.</summary>
public sealed record SenderProfile(
    string SenderId,
    string AuthAddress,
    IReadOnlyList<string> RetiredAuthAddresses,
    string PrivateAddress,
    IReadOnlyList<string> RetiredPrivateAddresses,
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
    private readonly IReadOnlyDictionary<string, SenderProfile> currentAuth;
    private readonly HashSet<string> retiredAuth;

    public IReadOnlyDictionary<string, SenderProfile> Profiles { get; }

    // Freeze the dictionaries after the configuration relationships have been validated.
    private SenderConfiguration(Dictionary<string, SenderProfile> profiles,
        Dictionary<string, SenderProfile> currentAuth, HashSet<string> retiredAuth)
    {
        Profiles = new ReadOnlyDictionary<string, SenderProfile>(profiles);
        this.currentAuth = new ReadOnlyDictionary<string, SenderProfile>(currentAuth);
        this.retiredAuth = retiredAuth;
    }

    // Load profiles and prove that all published authentication indexes agree with them.
    public static SenderConfiguration Load(string dataDir, TraceLog trace)
    {
        try
        {
            string profilesRoot = Path.Combine(dataDir, "profiles");
            string sendersRoot = Path.Combine(dataDir, "senders");
            string[] profileDirectories = Directory.GetDirectories(profilesRoot);
            string[] authDirectories = Directory.GetDirectories(sendersRoot);
            Array.Sort(profileDirectories, StringComparer.Ordinal);
            Array.Sort(authDirectories, StringComparer.Ordinal);
            var profiles = new Dictionary<string, SenderProfile>(StringComparer.Ordinal);
            var currentAuth = new Dictionary<string, SenderProfile>(StringComparer.Ordinal);
            var retiredAuth = new HashSet<string>(StringComparer.Ordinal);
            var allAddresses = new Dictionary<string, string>(StringComparer.Ordinal);
            var expectedIndexes = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string directory in profileDirectories)
            {
                string senderId = Path.GetFileName(directory);
                ValidateSenderId(senderId, directory);
                string auth = ReadAddress(Path.Combine(directory, "auth-address.txt"));
                string privateAddress = ReadAddress(Path.Combine(directory, "private-address.txt"));
                IReadOnlyList<string> oldAuth = ReadAddressList(Path.Combine(directory, "retired-auth-addresses.txt"));
                IReadOnlyList<string> oldPrivate = ReadAddressList(Path.Combine(directory, "retired-private-addresses.txt"));
                string template = ReadTemplate(Path.Combine(directory, "from-template.txt"));
                string policy = TrimTerminalNewlines(ReadText(Path.Combine(directory, "allow-mdn.txt")));
                if (policy is not ("true" or "false"))
                    throw new StartupConfigurationException($"Invalid allow-mdn.txt in {directory}; expected exactly true or false.");

                var profile = new SenderProfile(senderId, auth, oldAuth, privateAddress, oldPrivate, template, policy == "true");
                AddIdentity(allAddresses, auth, senderId, "current auth");
                AddIdentity(allAddresses, privateAddress, senderId, "current private");
                foreach (string address in oldAuth)
                    AddIdentity(allAddresses, address, senderId, "retired auth");
                foreach (string address in oldPrivate)
                    AddIdentity(allAddresses, address, senderId, "retired private");
                foreach (string address in oldAuth.Prepend(auth))
                {
                    if (!WindowsNames.IsUsableComponent(address))
                        throw new StartupConfigurationException($"Auth address {address} cannot name a literal Windows directory.");
                    expectedIndexes.Add(address, senderId);
                }

                profiles.Add(senderId, profile);
                currentAuth.Add(auth, profile);
                retiredAuth.UnionWith(oldAuth);
                trace.Event("-", "PROFILE", "OK", ("senderId", senderId), ("auth", auth),
                    ("privateAddress", privateAddress), ("retiredAuth", oldAuth),
                    ("retiredPrivate", oldPrivate), ("template", template), ("allowMdn", profile.AllowMdn));
            }

            var actualIndexes = new HashSet<string>(StringComparer.Ordinal);
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
                if (!expectedIndexes.TryGetValue(address, out string? expected) || expected != senderId)
                    throw new StartupConfigurationException($"Auth index {directory} does not identify its profile's current or retired auth address.");
                if (!profiles.ContainsKey(senderId) || !actualIndexes.Add(address))
                    throw new StartupConfigurationException($"Unresolved or duplicate auth index: {directory}");
                trace.Event("-", "AUTH_INDEX", "OK", ("auth", address), ("senderId", senderId));
            }
            foreach (string address in expectedIndexes.Keys)
                if (!actualIndexes.Contains(address))
                    throw new StartupConfigurationException($"Missing required auth index: {Path.Combine(sendersRoot, address)}");

            return new SenderConfiguration(profiles, currentAuth, retiredAuth);
        }
        catch (StartupConfigurationException) { throw; }
        catch (Exception exception)
        {
            throw new StartupConfigurationException($"Cannot load sender configuration from {dataDir}.", exception);
        }
    }

    // Select only a current enrolled auth; queued retired and unknown auth fail closed.
    public SenderProfile Resolve(string canonicalAuth)
    {
        if (currentAuth.TryGetValue(canonicalAuth, out SenderProfile? profile))
            return profile;
        string reason = retiredAuth.Contains(canonicalAuth) ? "retired" : "unconfigured";
        throw new MailContractException($"Authenticated address {canonicalAuth} is {reason} in this invocation's configuration.");
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
    private static string ReadTemplate(string path) => ParseTemplate(Encoding.UTF8.GetString(File.ReadAllBytes(path)));

    // Read strict UTF-8 without hiding a BOM or replacing corrupted identity bytes.
    internal static string ReadText(string path) => StrictUtf8.GetString(File.ReadAllBytes(path));

    // Accept only terminal CR/LF in single-value persistent identity files.
    internal static string TrimTerminalNewlines(string text) => text.TrimEnd('\r', '\n');

    // Read and validate one canonical stable sender UUID.
    internal static string ReadSenderId(string path)
    {
        string value = TrimTerminalNewlines(ReadText(path));
        ValidateSenderId(value, path);
        return value;
    }

    // Reject any noncanonical spelling of the permanent sender identity.
    internal static void ValidateSenderId(string value, string location)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed) || parsed.ToString("D") != value)
            throw new StartupConfigurationException($"Expected a canonical lowercase sender UUID at {location}.");
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

    // Parse one address file, preserving strict outer-whitespace handling.
    private static string ReadAddress(string path) => AddressSyntax.Canonicalize(TrimTerminalNewlines(ReadText(path)));

    // Parse each nonempty retired-address line and let global identity validation reject duplicates.
    private static IReadOnlyList<string> ReadAddressList(string path)
    {
        string[] values = ReadText(path).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return Array.AsReadOnly(values.Select(AddressSyntax.Canonicalize).ToArray());
    }

    // Enforce one canonical address across every role and every sender profile.
    private static void AddIdentity(Dictionary<string, string> addresses, string address, string senderId, string role)
    {
        string owner = $"{senderId} ({role})";
        if (!addresses.TryAdd(address, owner))
            throw new StartupConfigurationException($"Duplicate identity {address}: {addresses[address]} and {owner}.");
    }
}
