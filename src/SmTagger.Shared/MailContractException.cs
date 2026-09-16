namespace SmTagger.Shared;

public class MailContractException : Exception
{
    // Identifies malformed mail or a violated message contract for retained-state handling.
    public MailContractException(string message, Exception? innerException = null) : base(message, innerException) { }
}
