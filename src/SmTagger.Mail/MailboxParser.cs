using SmTagger.Shared;

namespace SmTagger.Mail;

internal static class MailboxParser
{
    // Parses bounded mailbox lists while retaining exact address offsets in the original byte string.
    public static IReadOnlyList<MailboxAddress> Parse(string text, int start, int length, bool single)
    {
        var result = new List<MailboxAddress>();
        int end = start + length;
        int elementStart = start;
        int angleStart = -1;
        int angleEnd = -1;
        for (int i = start; i <= end; i++)
        {
            if (i == end || text[i] == ',')
            {
                if (angleStart >= 0 && angleEnd < 0) throw new MailContractException("Unclosed mailbox angle path.");
                result.Add(ParseElement(text, elementStart, i, angleStart, angleEnd));
                elementStart = i + 1;
                angleStart = angleEnd = -1;
                continue;
            }
            if (text[i] == '"') { i = QuotedEnd(text, i, end) - 1; continue; }
            if (i + 1 < end && text[i] == '=' && text[i + 1] == '?' && TryEncodedEnd(text, i, end, out int encodedEnd))
            {
                i = encodedEnd - 1;
                continue;
            }
            if (text[i] == '<')
            {
                if (angleStart >= 0) throw new MailContractException("Multiple angle paths in one mailbox.");
                angleStart = i;
            }
            else if (text[i] == '>')
            {
                if (angleStart < 0 || angleEnd >= 0) throw new MailContractException("Unmatched mailbox angle bracket.");
                angleEnd = i;
            }
        }
        if (single && result.Count != 1) throw new MailContractException("This sender field requires one mailbox.");
        return result.AsReadOnly();
    }

    // Recognizes only Return-Path's angle address or null path; display names and lists are forbidden.
    public static IReadOnlyList<MailboxAddress> ParseReturnPath(string text, int start, int length)
    {
        int end = start + length;
        TrimWhite(text, ref start, ref end);
        if (end - start < 2 || text[start] != '<' || text[end - 1] != '>')
            throw new MailContractException("Return-Path requires one angle address or <>.");
        if (end - start == 2) return Array.Empty<MailboxAddress>();
        int addressStart = start + 1;
        int addressEnd = end - 1;
        TrimWhite(text, ref addressStart, ref addressEnd);
        return Array.AsReadOnly(new[] { Address(text, addressStart, addressEnd) });
    }

    // Identifies the sole address in a bare or named mailbox after list boundaries are known.
    private static MailboxAddress ParseElement(string text, int start, int end, int angleStart, int angleEnd)
    {
        TrimWhite(text, ref start, ref end);
        if (angleStart < 0) return Address(text, start, end);
        if (angleEnd != end - 1) throw new MailContractException("Unexpected bytes after mailbox angle path.");
        ValidateDisplay(text, start, angleStart);
        start = angleStart + 1;
        end = angleEnd;
        TrimWhite(text, ref start, ref end);
        return Address(text, start, end);
    }

    // Converts one contiguous unquoted addr-spec span without losing its original spelling.
    private static MailboxAddress Address(string text, int start, int end)
    {
        string original = text[start..end];
        return new MailboxAddress(original, AddressSyntax.Canonicalize(original), start, end - start);
    }

    // Validates the allowed quoted display name or whitespace-separated ASCII/encoded-word tokens.
    private static void ValidateDisplay(string text, int start, int end)
    {
        TrimWhite(text, ref start, ref end);
        if (start == end) return;
        if (text[start] == '"')
        {
            if (QuotedEnd(text, start, end) != end) throw new MailContractException("Unexpected bytes after quoted display name.");
            return;
        }
        while (start < end)
        {
            if (start + 1 < end && text[start] == '=' && text[start + 1] == '?'
                && TryEncodedEnd(text, start, end, out int encodedEnd) && (encodedEnd == end || IsWhite(text[encodedEnd]))) start = encodedEnd;
            else
            {
                int tokenStart = start;
                while (start < end && !IsWhite(text[start]))
                {
                    char value = text[start++];
                    if (value is < '!' or > '~' || "()<> ,:;\\\"".Contains(value))
                        throw new MailContractException("Unsupported unquoted display-name syntax.");
                }
                if (start == tokenStart) throw new MailContractException("Empty display-name token.");
            }
            if (start < end && !IsWhite(text[start])) throw new MailContractException("Display-name tokens require whitespace.");
            start = SkipWhite(text, start, end);
        }
    }

    // Validates quoted display text and escapes without decoding or modifying source bytes.
    private static int QuotedEnd(string text, int start, int end)
    {
        for (int i = start + 1; i < end; i++)
        {
            char value = text[i];
            if (value == '"') return i + 1;
            if (value == '\\')
            {
                if (++i >= end || (text[i] is < ' ' or > '~' && text[i] != '\t'))
                    throw new MailContractException("Invalid quoted display-name escape.");
            }
            else if (value == '\r') i = SkipWhite(text, i, end) - 1;
            else if (value is < ' ' or > '~' && value != '\t') throw new MailContractException("Invalid quoted display-name byte.");
        }
        throw new MailContractException("Unclosed quoted display name.");
    }

    // Recognizes an opaque RFC 2047 encoded-word so internal punctuation cannot split a mailbox.
    private static int EncodedEnd(string text, int start, int end)
    {
        int charsetEnd = text.IndexOf('?', start + 2, end - start - 2);
        if (charsetEnd <= start + 2 || charsetEnd + 3 >= end || text[charsetEnd + 2] != '?'
            || text[charsetEnd + 1] is not ('b' or 'B' or 'q' or 'Q'))
            throw new MailContractException("Malformed encoded display-name word.");
        for (int i = start + 2; i < charsetEnd; i++)
            if (text[i] is < '!' or > '~' || "()<>@,;:\\\"/[]?=".Contains(text[i]))
                throw new MailContractException("Malformed encoded-word charset.");
        int payloadStart = charsetEnd + 3;
        int payloadEnd = text.IndexOf('?', payloadStart, end - payloadStart);
        if (payloadEnd <= payloadStart || payloadEnd + 1 >= end || text[payloadEnd + 1] != '=')
            throw new MailContractException("Malformed encoded-word terminator.");
        for (int i = payloadStart; i < payloadEnd; i++)
            if (text[i] is < '!' or > '~') throw new MailContractException("Invalid encoded-word payload byte.");
        return payloadEnd + 2;
    }

    // A literal dot-atom may contain =?; only complete encoded-words hide structural display punctuation.
    private static bool TryEncodedEnd(string text, int start, int end, out int encodedEnd)
    {
        try
        {
            encodedEnd = EncodedEnd(text, start, end);
            return true;
        }
        catch (MailContractException)
        {
            encodedEnd = start;
            return false;
        }
    }

    // Removes permitted outer folding whitespace while leaving all retained address offsets exact.
    private static void TrimWhite(string text, ref int start, ref int end)
    {
        start = SkipWhite(text, start, end);
        while (end > start && IsWhite(text[end - 1])) end--;
    }

    // Consumes SP/HTAB and legal folded CRLF while rejecting a bare or nonfolded line ending.
    internal static int SkipWhite(string text, int start, int end)
    {
        while (start < end)
        {
            if (text[start] is ' ' or '\t') { start++; continue; }
            if (text[start] == '\r')
            {
                if (start + 2 >= end || text[start + 1] != '\n' || text[start + 2] is not (' ' or '\t'))
                    throw new MailContractException("Invalid folding whitespace.");
                start += 2;
                continue;
            }
            if (text[start] == '\n') throw new MailContractException("Bare LF in folding whitespace.");
            break;
        }
        return start;
    }

    // Classifies only the header grammar's explicit whitespace characters.
    private static bool IsWhite(char value) => value is ' ' or '\t' or '\r' or '\n';
}
