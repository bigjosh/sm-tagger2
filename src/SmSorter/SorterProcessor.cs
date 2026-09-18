using System.Text;
using SmTagger.Shared;

namespace SmSorter;

public sealed class SorterProcessor
{
    private readonly string inputDirectory;
    private readonly string spoolDirectory;
    private readonly string processDirectory;
    private readonly string failedDirectory;
    private readonly string authAddressesDirectory;
    private readonly TextWriter standardError;
    private readonly SorterDiagnostics? diagnostics;

    // Bind trusted queue roots and the auth-address index without reading sender records, mappings, or tagger logs.
    public SorterProcessor(string dataDir, string spoolDir, TextWriter? stderr = null, SorterDiagnostics? diagnostics = null)
    {
        inputDirectory = Path.Combine(spoolDir, "proc");
        spoolDirectory = spoolDir;
        processDirectory = MailQueuePaths.ProcessDirectory(spoolDir);
        failedDirectory = MailQueuePaths.FailedDirectory(spoolDir);
        authAddressesDirectory = Path.Combine(dataDir, "senders", "auth-addresses");
        standardError = stderr ?? Console.Error;
        this.diagnostics = diagnostics;
    }

    // Wait for producer readiness, classify only HDR bytes, and publish EML before HDR without rollback.
    public MessageOutcome Process(string basename, bool watchMode = false)
    {
        string sourceHdr = Path.Combine(inputDirectory, basename + ".hdr");
        string ownedHdr = Path.Combine(inputDirectory, basename + ".hdr.sort");
        string sourceEml = Path.Combine(inputDirectory, basename + ".eml");
        string currentHdr = sourceHdr;
        string currentEml = sourceEml;
        string operation = "check EML readiness";
        string? destination = null;
        string? canonicalAuth = null;
        Exception? holdReason = null;
        bool divert = false;
        string routeReason = "NO_AUTH";

        try
        {
            // VERSION-SENSITIVE-001: Final EML visibility admits a candidate, not ownership or delivery.
            try
            {
                FileAttributes attributes = File.GetAttributes(sourceEml);
                if ((attributes & FileAttributes.Directory) != 0)
                    throw new IOException("The companion EML is not a regular file.");
            }
            catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                if (watchMode)
                {
                    diagnostics?.Debug(basename, "STALE", ("operation", operation));
                    return MessageOutcome.Stale;
                }
                return NotReady(basename, watchMode, "EML_NOT_AVAILABLE", sourceHdr, sourceEml);
            }
            operation = "read HDR";
            diagnostics?.Debug(basename, "READ_HDR", ("path", sourceHdr));
            byte[] hdr = File.ReadAllBytes(sourceHdr);
            int statusEnd = hdr.AsSpan().IndexOf("\r\n"u8);
            // VERSION-SENSITIVE-001: Only Written permits routing; retain the read's exclusion of writers.
            // Unknown or incomplete statuses remain producer-owned; see written-readiness-2026-09-17.md.
            if (statusEnd < 0)
                return NotReady(basename, watchMode, "HDR_STATUS_UNEXPECTED", sourceHdr, sourceEml, "<missing CRLF>");
            ReadOnlySpan<byte> status = hdr.AsSpan(0, statusEnd).TrimEnd(" \t"u8);
            if (status.SequenceEqual("Writing"u8))
                return NotReady(basename, watchMode, "HDR_WRITING", sourceHdr, sourceEml, "Writing");
            if (!status.SequenceEqual("Written"u8) && !status.SequenceEqual("Failed"u8))
                return NotReady(basename, watchMode, "HDR_STATUS_UNEXPECTED", sourceHdr, sourceEml,
                    Encoding.Latin1.GetString(status));
            operation = "classify HDR";
            if (status.SequenceEqual("Failed"u8))
            {
                routeReason = "UPSTREAM_FAILED";
                holdReason = new InvalidDataException("SmarterMail marked the message Failed.");
            }
            else
            {
                FastHdrResult classification = FastHdrClassifier.Classify(hdr);
                canonicalAuth = classification.CanonicalAuth;
                if (classification.Kind == FastHdrKind.Unsafe)
                {
                    routeReason = "UNSAFE_HDR";
                    holdReason = new InvalidDataException(classification.Reason ?? "The HDR does not establish a safe auth classification.");
                }
                else if (classification.Kind == FastHdrKind.ValidAuth && WindowsNames.IsUsableComponent(canonicalAuth!))
                {
                    operation = "look up authenticated enrollment";
                    routeReason = "UNENROLLED_AUTH";
                    diagnostics?.Debug(basename, "AUTH_LOOKUP", ("auth", canonicalAuth), ("root", authAddressesDirectory));
                    FileAttributes rootAttributes = File.GetAttributes(authAddressesDirectory);
                    if ((rootAttributes & FileAttributes.Directory) == 0)
                    {
                        throw new IOException("The senders/auth-addresses root is not a directory.");
                    }

                    string authDirectory = Path.Combine(authAddressesDirectory, canonicalAuth!);
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(authDirectory);
                        if ((attributes & FileAttributes.Directory) == 0)
                        {
                            throw new IOException("The auth routing entry is not a directory: " + ConsoleErrors.Quote(authDirectory));
                        }

                        divert = true;
                        routeReason = "ENROLLED_AUTH";
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
                else if (classification.Kind == FastHdrKind.ValidAuth)
                {
                    routeReason = "UNENROLLABLE_AUTH";
                }
            }
        }
        catch (FileNotFoundException error) when (operation == "read HDR")
        {
            return MissingBeforeOwnership(basename, watchMode, error, canonicalAuth, "read HDR");
        }
        catch (IOException error) when (operation == "read HDR" && (error.HResult & 0xffff) is 32 or 33)
        {
            return NotReady(basename, watchMode, "HDR_BUSY", sourceHdr, sourceEml);
        }
        catch (Exception error)
        {
            holdReason = error;
            routeReason = operation switch
            {
                "read HDR" => "HDR_READ_FAILED",
                "check EML readiness" => "INPUT_READINESS_FAILED",
                "classify HDR" => "UNSAFE_HDR",
                _ => "AUTH_LOOKUP_FAILED"
            };
        }

        diagnostics?.Debug(basename, "ROUTE", ("auth", canonicalAuth), ("reason", routeReason),
            ("decision", holdReason is not null ? "HOLD" : divert ? "DIVERT" : "PASS"));
        try
        {
            diagnostics?.Debug(basename, "MOVE_INTENT", ("operation", "claim HDR"), ("source", sourceHdr), ("destination", ownedHdr));
            File.Move(sourceHdr, ownedHdr, overwrite: false);
            currentHdr = ownedHdr;
            diagnostics?.Debug(basename, "MOVE_OK", ("operation", "claim HDR"), ("hdr", currentHdr));
        }
        catch (FileNotFoundException error)
        {
            return MissingBeforeOwnership(basename, watchMode, error, canonicalAuth, "claim HDR");
        }
        catch (Exception error)
        {
            diagnostics?.Message(basename, "ERROR", canonicalAuth, "HDR_CLAIM_FAILED", "claim HDR",
                currentHdr, currentEml, ownedHdr, error);
            throw new FatalProcessingException("Cannot make the selected HDR inert: " + ConsoleErrors.Quote(sourceHdr) + " -> " + ConsoleErrors.Quote(ownedHdr), error);
        }

        if (holdReason is not null)
        {
            if (routeReason == "UPSTREAM_FAILED")
                return RetainUpstreamFailure(basename, currentHdr, currentEml, holdReason);

            diagnostics?.Message(basename, "ERROR", canonicalAuth, routeReason, operation,
                currentHdr, currentEml, destination, holdReason);
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
                diagnostics?.Debug(basename, "CREATE_DIRECTORY", ("path", destinationDirectory));
                Directory.CreateDirectory(destinationDirectory);
            }

            operation = "move EML";
            destination = Path.Combine(destinationDirectory, basename + ".eml");
            diagnostics?.Debug(basename, "MOVE_INTENT", ("operation", operation), ("source", sourceEml), ("destination", destination));
            File.Move(sourceEml, destination, overwrite: false);
            currentEml = destination;
            diagnostics?.Debug(basename, "MOVE_OK", ("operation", operation), ("eml", currentEml));
            operation = "publish HDR";
            destination = Path.Combine(destinationDirectory, basename + ".hdr");
            diagnostics?.Debug(basename, "MOVE_INTENT", ("operation", operation), ("source", ownedHdr), ("destination", destination));
            File.Move(ownedHdr, destination, overwrite: false);
            currentHdr = destination;
            diagnostics?.Debug(basename, "MOVE_OK", ("operation", operation), ("hdr", currentHdr));
            diagnostics?.Message(basename, divert ? "DIVERT" : "PASS", canonicalAuth, routeReason,
                operation, currentHdr, currentEml, destination);
            return MessageOutcome.Succeeded;
        }
        catch (Exception error)
        {
            diagnostics?.Message(basename, "ERROR", canonicalAuth, "HANDOFF_FAILED", operation,
                currentHdr, currentEml, destination, error);
            ReportFailure(basename, "The sorter could not complete the owned message's handoff.", operation,
                currentHdr, currentEml, destination, canonicalAuth, error);
            return MessageOutcome.Failed;
        }
    }

    // Preserve upstream-failed bytes outside SmarterMail's queues without publishing, replacing, or rolling back files.
    private MessageOutcome RetainUpstreamFailure(string basename, string currentHdr, string currentEml, Exception upstreamError)
    {
        string operation = "create failed retention directory";
        string destination = failedDirectory;
        string reason = "SmarterMail marked the message Failed; retained files require manual investigation.";
        Exception reportedError = upstreamError;
        try
        {
            // VERSION-SENSITIVE-001: SM deletes Failed pairs returned to spool; see failed-spool-2026-09-16.md.
            diagnostics?.Debug(basename, "CREATE_DIRECTORY", ("path", failedDirectory));
            Directory.CreateDirectory(failedDirectory);
            operation = "retain failed EML";
            destination = Path.Combine(failedDirectory, basename + ".eml");
            diagnostics?.Debug(basename, "MOVE_INTENT", ("operation", operation), ("source", currentEml), ("destination", destination));
            File.Move(currentEml, destination, overwrite: false);
            currentEml = destination;
            diagnostics?.Debug(basename, "MOVE_OK", ("operation", operation), ("eml", currentEml));
            operation = "retain failed HDR";
            destination = Path.Combine(failedDirectory, basename + ".hdr.sort");
            diagnostics?.Debug(basename, "MOVE_INTENT", ("operation", operation), ("source", currentHdr), ("destination", destination));
            File.Move(currentHdr, destination, overwrite: false);
            currentHdr = destination;
            diagnostics?.Debug(basename, "MOVE_OK", ("operation", operation), ("hdr", currentHdr));
        }
        catch (Exception error)
        {
            reason = "SmarterMail marked the message Failed; the sorter could not complete retention. Files remain at the recorded locations.";
            reportedError = error;
        }

        diagnostics?.Message(basename, "ERROR", null, "UPSTREAM_FAILED", operation,
            currentHdr, currentEml, destination, reportedError);
        ReportFailure(basename, reason, operation, currentHdr, currentEml, destination, null, reportedError);
        return MessageOutcome.Failed;
    }

    // Leave unready inputs untouched; report unexpected statuses even in watch mode without terminal logging.
    private MessageOutcome NotReady(string basename, bool watchMode, string reason, string hdr, string eml,
        string? status = null)
    {
        try
        {
            diagnostics?.Debug(basename, "DEFER", ("reason", reason), ("hdr", hdr), ("eml", eml), ("status", status));
            if (!watchMode || reason == "HDR_STATUS_UNEXPECTED")
            {
                ConsoleErrors.Write(standardError, "NOT READY sorter input left untouched basename=" + ConsoleErrors.Quote(basename)
                    + " reason=" + reason
                    + (status is null ? "" : " status=" + ConsoleErrors.Quote(status))
                    + " HDR=" + ConsoleErrors.Quote(hdr) + " EML=" + ConsoleErrors.Quote(eml));
            }
        }
        catch (Exception)
        {
            // Diagnostic formatting cannot turn a producer-owned input into an owned failure.
        }
        return MessageOutcome.Deferred;
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
    private MessageOutcome MissingBeforeOwnership(string basename, bool watchMode, Exception error, string? auth, string operation)
    {
        if (watchMode)
        {
            diagnostics?.Debug(basename, "STALE", ("operation", operation));
            return MessageOutcome.Stale;
        }

        diagnostics?.Message(basename, "ERROR", auth, "MISSING_HDR", operation,
            Path.Combine(inputDirectory, basename + ".hdr"), Path.Combine(inputDirectory, basename + ".eml"), error: error);
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
            string diagnostic = Path.Combine(Path.GetDirectoryName(currentHdr)!, basename + ".sort.err");
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
