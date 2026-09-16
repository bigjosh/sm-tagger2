using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace SmTagger.LabServices;

/// <summary>One previously validated local test-zone record.</summary>
internal sealed record DnsRecord(string Name, string Type, string Value, ushort Preference = 10);

/// <summary>A bounded authoritative DNS packet responder with no forwarding or network access.</summary>
internal sealed class DnsResponder
{
    private const ushort InternetClass = 1;
    private const int MaximumNameBytes = 255;
    private const int MaximumPointers = 32;
    private const int MaximumUdpBytes = 512;
    private const int MaximumTcpBytes = ushort.MaxValue;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string[] zones;
    private readonly HashSet<string> knownNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Answer>> answers = new(StringComparer.Ordinal);

    // Snapshot validated configuration and prepare bounded wire data without adding runtime configuration paths.
    internal DnsResponder(IReadOnlyList<string> zones, IReadOnlyList<DnsRecord> records)
    {
        this.zones = zones.ToArray();
        knownNames.UnionWith(this.zones);
        foreach (DnsRecord record in records)
        {
            if (!answers.TryGetValue(record.Name, out List<Answer>? namedAnswers))
            {
                namedAnswers = [];
                answers.Add(record.Name, namedAnswers);
            }
            namedAnswers.Add(CreateAnswer(record));

            // Empty nonterminal names exist when a configured record is present beneath them.
            string name = record.Name;
            while (IsInConfiguredZone(name))
            {
                knownNames.Add(name);
                int separator = name.IndexOf('.');
                if (separator < 0)
                    break;
                name = name[(separator + 1)..];
            }
        }
    }

    // Return one RFC 1035 message while preserving query identity and requesting TCP after bounded UDP truncation.
    public byte[] Respond(byte[] query, bool tcp)
    {
        ushort identifier = query.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(query) : (ushort)0;
        bool recursionDesired = query.Length >= 4 && (query[2] & 1) != 0;
        Question question;
        try
        {
            question = ReadQuestion(query);
        }
        catch (FormatException)
        {
            return CreateResponse(identifier, recursionDesired, 1, authoritative: false, question: null);
        }

        if (!IsInConfiguredZone(question.Name))
            return CreateResponse(identifier, recursionDesired, 5, authoritative: false, question);
        if (!knownNames.Contains(question.Name))
            return CreateResponse(identifier, recursionDesired, 3, authoritative: true, question);

        List<Answer> matching = answers.TryGetValue(question.Name, out List<Answer>? namedAnswers)
            ? namedAnswers.Where(answer => answer.Type == question.Type).ToList() : [];
        byte[] prefix = CreateResponse(identifier, recursionDesired, 0, authoritative: true, question);
        using var response = new MemoryStream();
        response.Write(prefix);
        int limit = tcp ? MaximumTcpBytes : MaximumUdpBytes;
        foreach (Answer answer in matching)
        {
            // Every RR has a two-byte owner pointer and ten fixed bytes before its bounded RDATA.
            if (response.Length + 12 + answer.Data.Length > limit)
            {
                if (tcp)
                    return CreateResponse(identifier, recursionDesired, 2, authoritative: false, question);
                return CreateResponse(identifier, recursionDesired, 0, authoritative: true, question, truncated: true);
            }
            WriteAnswer(response, answer);
        }
        byte[] result = response.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6, 2), checked((ushort)matching.Count));
        return result;
    }

    // Require one IN question and structurally skip optional records without interpreting EDNS capabilities.
    private static Question ReadQuestion(byte[] query)
    {
        if (query.Length is < 12 or > MaximumTcpBytes)
            throw new FormatException("Invalid DNS message length.");
        ushort flags = ReadUInt16(query, 2);
        if ((flags & 0xf800) != 0 || ReadUInt16(query, 4) != 1
            || ReadUInt16(query, 6) != 0 || ReadUInt16(query, 8) != 0)
            throw new FormatException("Only a standard query with one question is supported.");
        string name = ReadName(query, 12, out int position);
        RequireBytes(query, position, 4);
        ushort type = ReadUInt16(query, position);
        ushort queryClass = ReadUInt16(query, position + 2);
        if (queryClass != InternetClass)
            throw new FormatException("Only DNS class IN is supported.");
        position += 4;
        int additionalCount = ReadUInt16(query, 10);
        for (int index = 0; index < additionalCount; index++)
        {
            _ = ReadName(query, position, out position);
            RequireBytes(query, position, 10);
            int dataLength = ReadUInt16(query, position + 8);
            position += 10;
            RequireBytes(query, position, dataLength);
            position += dataLength;
        }
        if (position != query.Length)
            throw new FormatException("Unexpected bytes after the declared DNS sections.");
        return new Question(name, type);
    }

    // Decode a compressed ASCII name with bounded pointer traversal and expanded wire length.
    private static string ReadName(byte[] packet, int position, out int nextPosition)
    {
        var labels = new List<string>();
        var pointerTargets = new HashSet<int>();
        int originalEnd = -1;
        int expandedBytes = 1;
        int pointerCount = 0;
        while (true)
        {
            RequireBytes(packet, position, 1);
            byte length = packet[position];
            if ((length & 0xc0) == 0xc0)
            {
                RequireBytes(packet, position, 2);
                int target = ((length & 0x3f) << 8) | packet[position + 1];
                if (++pointerCount > MaximumPointers || !pointerTargets.Add(target))
                    throw new FormatException("DNS compression traversal exceeded its bound or repeated a target.");
                if (originalEnd < 0)
                    originalEnd = position + 2;
                position = target;
                continue;
            }
            if ((length & 0xc0) != 0)
                throw new FormatException("Unsupported DNS label encoding.");
            position++;
            if (length == 0)
            {
                nextPosition = originalEnd < 0 ? position : originalEnd;
                return string.Join('.', labels);
            }
            RequireBytes(packet, position, length);
            expandedBytes += length + 1;
            if (expandedBytes > MaximumNameBytes)
                throw new FormatException("Expanded DNS name exceeds 255 wire bytes.");
            for (int index = 0; index < length; index++)
            {
                byte value = packet[position + index];
                // A literal dot inside a wire label must not alias two separately configured labels.
                if (value is < 33 or > 126 or (byte)'.')
                    throw new FormatException("DNS labels must contain unambiguous printable ASCII bytes.");
            }
            labels.Add(Encoding.ASCII.GetString(packet, position, length).ToLowerInvariant());
            position += length;
        }
    }

    // Match complete zone labels so a similarly suffixed outside domain never receives an authoritative answer.
    private bool IsInConfiguredZone(string name) => zones.Any(zone => name == zone || name.EndsWith("." + zone, StringComparison.Ordinal));

    // Construct the response header and canonical question, with no advertised recursive service or EDNS expansion.
    private static byte[] CreateResponse(ushort identifier, bool recursionDesired, ushort resultCode,
        bool authoritative, Question? question, bool truncated = false)
    {
        byte[] name = question is null ? [] : EncodeName(question.Name);
        byte[] packet = new byte[12 + (question is null ? 0 : name.Length + 4)];
        ushort flags = (ushort)(0x8000 | resultCode | (recursionDesired ? 0x0100 : 0)
            | (authoritative ? 0x0400 : 0) | (truncated ? 0x0200 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), identifier);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), flags);
        if (question is not null)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 1);
            name.CopyTo(packet, 12);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12 + name.Length, 2), question.Type);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14 + name.Length, 2), InternetClass);
        }
        return packet;
    }

    // Encode previously validated record data once instead of reparsing configuration for each request.
    private static Answer CreateAnswer(DnsRecord record)
    {
        switch (record.Type)
        {
            case "A":
                return new Answer(1, IPAddress.Parse(record.Value).GetAddressBytes());
            case "MX":
                byte[] target = EncodeName(record.Value);
                byte[] mailExchange = new byte[target.Length + 2];
                BinaryPrimitives.WriteUInt16BigEndian(mailExchange, record.Preference);
                target.CopyTo(mailExchange, 2);
                return new Answer(15, mailExchange);
            case "TXT":
                byte[] text = StrictUtf8.GetBytes(record.Value);
                using (var data = new MemoryStream())
                {
                    if (text.Length == 0)
                        data.WriteByte(0);
                    for (int start = 0; start < text.Length;)
                    {
                        int end = Math.Min(start + 255, text.Length);
                        // Keep every character-string valid UTF-8 even when a multibyte character meets the limit.
                        while (end < text.Length && (text[end] & 0xc0) == 0x80)
                            end--;
                        data.WriteByte(checked((byte)(end - start)));
                        data.Write(text.AsSpan(start, end - start));
                        start = end;
                    }
                    return new Answer(16, data.ToArray());
                }
            default:
                throw new ArgumentException("DNS record type was not validated as A, MX, or TXT.", nameof(record));
        }
    }

    // Write a direct answer pointing to the response's canonical question name and using the fixed lab TTL.
    private static void WriteAnswer(Stream output, Answer answer)
    {
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[..2], 0xc00c);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(2, 2), answer.Type);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(4, 2), InternetClass);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(6, 4), 60);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(10, 2), checked((ushort)answer.Data.Length));
        output.Write(header);
        output.Write(answer.Data);
    }

    // Encode an already bounded canonical name without compression or platform DNS APIs.
    private static byte[] EncodeName(string name)
    {
        using var result = new MemoryStream();
        if (name.Length != 0)
        {
            foreach (string label in name.Split('.'))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(label);
                result.WriteByte(checked((byte)bytes.Length));
                result.Write(bytes);
            }
        }
        result.WriteByte(0);
        return result.ToArray();
    }

    // Read a network-order field only after its complete two bytes have been established.
    private static ushort ReadUInt16(byte[] packet, int position)
    {
        RequireBytes(packet, position, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(position, 2));
    }

    // Reject truncated ranges before any wire-derived offset is used by an array operation.
    private static void RequireBytes(byte[] packet, int position, int count)
    {
        if (position < 0 || count < 0 || position > packet.Length - count)
            throw new FormatException("DNS packet ended within a declared field.");
    }

    private sealed record Question(string Name, ushort Type);
    private sealed record Answer(ushort Type, byte[] Data);
}
