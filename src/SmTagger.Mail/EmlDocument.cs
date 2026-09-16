using System.Text;
using SmTagger.Shared;

namespace SmTagger.Mail;

public sealed class EmlDocument
{
    private static readonly HashSet<string> SenderFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "From", "Reply-To", "Sender", "Resent-From", "Resent-Sender", "Return-Path",
        "Disposition-Notification-To", "Return-Receipt-To", "X-Confirm-Reading-To"
    };
    private readonly byte[] source;
    public IReadOnlyList<MailField> Fields { get; }
    public IReadOnlyList<MailField> FromFields { get; }
    public IReadOnlyList<MailField> ReplyToFields { get; }
    public IReadOnlyList<string> SenderAddresses { get; }

    // Keeps the parsed original immutable while exposing the counts needed for activation policy.
    private EmlDocument(byte[] source, IReadOnlyList<MailField> fields)
    {
        this.source = source;
        Fields = fields;
        FromFields = Array.AsReadOnly(fields.Where(field => field.Name.Equals("From", StringComparison.OrdinalIgnoreCase)).ToArray());
        ReplyToFields = Array.AsReadOnly(fields.Where(field => field.Name.Equals("Reply-To", StringComparison.OrdinalIgnoreCase)).ToArray());
        SenderAddresses = Array.AsReadOnly(fields.SelectMany(field => field.Addresses).Select(address => address.CanonicalAddress).ToArray());
    }

    // VERSION-SENSITIVE-005, VERSION-SENSITIVE-011: Parses header framing and supported sender identities, never the body.
    public static EmlDocument Parse(byte[] source)
    {
        int boundary = source.AsSpan().IndexOf("\r\n\r\n"u8);
        if (boundary < 0) throw new MailContractException("EML is missing its CRLF header/body boundary.");
        IReadOnlyList<PhysicalLine> lines = boundary == 0 ? Array.Empty<PhysicalLine>() : PhysicalLines.Scan(source.AsSpan(0, boundary + 2));
        string header = Encoding.Latin1.GetString(source, 0, boundary + 2);
        var fields = new List<MailField>();
        int index = 0;
        while (index < lines.Count)
        {
            int first = index;
            PhysicalLine line = lines[index++];
            if (line.Length == 0 || source[line.Start] is 32 or 9)
                throw new MailContractException("Orphan EML continuation or empty logical field.");
            int colon = source.AsSpan(line.Start, line.Length).IndexOf((byte)':');
            if (colon <= 0) throw new MailContractException("Invalid EML field boundary.");
            for (int i = line.Start; i < line.Start + colon; i++)
                if (source[i] is < 33 or > 126) throw new MailContractException("Invalid EML field name.");
            while (index < lines.Count && lines[index].Length > 0 && source[lines[index].Start] is 32 or 9) index++;
            string name = header.Substring(line.Start, colon);
            int valueStart = line.Start + colon + 1;
            int fieldEnd = lines[index - 1].EndIncludingCrLf;
            int valueLength = fieldEnd - 2 - valueStart;
            IReadOnlyList<MailboxAddress> addresses = Array.Empty<MailboxAddress>();
            if (name.Equals("Return-Path", StringComparison.OrdinalIgnoreCase))
                addresses = MailboxParser.ParseReturnPath(header, valueStart, valueLength);
            else if (SenderFieldNames.Contains(name))
                addresses = MailboxParser.Parse(header, valueStart, valueLength,
                    name.Equals("Sender", StringComparison.OrdinalIgnoreCase) || name.Equals("Resent-Sender", StringComparison.OrdinalIgnoreCase));
            fields.Add(new MailField(name, line.Start, fieldEnd - line.Start, valueStart, valueLength, addresses,
                Array.AsReadOnly(lines.Skip(first).Take(index - first).ToArray())));
        }
        return new EmlDocument(source, fields.AsReadOnly());
    }

    // Separates optional MDN classification from ordinary field parsing so an enabled profile can skip it.
    public bool IsMdn()
    {
        MailField[] fields = Fields.Where(field => field.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (fields.Length > 1) throw new MailContractException("Multiple Content-Type fields are ambiguous.");
        return fields.Length == 1 && ContentTypeParser.IsMdn(Encoding.Latin1.GetString(source, fields[0].ValueStart, fields[0].ValueLength));
    }

    // Rewrites an activated child using caller-validated counts and the actual individual/group tags.
    public byte[] CreateChild(string privateAddress, string individualTag, string? groupTag)
    {
        var edits = new List<ByteEdit>();
        foreach (MailField field in Fields)
        {
            if (field.Name.Equals("Return-Path", StringComparison.OrdinalIgnoreCase))
            {
                edits.Add(new(field.Start, field.Length, []));
                continue;
            }
            string target = groupTag is not null && field.Name.Equals("Reply-To", StringComparison.OrdinalIgnoreCase) ? groupTag : individualTag;
            var replacements = field.Addresses.Where(address => address.CanonicalAddress == privateAddress)
                .Select(address => new ByteEdit(address.Start, address.Length, Encoding.ASCII.GetBytes(target))).ToList();
            if (replacements.Count > 0)
            {
                CheckLineLengths(field, replacements, false);
                edits.AddRange(replacements);
            }
        }
        if (groupTag is not null && ReplyToFields.Count == 0)
        {
            MailField from = FromFields[0];
            MailboxAddress address = from.Addresses[0];
            var replacements = new List<ByteEdit>
            {
                new(from.Start, from.Name.Length, "Reply-To"u8.ToArray()),
                new(address.Start, address.Length, Encoding.ASCII.GetBytes(groupTag))
            };
            CheckLineLengths(from, replacements, true);
            byte[] original = source.AsSpan(from.Start, from.Length).ToArray();
            byte[] reply = ByteEdits.Apply(original, replacements.Select(edit => edit with { Start = edit.Start - from.Start }));
            edits.Add(new(from.Start + from.Length, 0, reply));
        }
        return ByteEdits.Apply(source, edits);
    }

    // Counts changed physical lines from parsed boundaries; synthetic fields check every output line.
    private void CheckLineLengths(MailField field, IReadOnlyList<ByteEdit> replacements, bool synthesized)
    {
        for (int i = 0; i < field.Lines.Count; i++)
        {
            PhysicalLine line = field.Lines[i];
            int length = line.Length;
            bool changed = synthesized;
            foreach (ByteEdit replacement in replacements)
            {
                if (replacement.Start < line.Start || replacement.Start >= line.End) continue;
                if (!source.AsSpan(replacement.Start, replacement.Length).SequenceEqual(replacement.Replacement)) changed = true;
                length = checked(length + replacement.Replacement.Length - replacement.Length);
            }
            if (changed && length > 998) throw new HeaderLineLengthException(synthesized ? "Reply-To" : field.Name, i + 1, length);
        }
    }
}
