using System.Text;
using SmTagger.Shared;

namespace SmTagger.Mail;

public sealed record Recipient(string OriginalAddress, string CanonicalAddress);

public static class RecipientIdentity
{
    // Encodes already canonical addresses in their stable sorted, deduplicated representation.
    public static string Encode(IEnumerable<string> canonicalAddresses)
    {
        string[] addresses = canonicalAddresses.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (addresses.Length == 0) throw new MailContractException("A recipient identity cannot be empty.");
        return string.Concat(addresses.Select(address => address.Replace("%", "%%", StringComparison.Ordinal)
            .Replace(";", "%;", StringComparison.Ordinal) + ";"));
    }

    // Decodes persistent identity bytes and rejects noncanonical, duplicate, or unsorted representations.
    public static IReadOnlyList<string> Decode(string identity)
    {
        string text = identity.TrimEnd('\r', '\n');
        var addresses = new List<string>();
        var current = new StringBuilder();
        bool delimiter = false;
        for (int i = 0; i < text.Length; i++)
        {
            char value = text[i];
            delimiter = false;
            if (value == '%')
            {
                if (++i >= text.Length || text[i] is not ('%' or ';'))
                    throw new MailContractException("Invalid recipient-id escape.");
                current.Append(text[i]);
            }
            else if (value == ';')
            {
                string address = current.ToString();
                if (AddressSyntax.Canonicalize(address) != address ||
                    (addresses.Count > 0 && StringComparer.Ordinal.Compare(addresses[^1], address) >= 0))
                    throw new MailContractException("Recipient-id addresses must be canonical and strictly increasing.");
                addresses.Add(address);
                current.Clear();
                delimiter = true;
            }
            else current.Append(value);
        }
        if (!delimiter || addresses.Count == 0 || Encode(addresses) != text)
            throw new MailContractException("Recipient-id requires its canonical terminal delimiter.");
        return addresses.AsReadOnly();
    }
}
