namespace SmTagger.Mail;

internal sealed record ByteEdit(int Start, int Length, byte[] Replacement);

internal static class ByteEdits
{
    // Applies disjoint parser-owned edits from the end, copying all untouched bytes exactly once.
    public static byte[] Apply(byte[] source, IEnumerable<ByteEdit> edits)
    {
        ByteEdit[] ordered = edits.OrderByDescending(edit => edit.Start).ThenByDescending(edit => edit.Length).ToArray();
        int length = source.Length;
        foreach (ByteEdit edit in ordered) length = checked(length + edit.Replacement.Length - edit.Length);
        byte[] result = new byte[length];
        int sourceEnd = source.Length;
        int outputEnd = result.Length;
        foreach (ByteEdit edit in ordered)
        {
            int unchangedLength = sourceEnd - edit.Start - edit.Length;
            outputEnd -= unchangedLength;
            source.AsSpan(edit.Start + edit.Length, unchangedLength).CopyTo(result.AsSpan(outputEnd));
            outputEnd -= edit.Replacement.Length;
            edit.Replacement.CopyTo(result, outputEnd);
            sourceEnd = edit.Start;
        }
        source.AsSpan(0, sourceEnd).CopyTo(result);
        return result;
    }
}
