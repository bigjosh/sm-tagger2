using System.Text;
using SmTagger.Shared;

namespace SmTagger.Mail;

internal sealed class ContentTypeParser
{
    private readonly string text;
    private int position;

    // Creates a cursor over one logical Content-Type body without decoding MIME content.
    private ContentTypeParser(string text) => this.text = text;

    // VERSION-SENSITIVE-012: Classifies only the specified top-level standard MDN forms.
    public static bool IsMdn(string text)
    {
        var parser = new ContentTypeParser(text);
        string type = parser.Token();
        parser.Expect('/');
        string subtype = parser.Token();
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (parser.SkipWhite() < text.Length)
        {
            parser.Expect(';');
            string attribute = parser.Token();
            parser.Expect('=');
            string value = parser.Value();
            if (!parameters.TryAdd(attribute, value)) throw new MailContractException("Duplicate Content-Type parameter.");
        }
        return (type.Equals("message", StringComparison.OrdinalIgnoreCase) && subtype.Equals("disposition-notification", StringComparison.OrdinalIgnoreCase))
            || (type.Equals("multipart", StringComparison.OrdinalIgnoreCase) && subtype.Equals("report", StringComparison.OrdinalIgnoreCase)
                && parameters.TryGetValue("report-type", out string? reportType)
                && reportType.Equals("disposition-notification", StringComparison.OrdinalIgnoreCase));
    }

    // Reads one nonempty MIME token using the explicit separator alphabet.
    private string Token()
    {
        SkipWhite();
        int start = position;
        while (position < text.Length && IsToken(text[position])) position++;
        if (start == position) throw new MailContractException("Expected nonempty Content-Type token.");
        return text[start..position];
    }

    // Reads a parameter value and decodes quoted pairs only for media-type classification.
    private string Value()
    {
        SkipWhite();
        if (position >= text.Length || text[position] != '"') return Token();
        position++;
        var value = new StringBuilder();
        while (position < text.Length)
        {
            char next = text[position++];
            if (next == '"')
            {
                if (value.Length == 0) throw new MailContractException("Empty Content-Type parameter value.");
                return value.ToString();
            }
            if (next == '\\')
            {
                if (position >= text.Length || (text[position] is < ' ' or > '~' && text[position] != '\t'))
                    throw new MailContractException("Invalid Content-Type quoted-pair escape.");
                value.Append(text[position++]);
            }
            else if (next == '\r')
            {
                position--;
                SkipWhite();
                value.Append(' ');
            }
            else
            {
                if (next is < ' ' or > '~' && next != '\t') throw new MailContractException("Invalid Content-Type quoted byte.");
                value.Append(next);
            }
        }
        throw new MailContractException("Unclosed Content-Type quoted string.");
    }

    // Requires the next structural delimiter after optional legal whitespace.
    private void Expect(char expected)
    {
        SkipWhite();
        if (position >= text.Length || text[position++] != expected) throw new MailContractException($"Expected '{expected}' in Content-Type.");
    }

    // Advances over the same legal folding grammar used by mailbox fields.
    private int SkipWhite() => position = MailboxParser.SkipWhite(text, position, text.Length);

    // Limits MIME tokens to printable ASCII excluding the MIME separator characters.
    private static bool IsToken(char value) => value is >= '!' and <= '~' && !"()<>@,;:\\\"/[]?=".Contains(value);
}
