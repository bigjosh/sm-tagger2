using System.Buffers;
using System.Net.Sockets;
using System.Text;

namespace SmTagger.LabServices;

internal sealed class WireLineReader
{
    private readonly NetworkStream stream;
    private readonly byte[] buffer = new byte[8192];
    private int start;
    private int end;

    // Buffers socket bytes without text decoding or newline normalization.
    public WireLineReader(NetworkStream stream) => this.stream = stream;

    // Reads one bounded exact CRLF line, retaining its terminator, or returns null on clean EOF.
    public async Task<byte[]?> ReadAsync(int maximumBytes, CancellationToken cancellation)
    {
        var result = new ArrayBufferWriter<byte>();
        while (true)
        {
            if (start == end)
            {
                start = 0;
                end = await stream.ReadAsync(buffer, cancellation);
                if (end == 0)
                {
                    if (result.WrittenCount != 0) throw new InvalidDataException("Unterminated SMTP line at EOF.");
                    return null;
                }
            }
            int newline = Array.IndexOf(buffer, (byte)10, start, end - start);
            int count = (newline < 0 ? end : newline + 1) - start;
            if (result.WrittenCount + count > maximumBytes) throw new InvalidDataException("SMTP physical line exceeds the lab bound.");
            result.Write(buffer.AsSpan(start, count));
            start += count;
            if (newline < 0) continue;
            byte[] line = result.WrittenSpan.ToArray();
            if (line.Length < 2 || line[^2] != 13) throw new InvalidDataException("SMTP requires exact CRLF framing.");
            return line;
        }
    }

    // Writes a fixed ASCII SMTP reply without changing incoming message bytes.
    public static ValueTask WriteAsync(NetworkStream stream, string text, CancellationToken cancellation)
        => stream.WriteAsync(Encoding.ASCII.GetBytes(text), cancellation);
}
