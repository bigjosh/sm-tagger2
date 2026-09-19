using SmTagger.Shared;

namespace SmTagger.Tests;

public sealed class QueueWatcherTests
{
    // Scan only direct backlog in ordinal order, excluding suffixed files and the nested private working queues.
    [Theory]
    [InlineData(".hdr")]
    [InlineData(".eml")]
    public void StartupScanUsesExactFinalExtensionAndOrdinalOrder(string inputExtension)
    {
        using SorterTestDirectory tree = new();
        string otherExtension = inputExtension == ".hdr" ? ".eml" : ".hdr";
        foreach (string filename in new[] { "z" + inputExtension, "a" + inputExtension.ToUpperInvariant(),
            "B" + inputExtension, "ignored" + inputExtension + ".start", "ignored" + inputExtension + "x", "orphan" + otherExtension,
            "42615432-1" + inputExtension + ".pend", "42615432c1" + inputExtension + ".pend" })
        {
            File.WriteAllText(tree.Input(filename), "fixture");
        }

        Directory.CreateDirectory(tree.Input("directory" + inputExtension));
        Directory.CreateDirectory(tree.Work("process"));
        Directory.CreateDirectory(tree.Work("failed"));
        File.WriteAllText(tree.Work("process/child" + inputExtension), "queued working fixture");
        File.WriteAllText(tree.Work("failed/child" + inputExtension), "retained working fixture");
        List<string> observed = [];
        List<string[]> scans = [];
        QueueWatcher? active = null;
        using QueueWatcher watcher = new(tree.Input(""), basename =>
        {
            observed.Add(basename);
            if (observed.Count == 3)
            {
                active!.RequestStop();
            }

            return MessageOutcome.Succeeded;
        }, basenames => scans.Add(basenames.ToArray()), inputExtension: inputExtension);
        active = watcher;
        watcher.Run();
        Assert.Equal(["B", "a", "z"], observed);
        Assert.Equal(["B", "a", "z"], Assert.Single(scans));
        Assert.Equal("queued working fixture", File.ReadAllText(tree.Work("process/child" + inputExtension)));
        Assert.Equal("retained working fixture", File.ReadAllText(tree.Work("failed/child" + inputExtension)));
        Assert.Equal("fixture", File.ReadAllText(tree.Input("42615432-1" + inputExtension + ".pend")));
        Assert.Equal("fixture", File.ReadAllText(tree.Input("42615432c1" + inputExtension + ".pend")));
    }

    // A stop recorded before discovery prevents any initial message claim.
    [Fact]
    public void StopBeforeRunClaimsNothing()
    {
        using SorterTestDirectory tree = new();
        tree.AddMessage("queued");
        int calls = 0;
        using QueueWatcher watcher = new(tree.Input(""), _ =>
        {
            calls++;
            return MessageOutcome.Succeeded;
        });
        watcher.RequestStop();
        watcher.Run();
        Assert.Equal(0, calls);
    }

    // A stop requested during one processor call permits its completion but no next claim.
    [Fact]
    public void StopDuringProcessingFinishesCurrentCallOnly()
    {
        using SorterTestDirectory tree = new();
        tree.AddMessage("first");
        tree.AddMessage("second");
        List<string> observed = [];
        bool completed = false;
        QueueWatcher? active = null;
        using QueueWatcher watcher = new(tree.Input(""), basename =>
        {
            observed.Add(basename);
            active!.RequestStop();
            completed = true;
            return MessageOutcome.Failed;
        });
        active = watcher;
        watcher.Run();
        Assert.True(completed);
        Assert.Equal(["first"], observed);
    }

    // Native filename notifications wake an idle loop and discover a subsequently published HDR.
    [Fact]
    public async Task ArrivalAfterStartupIsDiscovered()
    {
        using SorterTestDirectory tree = new();
        TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource initialScan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueWatcher? active = null;
        using QueueWatcher watcher = new(tree.Input(""), basename =>
        {
            if (basename == "arrival")
            {
                observed.TrySetResult();
                active!.RequestStop();
            }

            return MessageOutcome.Succeeded;
        }, _ => initialScan.TrySetResult());
        active = watcher;
        Task running = Task.Run(watcher.Run);
        try
        {
            await initialScan.Task.WaitAsync(TimeSpan.FromSeconds(5));
            tree.AddMessage("arrival");
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            watcher.RequestStop();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    // A simulated expired idle wait discovers work despite the absence of native notifications.
    [Fact]
    public void MissedNotificationIsReconsideredAfterThirtySecondIdleWait()
    {
        using SorterTestDirectory tree = new();
        List<string[]> scans = [];
        int waits = 0;
        QueueWatcher? active = null;
        using QueueWatcher watcher = new(tree.Input(""), basename =>
        {
            Assert.Equal("missed", basename);
            active!.RequestStop();
            return MessageOutcome.Succeeded;
        }, basenames => scans.Add(basenames.ToArray()), (_, timeout) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(30), timeout);
            Assert.Equal(1, ++waits);
            tree.AddMessage("missed");
            return false;
        });
        active = watcher;
        watcher.Run();
        Assert.Equal(1, waits);
        Assert.Equal(2, scans.Count);
        Assert.Empty(scans[0]);
        Assert.Equal(["missed"], scans[1]);
    }

    // Overflow uses the normal error callback to force a complete scan without failing processing.
    [Fact]
    public void OverflowRescansAndProcessesCurrentQueue()
    {
        using SorterTestDirectory tree = new();
        int scans = 0;
        int processed = 0;
        QueueWatcher? active = null;
        using QueueWatcher watcher = new(tree.Input(""), basename =>
        {
            Assert.Equal("after-overflow", basename);
            processed++;
            active!.RequestStop();
            return MessageOutcome.Succeeded;
        }, _ => scans++, (reportError, _) =>
        {
            tree.AddMessage("after-overflow");
            reportError(new InternalBufferOverflowException("synthetic overflow"));
            return true;
        });
        active = watcher;
        watcher.Run();
        Assert.Equal(2, scans);
        Assert.Equal(1, processed);
    }

    // Irrecoverable monitoring loss prevents the next claim even if more plain files now exist.
    [Fact]
    public void UnrecoverableWatcherErrorStopsBeforeAnotherClaim()
    {
        using SorterTestDirectory tree = new();
        IOException monitoringError = new("synthetic monitoring loss");
        int processed = 0;
        using QueueWatcher watcher = new(tree.Input(""), _ =>
        {
            processed++;
            return MessageOutcome.Succeeded;
        }, null, (reportError, _) =>
        {
            tree.AddMessage("waiting");
            reportError(monitoringError);
            return true;
        });
        FatalProcessingException failure = Assert.Throws<FatalProcessingException>(watcher.Run);
        Assert.Same(monitoringError, failure.InnerException);
        Assert.Equal(0, processed);
        Assert.True(File.Exists(tree.Input("waiting.hdr")));
    }

    // An asynchronous monitoring error during a call allows that owned work to finish first.
    [Fact]
    public void MonitoringFailureDuringOwnedWorkCompletesOnlyThatCall()
    {
        using SorterTestDirectory tree = new();
        Action<Exception>? reportFailure = null;
        List<string> observed = [];
        bool completed = false;
        using QueueWatcher watcher = new(tree.Input(""), basename =>
        {
            observed.Add(basename);
            reportFailure!(new IOException("synthetic monitoring loss"));
            completed = true;
            return MessageOutcome.Succeeded;
        }, null, (reportError, _) =>
        {
            reportFailure = reportError;
            tree.AddMessage("first");
            tree.AddMessage("second");
            return true;
        });
        Assert.Throws<FatalProcessingException>(watcher.Run);
        Assert.True(completed);
        Assert.Equal(["first"], observed);
    }

    // A failed complete enumeration is fatal even when the notification wait itself succeeds.
    [Fact]
    public void EnumerationFailureStopsDiscovery()
    {
        using SorterTestDirectory tree = new();
        int scans = 0;
        using QueueWatcher watcher = new(tree.Input(""), _ => throw new InvalidOperationException("No claim expected."),
            _ => scans++, (_, _) =>
            {
                Directory.Delete(tree.Input(""));
                return false;
            });
        FatalProcessingException failure = Assert.Throws<FatalProcessingException>(watcher.Run);
        Assert.IsType<DirectoryNotFoundException>(failure.InnerException);
        Assert.Equal(1, scans);
    }

    // Discovery preserves fatal processor failures rather than silently claiming another message.
    [Fact]
    public void FatalProcessorErrorEscapesTheWatchLoop()
    {
        using SorterTestDirectory tree = new();
        tree.AddMessage("first");
        tree.AddMessage("second");
        int calls = 0;
        using QueueWatcher watcher = new(tree.Input(""), _ =>
        {
            calls++;
            throw new FatalProcessingException("cannot make HDR inert");
        });
        Assert.Throws<FatalProcessingException>(watcher.Run);
        Assert.Equal(1, calls);
    }
}
