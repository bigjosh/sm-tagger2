using SmTagger.Shared;

namespace SmTagger.Mail;

public sealed record MailboxAddress(string OriginalAddress, string CanonicalAddress, int Start, int Length);

public sealed class MailField
{
    public string Name { get; }
    public int Start { get; }
    public int Length { get; }
    public IReadOnlyList<MailboxAddress> Addresses { get; }
    internal int ValueStart { get; }
    internal int ValueLength { get; }
    internal IReadOnlyList<PhysicalLine> Lines { get; }

    // Stores one original logical field and its parser-owned address and physical-line ranges.
    internal MailField(string name, int start, int length, int valueStart, int valueLength,
        IReadOnlyList<MailboxAddress> addresses, IReadOnlyList<PhysicalLine> lines)
    {
        Name = name;
        Start = start;
        Length = length;
        ValueStart = valueStart;
        ValueLength = valueLength;
        Addresses = addresses;
        Lines = lines;
    }
}

public sealed class HeaderLineLengthException : MailContractException
{
    public string FieldName { get; }
    public int PhysicalLineNumber { get; }
    public int MeasuredLength { get; }
    public int Limit => 998;

    // Reports a resulting physical-line contract error without changing the parent/child states.
    public HeaderLineLengthException(string fieldName, int physicalLineNumber, int measuredLength)
        : base($"Header {fieldName}, physical line {physicalLineNumber}: {measuredLength} bytes exceeds the 998-byte limit excluding CRLF.")
    {
        FieldName = fieldName;
        PhysicalLineNumber = physicalLineNumber;
        MeasuredLength = measuredLength;
    }
}
