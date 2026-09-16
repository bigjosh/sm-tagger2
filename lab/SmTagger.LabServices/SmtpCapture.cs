using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SmTagger.LabServices;

// Retains one accepted envelope recipient separately from the message headers.
internal sealed record CapturedRecipient(string Address, string Parameters);
// Associates exact captured DATA with the SMTP transaction that delivered it.
internal sealed record CapturedEnvelope(string CaptureId, DateTimeOffset ReceivedAtUtc, string? Peer,
    string Helo, string MailFrom, string MailParameters, IReadOnlyList<CapturedRecipient> Recipients, string EmlFileName);

internal sealed class SmtpCapture
{
    internal const int MaximumMessageBytes = 32 * 1024 * 1024;
    private readonly string directory;

    // Creates only the explicitly configured local capture directory.
    public SmtpCapture(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
    }

    // Captures one plaintext SMTP session; every accepted recipient must remain under .test.
    public async Task HandleAsync(TcpClient client, CancellationToken cancellation)
    {
        NetworkStream stream = client.GetStream();
        var reader = new WireLineReader(stream);
        string helo = "";
        string? sender = null;
        string mailParameters = "";
        var recipients = new List<CapturedRecipient>();
        await WireLineReader.WriteAsync(stream, "220 capture.sm-lab.test ONLY LOCAL LAB SMTP\r\n", cancellation);
        while (await reader.ReadAsync(4096, cancellation) is byte[] line)
        {
            if (line.AsSpan(0, line.Length - 2).ContainsAnyExceptInRange((byte)32, (byte)126))
            {
                await WireLineReader.WriteAsync(stream, "500 ASCII SMTP commands required\r\n", cancellation);
                continue;
            }
            string commandLine = Encoding.ASCII.GetString(line, 0, line.Length - 2);
            int space = commandLine.IndexOf(' ');
            string command = (space < 0 ? commandLine : commandLine[..space]).ToUpperInvariant();
            string argument = space < 0 ? "" : commandLine[(space + 1)..];
            string reply;
            switch (command)
            {
                case "EHLO":
                case "HELO":
                    if (argument.Length == 0) { reply = "501 Greeting name required\r\n"; break; }
                    helo = argument;
                    sender = null;
                    recipients.Clear();
                    reply = command == "EHLO" ? $"250-capture.sm-lab.test ONLY LOCAL LAB\r\n250-8BITMIME\r\n250 SIZE {MaximumMessageBytes}\r\n" : "250 capture.sm-lab.test\r\n";
                    break;
                case "MAIL":
                    if (helo.Length == 0) { reply = "503 Send EHLO or HELO first\r\n"; break; }
                    if (!TryPath(argument, "FROM:", true, out string from, out string parameters))
                    { reply = "501 Expected MAIL FROM:<address>\r\n"; break; }
                    if (!ValidMailParameters(parameters)) { reply = "555 Unsupported MAIL parameters\r\n"; break; }
                    sender = from;
                    mailParameters = parameters;
                    recipients.Clear();
                    reply = "250 Sender accepted for local capture\r\n";
                    break;
                case "RCPT":
                    if (sender is null) { reply = "503 Send MAIL first\r\n"; break; }
                    if (!TryPath(argument, "TO:", false, out string recipient, out string recipientParameters))
                    { reply = "501 Expected RCPT TO:<address>\r\n"; break; }
                    if (!recipient.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
                    { reply = "550 External recipient refused; ONLY .test capture is permitted\r\n"; break; }
                    if (recipientParameters.Length != 0) { reply = "555 RCPT parameters are not supported\r\n"; break; }
                    if (recipients.Count >= 1000) { reply = "452 Lab recipient bound reached\r\n"; break; }
                    recipients.Add(new(recipient, recipientParameters));
                    reply = "250 Recipient accepted for local capture\r\n";
                    break;
                case "DATA":
                    if (argument.Length != 0) { reply = "501 DATA takes no argument\r\n"; break; }
                    if (sender is null || recipients.Count == 0) { reply = "503 MAIL and an accepted RCPT are required\r\n"; break; }
                    await WireLineReader.WriteAsync(stream, "354 End with <CRLF>.<CRLF>\r\n", cancellation);
                    byte[] message = await ReadDataAsync(reader, cancellation);
                    string id = Save(message, client.Client.RemoteEndPoint?.ToString(), helo, sender, mailParameters, recipients);
                    sender = null;
                    recipients.Clear();
                    reply = $"250 Captured locally as {id}; no relay\r\n";
                    break;
                case "RSET":
                    sender = null;
                    recipients.Clear();
                    reply = "250 Transaction reset\r\n";
                    break;
                case "NOOP":
                    reply = "250 OK\r\n";
                    break;
                case "QUIT":
                    await WireLineReader.WriteAsync(stream, "221 Local capture closing\r\n", cancellation);
                    return;
                default:
                    reply = "502 Command unsupported by local lab capture\r\n";
                    break;
            }
            await WireLineReader.WriteAsync(stream, reply, cancellation);
        }
    }

    // Removes exactly one SMTP transparency dot while retaining every remaining DATA byte and CRLF.
    private static async Task<byte[]> ReadDataAsync(WireLineReader reader, CancellationToken cancellation)
    {
        using var message = new MemoryStream();
        while (true)
        {
            byte[] line = await reader.ReadAsync(65536, cancellation) ?? throw new InvalidDataException("EOF during SMTP DATA.");
            if (line.Length == 3 && line[0] == '.') return message.ToArray();
            int offset = line[0] == '.' ? 1 : 0;
            if (message.Length + line.Length - offset > MaximumMessageBytes) throw new InvalidDataException("SMTP DATA exceeds the 32 MiB lab bound.");
            message.Write(line, offset, line.Length - offset);
        }
    }

    // Writes an exact EML and separate envelope JSON before acknowledging successful capture.
    private string Save(byte[] message, string? peer, string helo, string sender, string parameters, IReadOnlyList<CapturedRecipient> recipients)
    {
        string id = Guid.NewGuid().ToString("D");
        string emlName = id + ".eml";
        using (var output = new FileStream(Path.Combine(directory, emlName), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            output.Write(message);
        var envelope = new CapturedEnvelope(id, DateTimeOffset.UtcNow, peer, helo, sender, parameters, recipients.ToArray(), emlName);
        using (var output = new FileStream(Path.Combine(directory, id + ".json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(output, envelope, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"CAPTURE {id} recipients={recipients.Count} bytes={message.Length}");
        return id;
    }

    // Admits only a simple ASCII angle path and keeps its trailing ESMTP parameters separately.
    private static bool TryPath(string argument, string prefix, bool allowNull, out string address, out string parameters)
    {
        address = parameters = "";
        if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string path = argument[prefix.Length..].TrimStart(' ');
        if (!path.StartsWith('<')) return false;
        int close = path.IndexOf('>');
        if (close < 0 || close + 1 < path.Length && path[close + 1] != ' ') return false;
        address = path[1..close];
        parameters = path[(close + 1)..].Trim(' ');
        if (address.Length == 0) return allowNull;
        int at = address.IndexOf('@');
        if (at <= 0 || at == address.Length - 1 || address.LastIndexOf('@') != at) return false;
        string local = address[..at];
        string domain = address[(at + 1)..];
        const string atom = "!#$%&'*+-/=?^_`{|}~";
        if (local.Split('.').Any(part => part.Length == 0 || part.Any(character => !char.IsAsciiLetterOrDigit(character) && !atom.Contains(character)))) return false;
        return domain.Split('.').All(label => label.Length is >= 1 and <= 63 && label[0] != '-' && label[^1] != '-'
            && label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    // Accepts only the two ESMTP capabilities advertised by this capture utility.
    private static bool ValidMailParameters(string parameters)
    {
        foreach (string parameter in parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (parameter.Equals("BODY=8BITMIME", StringComparison.OrdinalIgnoreCase) || parameter.Equals("BODY=7BIT", StringComparison.OrdinalIgnoreCase)) continue;
            if (parameter.StartsWith("SIZE=", StringComparison.OrdinalIgnoreCase)
                && ulong.TryParse(parameter.AsSpan(5), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong size)
                && size <= MaximumMessageBytes) continue;
            return false;
        }
        return true;
    }
}
