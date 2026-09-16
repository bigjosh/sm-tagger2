using System.Globalization;
using SmTagger.Mail;
using SmTagger.Shared;

namespace SmTagger.Engine;

/// <summary>Processes one enrolled queue pair while preserving every completed filesystem transition.</summary>
public sealed class TaggerProcessor
{
    private readonly string processDirectory;
    private readonly string spoolDirectory;
    private readonly SenderConfiguration configuration;
    private readonly TagStore tags;
    private readonly TraceLog trace;
    private readonly bool keep;

    // Tests observe phase boundaries without introducing selectable production fault behavior.
    internal Action<ProcessingCheckpoint>? ObserveCheckpoint { get; set; }

    // Tests wrap actual child files to exercise write and close failures without runtime fault controls.
    internal Func<string, Stream>? OpenChildFileForTesting { get; set; }

    // Binds the validated startup snapshot and permanent mapping authority to one queue.
    public TaggerProcessor(string dataDirectory, string spoolDirectory, SenderConfiguration configuration,
        TagStore tags, TraceLog trace, bool keep = false)
    {
        processDirectory = Path.Combine(dataDirectory, "process");
        this.spoolDirectory = spoolDirectory;
        this.configuration = configuration;
        this.tags = tags;
        this.trace = trace;
        this.keep = keep;
    }

    // Claims and completes one plain pair, returning a local failure without retrying retained mail.
    public MessageOutcome Process(string basename, bool watchMode = false)
    {
        var state = new MessageState(basename, Path.Combine(processDirectory, basename + ".hdr"),
            Path.Combine(processDirectory, basename + ".eml"));
        trace.Event(basename, "MESSAGE", "BEGIN", ("hdr", state.HeaderPath), ("eml", state.EmlPath));

        try
        {
            MoveParent(state, true, Path.Combine(processDirectory, basename + ".hdr.start"));
        }
        catch (FileNotFoundException exception)
        {
            if (watchMode)
            {
                trace.Event(basename, "OWNERSHIP", "STALE", ("hdr", state.HeaderPath));
                return MessageOutcome.Stale;
            }

            trace.ReportError($"Requested HDR was not found for {ConsoleErrors.Quote(basename)}.", exception);
            return MessageOutcome.Failed;
        }
        catch (Exception exception)
        {
            throw new FatalProcessingException($"Cannot make the selected HDR inert: {state.HeaderPath}", exception);
        }

        try
        {
            Checkpoint("PARENT_HDR_CLAIMED", state);
            MoveParent(state, false, Path.Combine(processDirectory, basename + ".eml.start"));
            Checkpoint("PARENT_CLAIMED", state);
            if (keep)
            {
                CopyEvidence(state, state.HeaderPath, Path.Combine(processDirectory, basename + ".hdr.in"));
                CopyEvidence(state, state.EmlPath, Path.Combine(processDirectory, basename + ".eml.in"));
            }

            // VERSION-SENSITIVE-007: Upstream admission limits bound resource use; actual read/allocation failures retain owned mail.
            state.SetOperation("parse-hdr", state.HeaderPath, null);
            var hdr = HdrDocument.Parse(File.ReadAllBytes(state.HeaderPath));
            state.SetOperation("resolve-profile", state.HeaderPath, null);
            var profile = configuration.Resolve(hdr.AuthAddress);
            state.SetOperation("parse-eml", state.EmlPath, null);
            var eml = EmlDocument.Parse(File.ReadAllBytes(state.EmlPath));
            trace.Event(basename, "PROFILE", "SELECTED", ("auth", hdr.AuthAddress),
                ("hdrMarker", hdr.MarkerBytes),
                ("senderId", profile.SenderId), ("privateAddress", profile.PrivateAddress),
                ("allowMdn", profile.AllowMdn), ("hdrSenders", hdr.SenderAddresses),
                ("emlSenders", eml.SenderAddresses));

            // VERSION-SENSITIVE-012: Only approved client paths may bypass top-level MDN classification.
            state.SetOperation("classify-mdn", state.EmlPath, null);
            if (!profile.AllowMdn && eml.IsMdn())
            {
                throw new MailContractException("Outbound message-disposition notifications are disabled for this sender.");
            }

            state.SetOperation("classify-private-identities", state.EmlPath, null);
            var senderAddresses = hdr.SenderAddresses.Concat(eml.SenderAddresses).ToArray();
            var retiredMatch = senderAddresses.FirstOrDefault(address => profile.RetiredPrivateAddresses.Contains(address));
            if (retiredMatch is not null)
            {
                throw new MailContractException($"A supported sender field contains retired private-address {retiredMatch}.");
            }

            if (!senderAddresses.Contains(profile.PrivateAddress))
            {
                trace.Event(basename, "TRIGGER", "PASS", ("reason", "No current or retired private sender identity"));
                PassUnchanged(state);
                return MessageOutcome.Succeeded;
            }

            trace.Event(basename, "TRIGGER", "TAG", ("fromFields", eml.FromFields.Count),
                ("fromMailboxCounts", eml.FromFields.Select(field => field.Addresses.Count)),
                ("replyToFields", eml.ReplyToFields.Count));
            state.SetOperation("validate-activated-header-counts", state.EmlPath, null);
            if (eml.FromFields.Count != 1 || eml.FromFields[0].Addresses.Count != 1)
            {
                RejectFromCounts(state, eml);
                return MessageOutcome.Failed;
            }

            if (eml.ReplyToFields.Count > 1)
            {
                throw new MailContractException($"Activated message has {eml.ReplyToFields.Count} Reply-To fields; at most one is allowed.");
            }

            MoveParent(state, true, Path.Combine(processDirectory, basename + ".hdr.break"));
            MoveParent(state, false, Path.Combine(processDirectory, basename + ".eml.break"));
            Checkpoint("PARENT_BROKEN", state);
            state.SetOperation("parse-recipients", state.HeaderPath, null);
            var recipients = hdr.ParseRecipients();
            for (var index = 0; index < recipients.Count; index++)
            {
                var childName = basename + "-" + checked(index + 1).ToString(CultureInfo.InvariantCulture);
                state.Children.Add(new ChildState(childName, recipients[index]));
            }

            trace.Event(basename, "RECIPIENTS", "ORDERED",
                ("recipients", recipients.Select(recipient => recipient.CanonicalAddress)));
            // VERSION-SENSITIVE-010: Replies to this permanent group tag depend on the operator's catch-all route.
            var groupNeeded = recipients.Count > 1 && (eml.ReplyToFields.Count == 0 ||
                eml.ReplyToFields[0].Addresses.Any(address => address.CanonicalAddress == profile.PrivateAddress));
            state.SetOperation("obtain-group-tag", null, null);
            var group = groupNeeded
                ? tags.GetOrCreate(profile, RecipientIdentity.Encode(recipients.Select(recipient => recipient.CanonicalAddress)), basename)
                : null;
            trace.Event(basename, "GROUP_REPLY_TO", groupNeeded ? "TAG" : "PRESERVE", ("tag", group?.TagAddress));

            foreach (var child in state.Children)
            {
                ConstructChild(state, child, hdr, eml, profile, group);
            }

            if (keep)
            {
                foreach (var child in state.Children)
                {
                    state.ActiveChild = child.Basename;
                    CopyEvidence(state, child.HeaderPath!, Path.Combine(processDirectory, child.Basename + ".hdr.out"));
                    CopyEvidence(state, child.EmlPath!, Path.Combine(processDirectory, child.Basename + ".eml.out"));
                }
            }

            Checkpoint("ALL_CHILDREN_READY", state);
            foreach (var child in state.Children)
            {
                PublishChild(state, child);
            }

            Checkpoint("BEFORE_PARENT_CLEANUP", state);
            DeleteParent(state, false);
            DeleteParent(state, true);
            trace.Event(basename, "MESSAGE", "SUCCESS", ("children", state.Children.Count));
            return MessageOutcome.Succeeded;
        }
        catch (MappingAvailabilityException exception)
        {
            ReportFailure(state, exception);
            throw;
        }
        catch (Exception exception)
        {
            ReportFailure(state, exception);
            return MessageOutcome.Failed;
        }
    }

    // Names inert message evidence at watcher startup while leaving all files untouched.
    public void ReportResiduals()
    {
        foreach (var path in Directory.EnumerateFiles(processDirectory).Order(StringComparer.Ordinal))
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".start", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".break", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".process", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".pend", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".err", StringComparison.OrdinalIgnoreCase))
            {
                trace.ReportError($"WARNING retained message evidence: {ConsoleErrors.Quote(path)}");
                trace.Event("-", "RESIDUAL", "WARNING", ("path", path));
            }
        }
    }

    // Preserves a nonactivated pair and advertises readiness only after its EML reaches the spool.
    private void PassUnchanged(MessageState state)
    {
        if (keep)
        {
            CopyEvidence(state, state.HeaderPath, Path.Combine(processDirectory, state.Basename + ".hdr.out"));
            CopyEvidence(state, state.EmlPath, Path.Combine(processDirectory, state.Basename + ".eml.out"));
        }

        MoveParent(state, false, Path.Combine(spoolDirectory, state.Basename + ".eml"));
        MoveParent(state, true, Path.Combine(spoolDirectory, state.Basename + ".hdr"));
        trace.Event(state.Basename, "MESSAGE", "SUCCESS", ("branch", "PASS"));
    }

    // Retains the original From-count violation using its distinct HDR-first error suffix procedure.
    private void RejectFromCounts(MessageState state, EmlDocument eml)
    {
        var reason = $"Activated message requires exactly one From field containing exactly one mailbox; " +
            $"observed fields={eml.FromFields.Count}, mailboxCounts=[{string.Join(",", eml.FromFields.Select(field => field.Addresses.Count))}].";
        try
        {
            MoveParent(state, true, Path.Combine(processDirectory, state.Basename + ".hdr.err"));
            Checkpoint("FROM_HDR_RETAINED", state);
            MoveParent(state, false, Path.Combine(processDirectory, state.Basename + ".eml.err"));
        }
        catch (Exception exception)
        {
            ReportFailure(state, new MailContractException(reason + " Retention move failed: " + exception.Message, exception));
            return;
        }

        state.SetOperation("validate-From-counts", state.EmlPath, null);
        ReportFailure(state, new MailContractException(reason));
    }

    // Materializes one child with final tags and validates edited lines before either readiness move.
    private void ConstructChild(MessageState state, ChildState child, HdrDocument hdr, EmlDocument eml,
        SenderProfile profile, TagMapping? group)
    {
        state.ActiveChild = child.Basename;
        state.SetOperation("obtain-individual-tag", null, null);
        var individual = tags.GetOrCreate(profile, RecipientIdentity.Encode([child.Recipient.CanonicalAddress]), state.Basename);
        child.Mappings.Add(individual);
        if (group is not null)
        {
            child.Mappings.Add(group);
        }

        // VERSION-SENSITIVE-005, VERSION-SENSITIVE-006, VERSION-SENSITIVE-011: Approved queue/client bytes govern these edits.
        state.SetOperation("rewrite-eml-and-check-header-length", state.EmlPath,
            Path.Combine(processDirectory, child.Basename + ".eml.process"));
        var emlBytes = eml.CreateChild(profile.PrivateAddress, individual.TagAddress, group?.TagAddress);
        state.SetOperation("construct-child-envelope", state.HeaderPath,
            Path.Combine(processDirectory, child.Basename + ".hdr.process"));
        var hdrBytes = hdr.CreateChild(profile.PrivateAddress, individual.TagAddress, child.Recipient);
        CreateChildFile(state, child, false, emlBytes);
        CreateChildFile(state, child, true, hdrBytes);
        MoveChild(state, child, false, Path.Combine(processDirectory, child.Basename + ".eml.pend"));
        MoveChild(state, child, true, Path.Combine(processDirectory, child.Basename + ".hdr.pend"));
        Checkpoint("CHILD_READY", state, child.Basename);
    }

    // Attempts mapping diagnostics immediately before sequential EML-first/HDR-last publication.
    private void PublishChild(MessageState state, ChildState child)
    {
        // VERSION-SENSITIVE-008: SmarterMail must add the authoritative aligned DKIM signature after these rewritten bytes become live.
        state.ActiveChild = child.Basename;
        Checkpoint("BEFORE_CHILD_PUBLICATION", state, child.Basename);
        tags.AppendPublicationLogs(child.Mappings, child.Basename, state.Basename);
        try
        {
            MoveChild(state, child, false, Path.Combine(spoolDirectory, child.Basename + ".eml"));
        }
        catch
        {
            child.Status = "EML_MOVE_FAILED";
            throw;
        }

        Checkpoint("CHILD_EML_PUBLISHED", state, child.Basename);
        try
        {
            MoveChild(state, child, true, Path.Combine(spoolDirectory, child.Basename + ".hdr"));
        }
        catch
        {
            child.Status = "HDR_MOVE_FAILED_EML_IN_SPOOL";
            throw;
        }

        child.Status = "PUBLISHED";
        Checkpoint("CHILD_PUBLISHED", state, child.Basename);
    }

    // Creates and closes a child artifact without replacing a collision, retaining partial writes on failure.
    private void CreateChildFile(MessageState state, ChildState child, bool header, byte[] content)
    {
        var destination = Path.Combine(processDirectory, child.Basename + (header ? ".hdr.process" : ".eml.process"));
        state.SetOperation("create", null, destination);
        trace.Transition(state.Basename, "create", null, destination, () =>
        {
            using var stream = OpenChildFileForTesting is null
                ? new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                : OpenChildFileForTesting(destination);
            if (header)
            {
                child.HeaderPath = destination;
            }
            else
            {
                child.EmlPath = destination;
            }

            stream.Write(content);
        }, "Complete closed child .process file");
    }

    // Moves one parent artifact and records only the state established by a successful move.
    private void MoveParent(MessageState state, bool header, string destination)
    {
        var source = header ? state.HeaderPath : state.EmlPath;
        state.SetOperation("move", source, destination);
        trace.Transition(state.Basename, "move", source, destination, () =>
        {
            // VERSION-SENSITIVE-001, VERSION-SENSITIVE-003, VERSION-SENSITIVE-004: Same-volume nonreplacement is the queue contract.
            File.Move(source, destination, overwrite: false);
            if (header)
            {
                state.HeaderPath = destination;
            }
            else
            {
                state.EmlPath = destination;
            }
        });
    }

    // Moves one constructed child artifact without rolling back a prior successful transition.
    private void MoveChild(MessageState state, ChildState child, bool header, string destination)
    {
        var source = (header ? child.HeaderPath : child.EmlPath)!;
        state.SetOperation("move", source, destination);
        trace.Transition(state.Basename, "move", source, destination, () =>
        {
            File.Move(source, destination, overwrite: false);
            if (header)
            {
                child.HeaderPath = destination;
            }
            else
            {
                child.EmlPath = destination;
            }
        });
    }

    // Keeps debugging copies outside the execution trace as required by the -keep contract.
    private static void CopyEvidence(MessageState state, string source, string destination)
    {
        state.SetOperation("copy", source, destination);
        File.Copy(source, destination, overwrite: false);
    }

    // Removes a successfully consumed parent, accepting an already absent file as complete cleanup.
    private void DeleteParent(MessageState state, bool header)
    {
        var path = header ? state.HeaderPath : state.EmlPath;
        state.SetOperation("delete", path, null);
        trace.Transition(state.Basename, "delete", path, null, () =>
        {
            File.Delete(path);
            if (header)
            {
                state.HeaderRemoved = true;
            }
            else
            {
                state.EmlRemoved = true;
            }
        });
    }

    // Writes best-effort diagnostics while preserving the underlying message outcome and exact residual state.
    private void ReportFailure(MessageState state, Exception exception)
    {
        try
        {
            WriteFailureDetails(state, exception);
        }
        catch (Exception diagnosticException)
        {
            trace.ReportError("Could not prepare the message diagnostic; the underlying mail failure and retained files are unchanged.",
                diagnosticException);
        }
    }

    // Assembles protected human-readable state inside the nonthrowing diagnostic boundary.
    private void WriteFailureDetails(MessageState state, Exception exception)
    {
        var details = new List<(string Name, object? Value)>
        {
            ("basename", state.Basename), ("activeChild", state.ActiveChild),
            ("operation", state.Operation), ("source", state.OperationSource), ("destination", state.OperationDestination),
            ("parentHdr", state.HeaderRemoved ? "ABSENT" : state.HeaderPath),
            ("parentEml", state.EmlRemoved ? "ABSENT" : state.EmlPath),
            ("exception", ConsoleErrors.FormatException(exception))
        };
        foreach (var child in state.Children)
        {
            details.Add(("child", child.Basename));
            details.Add(("status", child.Status));
            details.Add(("recipient", child.Recipient.CanonicalAddress));
            details.Add(("childHdr", child.HeaderPath ?? "NOT_CREATED"));
            details.Add(("childEml", child.EmlPath ?? "NOT_CREATED"));
        }

        trace.Event(state.Basename, "MESSAGE", "ERROR", details.ToArray());
        trace.ReportError("Message failed; " + string.Join(" ", details.Select(detail =>
            detail.Name + "=" + TraceLog.Quote(Convert.ToString(detail.Value, CultureInfo.InvariantCulture) ?? ""))));
        trace.WriteDiagnostic(Path.Combine(processDirectory, state.Basename + ".err"), exception.Message, details);
    }

    // Exposes a few meaningful boundaries to tests; production composition never sets an observer.
    private void Checkpoint(string phase, MessageState state, string? childBasename = null)
    {
        ObserveCheckpoint?.Invoke(new ProcessingCheckpoint(phase, state.Basename, childBasename));
    }

    // Starts parent evidence tracking at the supplied source paths and records each known transition.
    private sealed class MessageState(string basename, string headerPath, string emlPath)
    {
        public string Basename { get; } = basename;
        public string HeaderPath { get; set; } = headerPath;
        public string EmlPath { get; set; } = emlPath;
        public bool HeaderRemoved { get; set; }
        public bool EmlRemoved { get; set; }
        public List<ChildState> Children { get; } = [];
        public string? ActiveChild { get; set; }
        public string? Operation { get; private set; }
        public string? OperationSource { get; private set; }
        public string? OperationDestination { get; private set; }

        // Retains the attempted operation even when it throws before its state update.
        public void SetOperation(string operation, string? source, string? destination)
        {
            Operation = operation;
            OperationSource = source;
            OperationDestination = destination;
        }
    }

    // Associates one canonical recipient with its planned child and eventual publication evidence.
    private sealed class ChildState(string basename, Recipient recipient)
    {
        public string Basename { get; } = basename;
        public Recipient Recipient { get; } = recipient;
        public string? HeaderPath { get; set; }
        public string? EmlPath { get; set; }
        public string Status { get; set; } = "UNATTEMPTED";
        public List<TagMapping> Mappings { get; } = [];
    }
}

// Describes a test observation boundary without exposing a runtime fault-selection mechanism.
internal sealed record ProcessingCheckpoint(string Phase, string ParentBasename, string? ChildBasename);
