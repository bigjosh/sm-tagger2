using System.Text;
using SmTagger.Engine;
using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class ConfigurationTests
{
    // Canonical identities and first-token templates produce a fixed startup snapshot.
    [Fact]
    public void LoadsCanonicalProfilesAndIgnoresTemplateNotes()
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteProfile("auth-address.txt", "Auth@Example.COM\r\n");
        fixture.WriteProfile("private-address.txt", "Secret@Example.COM\n");
        fixture.WriteProfile("from-template.txt", " \tSender-%@TAGS.EXAMPLE.COM ignored % invalid\nnotes");
        SenderConfiguration configuration = fixture.LoadConfiguration();
        SenderProfile profile = configuration.Resolve("auth@example.com");
        Assert.Equal("secret@example.com", profile.PrivateAddress);
        Assert.Equal("sender-%@tags.example.com", profile.Template);
        fixture.WriteProfile("private-address.txt", "changed@example.com");
        Assert.Equal("secret@example.com", configuration.Resolve("auth@example.com").PrivateAddress);
    }

    // Ignored annotations cannot invalidate the preceding complete template token through their encoding.
    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void InvalidUtf8InIgnoredTemplateNotesDoesNotFailStartup(string separator)
    {
        using var fixture = new StoreTestFixture();
        byte[] prefix = Encoding.ASCII.GetBytes("Sender-%@TAGS.EXAMPLE.COM" + separator);
        File.WriteAllBytes(Path.Combine(fixture.ProfileDirectory, "from-template.txt"), [.. prefix, 0xff, 0xc3]);
        Assert.Equal("sender-%@tags.example.com", fixture.LoadConfiguration().Profiles[StoreTestFixture.SenderId].Template);
    }

    // Replacement decoding cannot turn corrupt token bytes into a valid ASCII address pattern.
    [Theory]
    [InlineData("sender-", "%@tags.example.com")]
    [InlineData("sender-%@", "tags.example.com")]
    [InlineData("sender-%@tags.example.com", "")]
    public void InvalidUtf8InsideTemplateTokenFailsStartup(string before, string after)
    {
        using var fixture = new StoreTestFixture();
        File.WriteAllBytes(Path.Combine(fixture.ProfileDirectory, "from-template.txt"),
            [.. Encoding.ASCII.GetBytes(before), 0xff, .. Encoding.ASCII.GetBytes(after)]);
        Assert.Throws<StartupConfigurationException>(fixture.LoadConfiguration);
    }

    // Retired routing remains present but cannot select a profile for new processing.
    [Fact]
    public void RetiredAuthIsLoadedButRejected()
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteProfile("retired-auth-addresses.txt", "Old@Example.com\r\n\r\n");
        fixture.WriteIndex("old@example.com");
        SenderConfiguration configuration = fixture.LoadConfiguration();
        Assert.Contains("retired", Assert.Throws<MailContractException>(() => configuration.Resolve("old@example.com")).Message);
        Assert.Contains("unconfigured", Assert.Throws<MailContractException>(() => configuration.Resolve("stranger@example.com")).Message);
    }

    // Every profile field is required, including the two possibly empty retired lists.
    [Theory]
    [InlineData("auth-address.txt")]
    [InlineData("private-address.txt")]
    [InlineData("retired-auth-addresses.txt")]
    [InlineData("retired-private-addresses.txt")]
    [InlineData("from-template.txt")]
    [InlineData("allow-mdn.txt")]
    public void MissingProfileFileFailsStartup(string filename)
    {
        using var fixture = new StoreTestFixture();
        File.Delete(Path.Combine(fixture.ProfileDirectory, filename));
        Assert.Throws<StartupConfigurationException>(fixture.LoadConfiguration);
    }

    // MDN policy accepts only the complete lowercase token after terminal newlines.
    [Theory]
    [InlineData("TRUE")]
    [InlineData(" false")]
    [InlineData("false ")]
    [InlineData("false\ntrue")]
    [InlineData("")]
    [InlineData("false # note")]
    public void MalformedMdnPolicyFailsStartup(string policy)
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteProfile("allow-mdn.txt", policy);
        Assert.Throws<StartupConfigurationException>(fixture.LoadConfiguration);
    }

    // Required policy permits both documented values without inventing an evidence-file requirement.
    [Theory]
    [InlineData("true\r\n", true)]
    [InlineData("false\n", false)]
    public void LoadsExactMdnPolicy(string policy, bool expected)
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteProfile("allow-mdn.txt", policy);
        Assert.Equal(expected, fixture.LoadConfiguration().Resolve("auth@example.com").AllowMdn);
    }

    // Canonical identity uniqueness covers current and retired roles, not just auth lookup keys.
    [Theory]
    [InlineData("private-address.txt", "AUTH@EXAMPLE.COM")]
    [InlineData("retired-private-addresses.txt", "SECRET@EXAMPLE.COM")]
    [InlineData("retired-auth-addresses.txt", "auth@example.com")]
    [InlineData("retired-private-addresses.txt", "old@example.com\nOLD@EXAMPLE.COM")]
    public void DuplicateIdentityRolesFailStartup(string filename, string content)
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteProfile(filename, content);
        Assert.Contains("Duplicate identity", Assert.Throws<StartupConfigurationException>(fixture.LoadConfiguration).Message);
    }

    // Identity ownership is global across profiles even when each profile is internally coherent.
    [Fact]
    public void DuplicateIdentityAcrossProfilesFailsStartup()
    {
        using var fixture = new StoreTestFixture();
        fixture.AddProfile(StoreTestFixture.SecondSenderId, "other@example.com", "SECRET@EXAMPLE.COM");
        Assert.Contains("Duplicate identity", Assert.Throws<StartupConfigurationException>(fixture.LoadConfiguration).Message);
    }

    // Missing current or retired indexes cannot silently change conservative enrollment routing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingExpectedAuthIndexFailsStartup(bool retired)
    {
        using var fixture = new StoreTestFixture();
        if (retired)
            fixture.WriteProfile("retired-auth-addresses.txt", "old@example.com");
        else
            Directory.Move(Path.Combine(fixture.Root, "senders", "auth@example.com"), Path.Combine(fixture.Root, "outside-index"));
        Assert.Contains("Missing required auth index", Assert.Throws<StartupConfigurationException>(fixture.LoadConfiguration).Message);
    }

    // Published indexes cannot introduce an undeclared address or change their stable sender reference.
    [Theory]
    [InlineData("unknown@example.com", StoreTestFixture.SenderId)]
    [InlineData("auth@example.com", StoreTestFixture.SecondSenderId)]
    public void UnexpectedAuthIndexFailsStartup(string address, string senderId)
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteIndex(address, senderId);
        Assert.Throws<StartupConfigurationException>(fixture.LoadConfiguration);
    }

    // Invalid UUID spelling remains a configuration error despite Guid's permissive parser.
    [Fact]
    public void NoncanonicalSenderIdFailsStartup()
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteIndex("auth@example.com", StoreTestFixture.SenderId.ToUpperInvariant());
        Assert.Throws<StartupConfigurationException>(fixture.LoadConfiguration);
    }

    // Literal auth components must be usable by the sorter's directory-only routing lookup.
    [Fact]
    public void ValidAddressWithWindowsUnsafeComponentFailsEnrollment()
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteProfile("auth-address.txt", "slash/name@example.com");
        Assert.Throws<StartupConfigurationException>(fixture.LoadConfiguration);
    }

    // Single-value identity reading never conceals leading bytes, BOMs, or internal line breaks.
    [Theory]
    [InlineData(" secret@example.com")]
    [InlineData("secret@example.com ")]
    [InlineData("\r\nsecret@example.com")]
    [InlineData("\uFEFFsecret@example.com")]
    [InlineData("secret@example.com\nother@example.com")]
    public void MalformedAddressFileFailsStartup(string value)
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteProfile("private-address.txt", value);
        Assert.Throws<StartupConfigurationException>(fixture.LoadConfiguration);
    }

    // Template validation proves syntax and path usability without reading ignored annotation text.
    [Theory]
    [InlineData("")]
    [InlineData("tag@example.com")]
    [InlineData("tag-%%@example.com")]
    [InlineData("<tag-%@example.com>")]
    [InlineData("tag/%@example.com")]
    [InlineData("tag-%@-example.com")]
    public void InvalidTemplateFailsStartup(string value)
    {
        using var fixture = new StoreTestFixture();
        fixture.WriteProfile("from-template.txt", value);
        Assert.Throws<StartupConfigurationException>(fixture.LoadConfiguration);
    }

    // The trusted template-length assumption must not acquire a new runtime address-length gate.
    [Fact]
    public void TemplateAddressLengthRemainsAnOperatorAssumption()
    {
        using var fixture = new StoreTestFixture();
        string template = new string('a', 500) + "-%@tags.example.com";
        fixture.WriteProfile("from-template.txt", template);
        Assert.Equal(template, fixture.LoadConfiguration().Profiles[StoreTestFixture.SenderId].Template);
    }

    // Unrelated files are ignored while unpublished auth evidence is named and left untouched.
    [Fact]
    public void ExtraFilesAndInertAuthStagingDoNotBecomeConfiguration()
    {
        using var fixture = new StoreTestFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "profiles", "notes.txt"), "unrelated");
        File.WriteAllText(Path.Combine(fixture.Root, "senders", "notes.txt"), "unrelated");
        string staging = Path.Combine(fixture.Root, "senders", ".staging", "broken.authtmp");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "sender-id.txt"), [255]);
        Assert.Single(fixture.LoadConfiguration().Profiles);
        Assert.Contains("broken.authtmp", fixture.Errors.ToString());
        Assert.Equal(new byte[] { 255 }, File.ReadAllBytes(Path.Combine(staging, "sender-id.txt")));
    }
}

internal sealed class StoreTestFixture : IDisposable
{
    internal const string SenderId = "a1234567-89ab-4cde-8f01-23456789abcd";
    internal const string SecondSenderId = "b1234567-89ab-4cde-8f01-23456789abcd";
    private static readonly string TestParent = Path.Combine(Path.GetTempPath(), "sm-tagger-store-tests");
    internal string Root { get; } = Path.Combine(TestParent, Guid.NewGuid().ToString("D"));
    internal string ProfileDirectory => Path.Combine(Root, "profiles", SenderId);
    internal StringWriter Errors { get; } = new();
    internal TraceLog Trace { get; }

    // Create an isolated synthetic administrative tree; no captured message samples are needed.
    internal StoreTestFixture()
    {
        Directory.CreateDirectory(Path.Combine(Root, "profiles"));
        Directory.CreateDirectory(Path.Combine(Root, "senders"));
        AddProfile(SenderId, "auth@example.com", "secret@example.com");
        Trace = TraceLog.Open(Root, false, Errors);
    }

    // Write one coherent profile and its permanent current-auth index.
    internal void AddProfile(string senderId, string auth, string privateAddress)
    {
        string directory = Path.Combine(Root, "profiles", senderId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "auth-address.txt"), auth, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "private-address.txt"), privateAddress, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "retired-auth-addresses.txt"), "");
        File.WriteAllText(Path.Combine(directory, "retired-private-addresses.txt"), "");
        File.WriteAllText(Path.Combine(directory, "from-template.txt"), "sender-%@tags.example.com");
        File.WriteAllText(Path.Combine(directory, "allow-mdn.txt"), "false");
        WriteIndex(auth.ToLowerInvariant(), senderId);
    }

    // Mutate a stopped configuration field for a targeted corruption test.
    internal void WriteProfile(string filename, string content) =>
        File.WriteAllText(Path.Combine(ProfileDirectory, filename), content, new UTF8Encoding(false));

    // Publish a synthetic auth index without involving the runtime's external administration scope.
    internal void WriteIndex(string address, string senderId = SenderId)
    {
        string directory = Path.Combine(Root, "senders", address);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "sender-id.txt"), senderId);
    }

    // Publish a test live identity with caller-selected bytes and no dependency on a tag log.
    internal string WriteMapping(string tagAddress, string recipientId, string senderId = SenderId)
    {
        string directory = Path.Combine(Root, "tag-addresses", tagAddress);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "sender-id.txt"), senderId, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "recipient-id.txt"), recipientId, new UTF8Encoding(false));
        return directory;
    }

    // Load the fixture through the production configuration boundary.
    internal SenderConfiguration LoadConfiguration() => SenderConfiguration.Load(Root, Trace);

    // Remove only the verified unique test directory after all acquired log handles are closed.
    public void Dispose()
    {
        Trace.Dispose();
        string absolute = Path.GetFullPath(Root);
        string parent = Path.GetFullPath(TestParent) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup path escaped its named temporary parent.");
        if (Directory.Exists(absolute))
            Directory.Delete(absolute, recursive: true);
        Errors.Dispose();
    }
}
