using System.Text;

namespace SmTagger.Shared;

public static class ConsoleErrors
{
    // Write an operator diagnostic without making its failure a mail-processing dependency.
    public static void Write(TextWriter writer, string message)
    {
        try
        {
            writer.WriteLine(message);
            writer.Flush();
        }
        catch (Exception)
        {
            // Standard error is the final best-effort channel; there is no recursive fallback.
        }
    }

    // Keep arbitrary values on one diagnostic line while preserving printable Unicode text.
    public static string Quote(string value) => QuoteCore(value, escapeBackslashes: true);

    // Keep console scalar values on one line while leaving literal Windows paths readable.
    public static string QuoteForDisplay(string value) => QuoteCore(value, escapeBackslashes: false);

    // Encode scalar controls and quotes, with backslash escaping reserved for structured file records.
    private static string QuoteCore(string value, bool escapeBackslashes)
    {
        StringBuilder result = new(value.Length + 2);
        result.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '\\' when escapeBackslashes: result.Append("\\\\"); break;
                case '"': result.Append("\\\""); break;
                case '\r': result.Append("\\r"); break;
                case '\n': result.Append("\\n"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (char.IsControl(character))
                    {
                        foreach (byte encoded in Encoding.UTF8.GetBytes(character.ToString()))
                        {
                            result.Append("\\x").Append(encoded.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                        }
                    }
                    else
                    {
                        result.Append(character);
                    }

                    break;
            }
        }

        return result.Append('"').ToString();
    }

    // Render the complete managed exception chain with its real line breaks and literal path spelling.
    public static string FormatException(Exception exception)
    {
        return exception.ToString();
    }

    // Protect exception formatting as well as the final standard-error write.
    public static void WriteException(TextWriter writer, Exception exception)
    {
        try
        {
            Write(writer, "ERROR " + FormatException(exception));
        }
        catch (Exception)
        {
            // Diagnostic formatting is also best effort.
        }
    }
}
