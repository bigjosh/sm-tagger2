using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmTagger.LabServices;

// ONLY a local integration-lab configuration; this project is not part of either mail processor.
internal sealed class LabConfiguration
{
    public string BindAddress { get; set; } = "127.0.0.2";
    public int DnsPort { get; set; } = 53;
    public int SmtpPort { get; set; } = 25;
    public string CaptureDirectory { get; set; } = "captures";
    public List<string> Zones { get; set; } = [];
    public List<DnsRecord> Records { get; set; } = [];

    // Loads one bounded JSON file and resolves captures relative to its own directory.
    public static LabConfiguration Load(string path)
    {
        string absolute = Path.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(absolute);
        if (bytes.Length > 1_048_576) throw new InvalidDataException("Lab configuration exceeds 1 MiB.");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        LabConfiguration config = JsonSerializer.Deserialize<LabConfiguration>(bytes, options) ?? throw new InvalidDataException("Empty lab configuration.");
        config.CaptureDirectory = Path.GetFullPath(config.CaptureDirectory, Path.GetDirectoryName(absolute)!);
        return config;
    }

    // Establishes the loopback-only, explicitly configured .test namespace before opening listeners.
    public IPAddress Validate()
    {
        if (!IPAddress.TryParse(BindAddress, out IPAddress? bind) || bind.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(bind))
            throw new InvalidDataException("Lab listeners must bind an IPv4 loopback address, such as 127.0.0.2.");
        if (DnsPort is < 0 or > 65535 || SmtpPort is < 0 or > 65535 || DnsPort == SmtpPort && DnsPort != 0)
            throw new InvalidDataException("DNS/SMTP ports must be distinct values from 0 through 65535; zero requests an ephemeral test port.");
        if (Zones is null || Records is null || Zones.Count is < 1 or > 16 || Records.Count is < 1 or > 128)
            throw new InvalidDataException("Configure 1–16 .test zones and 1–128 explicit DNS records.");
        Zones = Zones.Select(NormalizeTestName).Distinct(StringComparer.Ordinal).ToList();
        Records = Records.Select(record =>
        {
            string name = NormalizeTestName(record.Name);
            if (!Zones.Any(zone => name == zone || name.EndsWith('.' + zone, StringComparison.Ordinal)))
                throw new InvalidDataException($"DNS owner {name} is outside the configured .test zones.");
            string type = record.Type.ToUpperInvariant();
            string value = record.Value;
            switch (type)
            {
                case "A":
                    if (!IPAddress.TryParse(value, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(address))
                        throw new InvalidDataException($"A record {name} requires an IPv4 loopback value.");
                    value = address.ToString();
                    break;
                case "MX":
                    value = NormalizeTestName(value);
                    break;
                case "TXT":
                    if (value is null || Encoding.UTF8.GetByteCount(value) > 4096)
                        throw new InvalidDataException($"TXT record {name} exceeds 4096 UTF-8 bytes.");
                    break;
                default:
                    throw new InvalidDataException($"Only explicitly configured A, MX, and TXT records are supported: {type}");
            }
            return new DnsRecord(name, type, value, record.Preference);
        }).ToList();
        CaptureDirectory = Path.GetFullPath(CaptureDirectory);
        return bind;
    }

    // Normalizes bounded ASCII DNS names while reserving all configured data to the .test suffix.
    internal static string NormalizeTestName(string value)
    {
        string name = value.TrimEnd('.').ToLowerInvariant();
        if (name.Length > 253 || !name.EndsWith(".test", StringComparison.Ordinal))
            throw new InvalidDataException("Lab DNS names must be beneath the reserved .test suffix.");
        foreach (string label in name.Split('.'))
            if (label.Length is < 1 or > 63 || label.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
                throw new InvalidDataException($"Invalid bounded ASCII DNS name: {name}");
        return name;
    }
}

internal sealed class LabErrors
{
    private int count;
    public int Count => Volatile.Read(ref count);

    // Records real helper failures without stopping independent DNS/SMTP sessions.
    public void Report(string context, Exception exception)
    {
        Interlocked.Increment(ref count);
        Console.Error.WriteLine($"LAB ERROR {context}: {exception.GetType().Name}: {exception.Message}");
    }
}
