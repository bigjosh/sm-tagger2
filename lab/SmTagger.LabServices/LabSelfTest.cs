using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SmTagger.LabServices;

internal static class LabSelfTest
{
    // Uses only real loopback sockets and a fresh retained temporary capture directory.
    public static async Task<int> RunAsync()
    {
        int failures = 0;
        int passed = 0;
        string capture = Path.Combine(Path.GetTempPath(), "sm-tagger-lab-selftest-" + Guid.NewGuid().ToString("D"));
        var config = new LabConfiguration
        {
            BindAddress = "127.0.0.2",
            DnsPort = 0,
            SmtpPort = 0,
            CaptureDirectory = capture,
            Zones = ["sm-lab.test"],
            Records =
            [
                new("capture.sm-lab.test", "A", "127.0.0.2"),
                new("sm-lab.test", "MX", "capture.sm-lab.test", 10),
                new("sm-lab.test", "TXT", "v=spf1 ip4:127.0.0.2 -all"),
                new("_dmarc.sm-lab.test", "TXT", "v=DMARC1; p=none"),
                new("selector._domainkey.sm-lab.test", "TXT", "v=DKIM1; k=rsa; p=" + new string('A', 600))
            ]
        };
        var errors = new LabErrors();
        try
        {
            await using (var host = new LabHost(config, errors))
            {
                foreach ((string name, Func<Task> run) in new (string, Func<Task>)[]
                {
                    ("UDP DNS A/MX/TXT, authoritative negative answers, refusal, malformed input", () => TestUdpDnsAsync(host)),
                    ("TCP DNS framing and complete multi-string DKIM TXT after UDP truncation", () => TestTcpDnsAsync(host)),
                    ("SMTP exact dot transparency, envelopes, external refusal, and transaction commands", () => TestSmtpAsync(host, capture)),
                    ("Configuration rejects non-loopback bind and non-.test DNS data", TestConfigurationAsync),
                    ("Listener failure is reported and permits complete socket shutdown", TestListenerFailureAsync)
                })
                {
                    try { await run(); passed++; Console.WriteLine("SELF-TEST PASS " + name); }
                    catch (Exception exception) { failures++; Console.Error.WriteLine("SELF-TEST FAIL " + name + ": " + exception); }
                }
            }
        }
        catch (Exception exception) { failures++; Console.Error.WriteLine("SELF-TEST HOST FAIL: " + exception); }
        failures += errors.Count;
        Console.WriteLine($"SELF-TEST completed passed={passed} errors={failures} captures={capture}");
        return failures == 0 ? 0 : 1;
    }

    // Stops a real listener unexpectedly and checks notification, error accounting, and release of all bound sockets.
    private static async Task TestListenerFailureAsync()
    {
        var config = new LabConfiguration
        {
            DnsPort = 0,
            SmtpPort = 0,
            CaptureDirectory = Path.Combine(Path.GetTempPath(), "sm-tagger-lab-selftest-" + Guid.NewGuid().ToString("D")),
            Zones = ["failure.test"],
            Records = [new("failure.test", "A", "127.0.0.2")]
        };
        var errors = new LabErrors();
        int dnsPort;
        int smtpPort;
        await using (var host = new LabHost(config, errors))
        {
            dnsPort = host.DnsPort;
            smtpPort = host.SmtpPort;
            Require(!host.ListenerFailure.IsCompleted, "Healthy listeners must not report failure.");
            host.StopSmtpListenerForTesting();
            await host.ListenerFailure.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Require(errors.Count == 1, "Unexpected listener loss must report exactly one failure.");
        using var dnsTcp = new TcpListener(IPAddress.Parse("127.0.0.2"), dnsPort);
        using var smtpTcp = new TcpListener(IPAddress.Parse("127.0.0.2"), smtpPort);
        using var dnsUdp = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), dnsPort));
        dnsTcp.Start();
        smtpTcp.Start();
    }

    // Exercises UDP DNS on the actual bound port, including refusal without recursion capability.
    private static async Task TestUdpDnsAsync(LabHost host)
    {
        byte[] answer = await QueryUdpAsync(host, Query("capture.sm-lab.test", 1));
        Require((Flags(answer) & 0x858F) == 0x8500, "A response must be authoritative, preserve RD, omit RA, and succeed.");
        Require(AnswerData(answer).SequenceEqual(new byte[] { 127, 0, 0, 2 }), "Wrong A bytes.");
        answer = await QueryUdpAsync(host, Query("sm-lab.test", 15));
        Require(AnswerData(answer).SequenceEqual(new byte[] { 0, 10 }.Concat(NameBytes("capture.sm-lab.test"))), "Wrong MX preference/target.");
        answer = await QueryUdpAsync(host, Query("sm-lab.test", 16));
        Require(ReadTxt(AnswerData(answer)) == "v=spf1 ip4:127.0.0.2 -all", "Wrong SPF TXT bytes.");
        answer = await QueryUdpAsync(host, Query("_dmarc.sm-lab.test", 16));
        Require(ReadTxt(AnswerData(answer)) == "v=DMARC1; p=none", "Wrong DMARC TXT bytes.");
        answer = await QueryUdpAsync(host, Query("outside.example", 1));
        Require((Flags(answer) & 15) == 5 && (Flags(answer) & 0x0080) == 0, "Unknown domains must be REFUSED without recursion.");
        answer = await QueryUdpAsync(host, Query("missing.sm-lab.test", 1));
        Require((Flags(answer) & 15) == 3, "Missing local names must return NXDOMAIN.");
        answer = await QueryUdpAsync(host, Query("capture.sm-lab.test", 28));
        Require((Flags(answer) & 15) == 0 && BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(6)) == 0, "Known names without requested type must return NODATA.");
        byte[] malformed = Query("capture.sm-lab.test", 1);
        malformed[12] = 0xC0;
        malformed[13] = 12;
        answer = await QueryUdpAsync(host, malformed);
        Require((Flags(answer) & 15) == 1, "Cyclic compressed question must return FORMERR.");
    }

    // Confirms conservative UDP truncation and complete TCP retrieval of long TXT data.
    private static async Task TestTcpDnsAsync(LabHost host)
    {
        byte[] query = Query("selector._domainkey.sm-lab.test", 16);
        byte[] udp = await QueryUdpAsync(host, query);
        Require((Flags(udp) & 0x0200) != 0 && udp.Length <= 512, "Large UDP answers must set TC and remain at most 512 bytes.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(host.BindAddress, host.DnsPort, timeout.Token);
        NetworkStream stream = client.GetStream();
        for (int iteration = 0; iteration < 2; iteration++)
        {
            byte[] length = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)query.Length);
            await stream.WriteAsync(length, timeout.Token);
            await stream.WriteAsync(query, timeout.Token);
            await stream.ReadExactlyAsync(length, timeout.Token);
            byte[] response = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
            await stream.ReadExactlyAsync(response, timeout.Token);
            Require((Flags(response) & 0x020F) == 0, "TCP TXT response must be complete and successful.");
            Require(ReadTxt(AnswerData(response)) == "v=DKIM1; k=rsa; p=" + new string('A', 600), "TCP TXT chunks lost bytes.");
        }
    }

    // Submits binary-safe DATA with transparency dots and verifies the exact files and envelope recipients.
    private static async Task TestSmtpAsync(LabHost host, string capture)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(host.BindAddress, host.SmtpPort, timeout.Token);
        NetworkStream stream = client.GetStream();
        var reader = new WireLineReader(stream);
        await ExpectReplyAsync(reader, 220, timeout.Token);
        await CommandAsync(stream, reader, "EHLO sender.sm-lab.test", 250, timeout.Token);
        await CommandAsync(stream, reader, "MAIL FROM:<sender@sm-lab.test> BODY=8BITMIME", 250, timeout.Token);
        await CommandAsync(stream, reader, "RCPT TO:<first@sm-lab.test>", 250, timeout.Token);
        await CommandAsync(stream, reader, "RCPT TO:<someone@external.example>", 550, timeout.Token);
        await CommandAsync(stream, reader, "RCPT TO:<second@other.test>", 250, timeout.Token);
        await CommandAsync(stream, reader, "DATA", 354, timeout.Token);
        byte[] expected = [.. Encoding.ASCII.GetBytes("From: sender@sm-lab.test\r\nSubject: dot test\r\n\r\n.leading\r\n..\r\n"), 0, 255, 13, 10];
        byte[] transmitted = [.. Encoding.ASCII.GetBytes("From: sender@sm-lab.test\r\nSubject: dot test\r\n\r\n..leading\r\n...\r\n"), 0, 255, 13, 10, .. ".\r\n"u8.ToArray()];
        await stream.WriteAsync(transmitted, timeout.Token);
        await ExpectReplyAsync(reader, 250, timeout.Token);
        await CommandAsync(stream, reader, "MAIL FROM:<>", 250, timeout.Token);
        await CommandAsync(stream, reader, "RCPT TO:<reset@sm-lab.test>", 250, timeout.Token);
        await CommandAsync(stream, reader, "RSET", 250, timeout.Token);
        await CommandAsync(stream, reader, "DATA", 503, timeout.Token);
        await CommandAsync(stream, reader, "NOOP", 250, timeout.Token);
        await CommandAsync(stream, reader, "HELO second.sm-lab.test", 250, timeout.Token);
        await CommandAsync(stream, reader, "QUIT", 221, timeout.Token);
        string[] emls = Directory.GetFiles(capture, "*.eml");
        Require(emls.Length == 1 && File.ReadAllBytes(emls[0]).SequenceEqual(expected), "Captured DATA does not match exact unstuffed bytes.");
        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(Path.ChangeExtension(emls[0], ".json")));
        Require(json.RootElement.GetProperty("MailFrom").GetString() == "sender@sm-lab.test", "MAIL FROM was not retained.");
        string?[] recipients = json.RootElement.GetProperty("Recipients").EnumerateArray().Select(value => value.GetProperty("Address").GetString()).ToArray();
        Require(recipients.SequenceEqual(new[] { "first@sm-lab.test", "second@other.test" }), "Envelope recipient capture lost accepted recipients or included a refused external address.");
    }

    // Ensures public listener addresses and nonreserved DNS zones cannot be enabled by configuration.
    private static Task TestConfigurationAsync()
    {
        foreach (LabConfiguration invalid in new[]
        {
            new LabConfiguration { BindAddress = "0.0.0.0", Zones = ["sm-lab.test"], Records = [new("sm-lab.test", "A", "127.0.0.2")] },
            new LabConfiguration { Zones = ["sm-lab.test"], Records = [new("sm-lab.test", "A", "192.0.2.1")] },
            new LabConfiguration { Zones = ["example.com"], Records = [new("example.com", "A", "127.0.0.2")] },
            new LabConfiguration { Zones = ["sm-lab.test"], Records = [new("sm-lab.test", "MX", "external.example")] }
        })
        {
            bool rejected = false;
            try { invalid.Validate(); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Unsafe lab configuration was accepted.");
        }
        return Task.CompletedTask;
    }

    // Exchanges a single DNS UDP datagram solely with the helper's bound loopback endpoint.
    private static async Task<byte[]> QueryUdpAsync(LabHost host, byte[] query)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        await udp.SendAsync(query, new IPEndPoint(host.BindAddress, host.DnsPort), timeout.Token);
        UdpReceiveResult response = await udp.ReceiveAsync(timeout.Token);
        Require(response.RemoteEndPoint.Address.Equals(host.BindAddress) && response.RemoteEndPoint.Port == host.DnsPort, "Unexpected DNS response endpoint.");
        Require(response.Buffer.Length >= 12 && response.Buffer[0] == query[0] && response.Buffer[1] == query[1], "DNS response lost its transaction ID.");
        return response.Buffer;
    }

    // Constructs an independent one-question DNS request with a fixed ID and recursion-desired bit.
    private static byte[] Query(string name, ushort type)
    {
        byte[] question = NameBytes(name);
        byte[] packet = new byte[12 + question.Length + 4];
        packet[0] = 0x3A;
        packet[1] = 0x71;
        packet[2] = 1;
        packet[5] = 1;
        question.CopyTo(packet, 12);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12 + question.Length), type);
        packet[^1] = 1;
        return packet;
    }

    // Independently encodes the ordinary ASCII test names without DNS compression.
    private static byte[] NameBytes(string name)
    {
        var bytes = new List<byte>();
        foreach (string label in name.Split('.')) { bytes.Add((byte)label.Length); bytes.AddRange(Encoding.ASCII.GetBytes(label)); }
        bytes.Add(0);
        return bytes.ToArray();
    }

    // Extracts the first answer's payload while checking basic response framing independently.
    private static byte[] AnswerData(byte[] response)
    {
        Require(response.Length >= 12 && BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6)) == 1, "Expected exactly one DNS answer.");
        int position = SkipName(response, 12) + 4;
        position = SkipName(response, position);
        Require(position + 10 <= response.Length, "Truncated DNS resource record.");
        int length = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(position + 8));
        position += 10;
        Require(position + length <= response.Length, "Truncated DNS answer data.");
        return response.AsSpan(position, length).ToArray();
    }

    // Skips a wire owner/question name, accepting the response's compression pointer as two bytes.
    private static int SkipName(byte[] bytes, int position)
    {
        while (position < bytes.Length)
        {
            int length = bytes[position++];
            if (length == 0) return position;
            if ((length & 0xC0) == 0xC0) { Require(position < bytes.Length, "Truncated DNS pointer."); return position + 1; }
            Require(length <= 63 && position + length <= bytes.Length, "Invalid DNS label.");
            position += length;
        }
        throw new InvalidDataException("Unterminated DNS name.");
    }

    // Reassembles length-prefixed TXT strings so chunk boundaries do not alter DKIM material.
    private static string ReadTxt(byte[] bytes)
    {
        using var result = new MemoryStream();
        int position = 0;
        while (position < bytes.Length)
        {
            int count = bytes[position++];
            Require(position + count <= bytes.Length, "Truncated DNS TXT string.");
            result.Write(bytes, position, count);
            position += count;
        }
        return Encoding.UTF8.GetString(result.ToArray());
    }

    // Reads the DNS response flag word after an explicit minimum-length check.
    private static ushort Flags(byte[] response)
    {
        Require(response.Length >= 12, "Truncated DNS response header.");
        return BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2));
    }

    // Sends one SMTP command and validates its complete possibly multiline reply.
    private static async Task CommandAsync(NetworkStream stream, WireLineReader reader, string command, int expected, CancellationToken cancellation)
    {
        await WireLineReader.WriteAsync(stream, command + "\r\n", cancellation);
        await ExpectReplyAsync(reader, expected, cancellation);
    }

    // Consumes all continuation lines rather than confusing EHLO capability output with the next reply.
    private static async Task ExpectReplyAsync(WireLineReader reader, int expected, CancellationToken cancellation)
    {
        for (int lineCount = 0; lineCount < 20; lineCount++)
        {
            byte[] bytes = await reader.ReadAsync(4096, cancellation) ?? throw new InvalidDataException("SMTP closed before expected reply.");
            string line = Encoding.ASCII.GetString(bytes);
            Require(line.Length >= 6 && int.TryParse(line.AsSpan(0, 3), out int actual) && actual == expected, $"Expected SMTP {expected}, received {line.TrimEnd()}.");
            if (line[3] == ' ') return;
            Require(line[3] == '-', "Invalid SMTP reply continuation.");
        }
        throw new InvalidDataException("SMTP reply exceeded the self-test line bound.");
    }

    // Converts each self-test expectation into a counted failure with a useful local diagnostic.
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
