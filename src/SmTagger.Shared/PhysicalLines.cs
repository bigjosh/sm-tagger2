using System.Text;

namespace SmTagger.Shared;

public readonly record struct PhysicalLine(int Start, int Length)
{
    public int End => Start + Length;
    public int EndIncludingCrLf => End + 2;
}

public sealed record HdrMetadata(string Key, int ValueStart, int ValueLength, int LineIndex);
public sealed record HdrLayout(IReadOnlyList<PhysicalLine> Lines, IReadOnlyList<HdrMetadata> Metadata);

public static class PhysicalLines
{
    // Locates terminated physical lines without decoding or normalizing their content.
    public static IReadOnlyList<PhysicalLine> Scan(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<PhysicalLine>();
        int start = 0;
        for (int position = 0; position < bytes.Length; position++)
        {
            if (bytes[position] == 10) throw new MailContractException("Bare LF in header framing.");
            if (bytes[position] != 13) continue;
            if (position + 1 >= bytes.Length || bytes[position + 1] != 10)
                throw new MailContractException("Bare CR in header framing.");
            lines.Add(new PhysicalLine(start, position - start));
            position++;
            start = position + 1;
        }
        if (start != bytes.Length) throw new MailContractException("Header line is not CRLF-terminated.");
        return lines.AsReadOnly();
    }

    // VERSION-SENSITIVE-005: Shares exact proprietary HDR framing between the fast and full parsers.
    public static HdrLayout ReadHdr(byte[] bytes)
    {
        IReadOnlyList<PhysicalLine> lines = Scan(bytes);
        if (lines.Count < 4 || lines[^1].Length != 0)
            throw new MailContractException("HDR requires three positional lines and a final blank line.");
        var metadata = new List<HdrMetadata>();
        for (int i = 3; i < lines.Count - 1; i++)
        {
            PhysicalLine line = lines[i];
            int colon = bytes.AsSpan(line.Start, line.Length).IndexOf((byte)':');
            if (colon <= 0) throw new MailContractException($"Invalid HDR metadata key on line {i + 1}.");
            for (int j = 0; j < colon; j++)
                if (bytes[line.Start + j] is < 33 or > 126)
                    throw new MailContractException($"Invalid HDR metadata key on line {i + 1}.");
            metadata.Add(new HdrMetadata(Encoding.Latin1.GetString(bytes, line.Start, colon),
                line.Start + colon + 1, line.Length - colon - 1, i));
        }
        return new HdrLayout(lines, metadata.AsReadOnly());
    }
}
