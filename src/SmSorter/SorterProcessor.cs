using System.Text;
using SmTagger.Shared;

namespace SmSorter;

public sealed class SorterProcessor
{
    private readonly string inputDirectory;
    private readonly string spoolDirectory;
    private readonly string processDirectory;
    private readonly string sendersDirectory;
    private readonly TextWriter standardError;

    // Bind trusted queue roots without reading profiles, mappings, or any tagger log.
    public SorterProcessor(string dataDir, string spoolDir, TextWriter? stderr = null)
    {
        inputDirectory = Path.Combine(spoolDir, "proc");
        spoolDirectory = spoolDir;
        processDirectory = Path.Combine(dataDir, "process");
        sendersDirectory = Path.Combine(dataDir, "senders");
        standardError = stderr ?? Console.Error;
    }

    // Classify only HDR bytes, own its live trigger, and publish EML before HDR without rollback.
    public MessageOutcome Process(string basename, bool watchMode = false)
    {
        string sourceHdr = Path.Combine(inputDirectory, basename + ".hdr");
        string ownedHdr = Path.Combine(inputDirectory, basename + ".hdr.sort");
        string sourceEml = Path.Combine(inputDirectory, basename + ".eml");
        string currentHdr = sourceHdr;
        string currentEml = sourceEml;
        string operation = "read HDR";
        string? destination = null;
        string? canonicalAuth = null;
        Exception? holdReason = null;
        bool divert = false;

        try
        {
            byte[] hdr = File.ReadAllBytes(sourceHdr);
            FastHdrResult classification = FastHdrClassifier.Classify(hdr);
            canonicalAuth = classification.CanonicalAuth;
            if (classification.Kind == FastHdrKind.Unsafe)
            {
                holdReason = new InvalidDataException(classification.Reason ?? "The HDR does not establish a safe auth classification.");
            }
            else if (classification.Kind == FastHdrKind.ValidAuth && WindowsNames.IsUsableComponent(canonicalAuth!))
            {
                operation = "look up authenticated enrollment";
                FileAttributes rootAttributes = File.GetAttributes(sendersDirectory);
                if ((rootAttributes & FileAttributes.Directory) == 0)
                {
                    throw new IOException("The senders root is not a directory.");
                }

                string authDirectory = Path.Combine(sendersDirectory, canonicalAuth!);
                try
                {
                    FileAttributes attributes = File.GetAttributes(authDirectory);
                    if ((attributes & FileAttributes.Directory) == 0)
                    {
                        throw new IOException("The auth routing entry is not a directory: " + ConsoleErrors.Quote(authDirectory));
                    }

                    divert = true;
                }
                catch (FileNotFoundException)
                {
                    // Absence under the accessible trusted root proves the auth is not enrolled.
                }
                catch (DirectoryNotFoundException)
                {
                    // The immutable administrative root was established before this child lookup.
                }
            }
        }
        catch (FileNotFoundException error) when (operation == "read HDR")
        {
            return MissingBeforeOwnership(basename, watchMode, error);
        }
        catch (Exception error)
        {
            holdReason = error;
        }

        try
        {
            File.Move(sourceHdr, ownedHdr, overwrite: false);
            currentHdr = ownedHdr;
        }
        catch (FileNotFoundException error)
        {
            return MissingBeforeOwnership(basename, watchMode, error);
        }
        catch (Exception error)
        {
            throw new FatalProcessingException("Cannot make the selected HDR inert: " + ConsoleErrors.Quote(sourceHdr) + " -> " + ConsoleErrors.Quote(ownedHdr), error);
        }

        if (holdReason is not null)
        {
            ReportFailure(basename, "The HDR was held because safe routing could not be established.", operation,
                currentHdr, currentEml, destination, canonicalAuth, holdReason);
            return MessageOutcome.Failed;
        }

        try
        {
            string destinationDirectory = divert ? processDirectory : spoolDirectory;
            if (divert)
            {
                operation = "create process queue directory";
                destination = destinationDirectory;
                Directory.CreateDirectory(destinationDirectory);
            }

            operation = "move EML";
            destination = Path.Combine(destinationDirectory, basename + ".eml");
            File.Move(sourceEml, destination, overwrite: false);
            currentEml = destination;
            operation = "publish HDR";
            destination = Path.Combine(destinationDirectory, basename + ".hdr");
            File.Move(ownedHdr, destination, overwrite: false);
            return MessageOutcome.Succeeded;
        }
        catch (Exception error)
        {
            ReportFailure(basename, "The sorter could not complete the owned message's handoff.", operation,
                currentHdr, currentEml, destination, canonicalAuth, error);
            return MessageOutcome.Failed;
        }
    }

    // Name sorter-owned leftovers only in watch startup without opening or repairing them.
    public void ReportResiduals()
    {
        string[] residuals = Directory.EnumerateFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".hdr.sort", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".sort.err", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string residual in residuals)
        {
            try
            {
                ConsoleErrors.Write(standardError, "WARNING inert sorter residual " + ConsoleErrors.Quote(residual));
            }
            catch (Exception)
            {
                // Failure to format a warning must not change discovery or mail processing.
            }
        }
    }

    // Treat a missing unowned HDR as stale only when it came from watch discovery.
    private MessageOutcome MissingBeforeOwnership(string basename, bool watchMode, Exception error)
    {
        if (watchMode)
        {
            return MessageOutcome.Stale;
        }

        ConsoleErrors.WriteException(standardError,
            new FileNotFoundException("The requested plain HDR was not available for " + basename + ".", error));
        return MessageOutcome.Failed;
    }

    // Retain state evidence with non-replacing diagnostics while keeping all output best effort.
    private void ReportFailure(string basename, string reason, string operation, string currentHdr,
        string currentEml, string? destination, string? canonicalAuth, Exception error)
    {
        try
        {
            string details = reason + "\r\n"
                + "basename=" + ConsoleErrors.Quote(basename) + "\r\n"
                + "operation=" + ConsoleErrors.Quote(operation) + "\r\n"
                + "HDR=" + ConsoleErrors.Quote(currentHdr) + "\r\n"
                + "EML=" + ConsoleErrors.Quote(currentEml) + "\r\n"
                + "destination=" + ConsoleErrors.Quote(destination ?? "-") + "\r\n"
                + "canonicalAuth=" + ConsoleErrors.Quote(canonicalAuth ?? "-") + "\r\n"
                + "error=" + ConsoleErrors.FormatException(error) + "\r\n";
            ConsoleErrors.Write(standardError, details);
            string diagnostic = Path.Combine(inputDirectory, basename + ".sort.err");
            try
            {
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(details);
                using FileStream stream = new(diagnostic, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            }
            catch (Exception diagnosticError)
            {
                ConsoleErrors.WriteException(standardError,
                    new IOException("Cannot write sorter diagnostic " + ConsoleErrors.Quote(diagnostic) + ".", diagnosticError));
            }
        }
        catch (Exception)
        {
            ConsoleErrors.WriteException(standardError, error);
        }
    }
}
