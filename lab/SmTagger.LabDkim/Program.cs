using System.Text;
using System.Text.Json;

namespace SmTagger.LabDkim;

internal static class Program
{
    // Verifies an explicitly supplied synthetic capture and DNS TXT record without querying networks or changing mail.
    public static int Main(string[] args)
    {
        if (args is ["--self-test"]) return DkimSelfTest.Run();
        if (args.Length == 0 || args is ["--help"])
        {
            Console.WriteLine("ONLY LOCAL LAB: sm-tagger-lab-dkim --message <synthetic.eml> --key-file <concatenated-dns-txt.txt> --dns-name <selector._domainkey.domain> --expected-domain <domain>");
            Console.WriteLine("No live DNS, SPF, DMARC, client compatibility, or deployment gate is asserted. Use --self-test for synthesized verification checks.");
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || args[i] is not ("--message" or "--key-file" or "--dns-name" or "--expected-domain") || !options.TryAdd(args[i], args[i + 1]))
                    throw new ArgumentException("Use --help; each supported option must appear once with one value.");
            }
            if (options.Count != 4) throw new ArgumentException("All four verification options are required.");
            byte[] message = ReadBounded(options["--message"], DkimVerifier.MaximumMessageBytes);
            string key = new UTF8Encoding(false, true).GetString(ReadBounded(options["--key-file"], DkimVerifier.MaximumKeyRecordBytes));
            VerificationReport report = DkimVerifier.Verify(message, key, options["--dns-name"], options["--expected-domain"], DateTimeOffset.UtcNow);
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return report.Verified ? 0 : 1;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("LAB INPUT ERROR: " + exception.Message);
            return 2;
        }
    }

    // Reads a size-capped snapshot from a file handle that denies concurrent writes on Windows.
    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximumBytes) throw new ArgumentException("The message or key record exceeds the documented lab size bound.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("The input length changed during its bounded read.");
        return bytes;
    }
}
