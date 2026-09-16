using System.Text;
using SmTagger.Shared;

namespace SmTagger.Mail;

public sealed class HdrDocument
{
    private readonly byte[] source;
    private readonly HdrLayout layout;
    private readonly IReadOnlyList<MailboxAddress> senders;

    public string AuthAddress { get; }
    public IReadOnlyList<string> SenderAddresses { get; }
    public ReadOnlyMemory<byte> MarkerBytes => source.AsMemory(layout.Lines[0].Start, layout.Lines[0].Length);

    // Retains the original envelope and validated identity spans for later selective edits.
    private HdrDocument(byte[] source, HdrLayout layout, string auth, IReadOnlyList<MailboxAddress> senders)
    {
        this.source = source;
        this.layout = layout;
        this.senders = senders;
        AuthAddress = auth;
        SenderAddresses = Array.AsReadOnly(senders.Select(sender => sender.CanonicalAddress).ToArray());
    }

    // VERSION-SENSITIVE-002, VERSION-SENSITIVE-005: Parses the complete HDR once while deferring recipients until activation.
    public static HdrDocument Parse(byte[] source)
    {
        HdrLayout layout = PhysicalLines.ReadHdr(source);
        var senders = new List<MailboxAddress>();
        PhysicalLine envelope = layout.Lines[1];
        AddSender(source, envelope.Start, envelope.Length, false, senders);
        string? auth = null;
        foreach (HdrMetadata metadata in layout.Metadata)
        {
            if (metadata.Key.Equals("auth", StringComparison.OrdinalIgnoreCase))
            {
                if (auth is not null) throw new MailContractException("Multiple HDR auth fields are ambiguous.");
                auth = AddressSyntax.Canonicalize(Encoding.Latin1.GetString(source, metadata.ValueStart, metadata.ValueLength).Trim(' ', '\t'));
            }
            else if (metadata.Key.Equals("from", StringComparison.OrdinalIgnoreCase))
                AddSender(source, metadata.ValueStart, metadata.ValueLength, true, senders);
        }
        if (auth is null) throw new MailContractException("A process-queue HDR requires exactly one auth field.");
        return new HdrDocument(source, layout, auth, senders.AsReadOnly());
    }

    // Parses activated recipients, preserving the first spelling of each canonical address.
    public IReadOnlyList<Recipient> ParseRecipients()
    {
        PhysicalLine line = layout.Lines[2];
        string text = Encoding.Latin1.GetString(source, line.Start, line.Length);
        var recipients = new SortedDictionary<string, Recipient>(StringComparer.Ordinal);
        foreach (string element in text.Split(','))
        {
            string original = element.Trim(' ', '\t');
            string canonical = AddressSyntax.Canonicalize(original);
            recipients.TryAdd(canonical, new Recipient(original, canonical));
        }
        return Array.AsReadOnly(recipients.Values.ToArray());
    }

    // VERSION-SENSITIVE-006: Selects this child's envelope recipient and address-matched notify metadata.
    public byte[] CreateChild(string privateAddress, string tagAddress, Recipient recipient)
    {
        PhysicalLine recipientLine = layout.Lines[2];
        var edits = new List<ByteEdit> { new(recipientLine.Start, recipientLine.Length, Encoding.ASCII.GetBytes(recipient.OriginalAddress)) };
        foreach (MailboxAddress sender in senders)
            if (sender.CanonicalAddress == privateAddress)
                edits.Add(new(sender.Start, sender.Length, Encoding.ASCII.GetBytes(tagAddress)));
        foreach (HdrMetadata metadata in layout.Metadata)
        {
            if (!metadata.Key.Equals("notify", StringComparison.OrdinalIgnoreCase)) continue;
            string value = Encoding.Latin1.GetString(source, metadata.ValueStart, metadata.ValueLength).TrimStart(' ', '\t');
            int at = value.IndexOf('@');
            int delimiter = at < 0 ? -1 : value.IndexOf('=', at + 1);
            if (delimiter > 0 && AddressSyntax.TryCanonicalize(value[..delimiter], out string canonical)
                && canonical == recipient.CanonicalAddress) continue;
            PhysicalLine line = layout.Lines[metadata.LineIndex];
            edits.Add(new(line.Start, line.Length + 2, []));
        }
        return ByteEdits.Apply(source, edits);
    }

    // Parses address paths without replacing null reverse-path spellings or surrounding metadata bytes.
    private static void AddSender(byte[] source, int start, int length, bool trim, List<MailboxAddress> senders)
    {
        if (trim)
        {
            while (length > 0 && source[start] is 32 or 9) { start++; length--; }
            while (length > 0 && source[start + length - 1] is 32 or 9) length--;
        }
        string original = Encoding.Latin1.GetString(source, start, length);
        if (original is "" or "<>") return;
        senders.Add(new MailboxAddress(original, AddressSyntax.Canonicalize(original), start, length));
    }
}
