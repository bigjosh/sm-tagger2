using System.Text;

namespace SmTagger.Shared;

public enum FastHdrKind { NoAuth, ValidAuth, Unsafe }
public sealed record FastHdrResult(FastHdrKind Kind, string? CanonicalAuth = null, string? Reason = null);

public static class FastHdrClassifier
{
    // VERSION-SENSITIVE-002, VERSION-SENSITIVE-005: Proves auth absence or identity without interpreting EML content.
    public static FastHdrResult Classify(byte[] bytes)
    {
        try
        {
            HdrLayout layout = PhysicalLines.ReadHdr(bytes);
            string? auth = null;
            foreach (HdrMetadata metadata in layout.Metadata)
            {
                if (!metadata.Key.Equals("auth", StringComparison.OrdinalIgnoreCase)) continue;
                if (auth is not null) throw new MailContractException("Multiple HDR auth fields are ambiguous.");
                auth = AddressSyntax.Canonicalize(Encoding.Latin1.GetString(bytes, metadata.ValueStart, metadata.ValueLength).Trim(' ', '\t'));
            }
            return auth is null ? new(FastHdrKind.NoAuth) : new(FastHdrKind.ValidAuth, auth);
        }
        catch (MailContractException error)
        {
            return new(FastHdrKind.Unsafe, Reason: error.Message);
        }
    }
}
