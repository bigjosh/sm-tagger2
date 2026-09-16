namespace SmTagger.Shared;

public static class AddressSyntax
{
    private const string AtomPunctuation = "!#$%&'*+-/=?^_`{|}~";

    // Validates the bounded ASCII addr-spec grammar and returns its canonical identity.
    public static string Canonicalize(string address)
    {
        if (!TryCanonicalize(address, out string canonical))
            throw new MailContractException($"Invalid version 1 addr-spec: {address}");
        return canonical;
    }

    // Validates address syntax without imposing an additional transport-length policy.
    public static bool TryCanonicalize(string address, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrEmpty(address)) return false;
        int at = address.IndexOf('@');
        if (at <= 0 || at == address.Length - 1 || address.LastIndexOf('@') != at) return false;
        bool atomEmpty = true;
        for (int i = 0; i < at; i++)
        {
            char value = address[i];
            if (value == '.')
            {
                if (atomEmpty) return false;
                atomEmpty = true;
            }
            else
            {
                if (!IsLetterOrDigit(value) && !AtomPunctuation.Contains(value)) return false;
                atomEmpty = false;
            }
        }
        if (atomEmpty) return false;
        int labelStart = at + 1;
        for (int i = labelStart; i <= address.Length; i++)
        {
            if (i == address.Length || address[i] == '.')
            {
                if (i == labelStart || address[labelStart] == '-' || address[i - 1] == '-') return false;
                labelStart = i + 1;
            }
            else if (!IsLetterOrDigit(address[i]) && address[i] != '-') return false;
        }
        canonical = string.Create(address.Length, address, static (output, input) =>
        {
            for (int i = 0; i < input.Length; i++)
                output[i] = input[i] is >= 'A' and <= 'Z' ? (char)(input[i] + ('a' - 'A')) : input[i];
        });
        return true;
    }

    // Keeps ASCII identity independent of culture and Unicode character categories.
    private static bool IsLetterOrDigit(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
