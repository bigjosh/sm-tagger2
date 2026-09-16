namespace SmTagger.Shared;

public sealed class QueueWatcher : IDisposable
{
    private readonly string inputDirectory;
    private readonly Func<string, MessageOutcome> processMessage;
    private readonly Action<IReadOnlyList<string>>? onScan;
    private readonly Func<Action<Exception>, TimeSpan, bool>? waitForNotification;
    private readonly AutoResetEvent wakeup = new(false);
    private Exception? watcherFailure;
    private int stopRequested;
    private int started;
    private int disposed;

    // Keep discovery separate from the sequential processor and its message-local failure policy.
    public QueueWatcher(string inputDirectory, Func<string, MessageOutcome> processMessage,
        Action<IReadOnlyList<string>>? onScan = null)
        : this(inputDirectory, processMessage, onScan, null)
    {
    }

    // Allow tests to replace only notification waiting and errors without runtime fault controls.
    internal QueueWatcher(string inputDirectory, Func<string, MessageOutcome> processMessage,
        Action<IReadOnlyList<string>>? onScan, Func<Action<Exception>, TimeSpan, bool>? waitForNotification)
    {
        this.inputDirectory = inputDirectory;
        this.processMessage = processMessage;
        this.onScan = onScan;
        this.waitForNotification = waitForNotification;
    }

    // VERSION-SENSITIVE-001, VERSION-SENSITIVE-013: Notifications are hints; complete scans establish queue work.
    public void Run()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("A queue watcher can run only once.");
        }

        if (Volatile.Read(ref stopRequested) != 0)
        {
            return;
        }

        using FileSystemWatcher? watcher = waitForNotification is null ? StartNotifications() : null;

        while (Volatile.Read(ref stopRequested) == 0)
        {
            ThrowWatcherFailure();
            string[] basenames;
            try
            {
                basenames = Directory.EnumerateFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => Path.GetExtension(path).Equals(".hdr", StringComparison.OrdinalIgnoreCase))
                    .Select(path => Path.GetFileName(path)[..^4])
                    .Order(StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception error)
            {
                throw new FatalProcessingException("Cannot enumerate the input queue " + ConsoleErrors.Quote(inputDirectory) + ".", error);
            }

            onScan?.Invoke(basenames);
            foreach (string basename in basenames)
            {
                ThrowWatcherFailure();
                if (Volatile.Read(ref stopRequested) != 0)
                {
                    break;
                }

                processMessage(basename);
            }

            ThrowWatcherFailure();
            if (Volatile.Read(ref stopRequested) == 0)
            {
                if (waitForNotification is null)
                {
                    wakeup.WaitOne(TimeSpan.FromSeconds(30));
                }
                else
                {
                    waitForNotification(RecordWatcherError, TimeSpan.FromSeconds(30));
                }
            }
        }

        ThrowWatcherFailure();
    }

    // Wake idle discovery and prevent the next claim while allowing owned work to finish.
    public void RequestStop()
    {
        Interlocked.Exchange(ref stopRequested, 1);
        Wake();
    }

    // Release the signal after the owning composition root has finished the processing loop.
    public void Dispose()
    {
        RequestStop();
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            wakeup.Dispose();
        }
    }

    // Enable native filename hints before scanning and release the handle if setup fails.
    private FileSystemWatcher StartNotifications()
    {
        FileSystemWatcher watcher = new(inputDirectory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName,
            Filter = "*"
        };
        try
        {
            watcher.Created += OnNotification;
            watcher.Deleted += OnNotification;
            watcher.Changed += OnNotification;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
    }

    // Ignore event paths and kinds because the next complete scan is authoritative.
    private void OnNotification(object sender, FileSystemEventArgs args)
    {
        Wake();
    }

    // Treat renames as the same wake-up hint as other filename notifications.
    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        Wake();
    }

    // Recover an overflow by scanning, but retain any loss of reliable monitoring as fatal.
    private void OnError(object sender, ErrorEventArgs args)
    {
        RecordWatcherError(args.GetException());
    }

    // Share the actual native error path with deterministic overflow and monitoring-loss tests.
    private void RecordWatcherError(Exception error)
    {
        if (error is not InternalBufferOverflowException)
        {
            Interlocked.CompareExchange(ref watcherFailure, error, null);
        }

        Wake();
    }

    // Permit a final asynchronous notification to race harmlessly with signal disposal.
    private void Wake()
    {
        try
        {
            wakeup.Set();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown has already ended discovery.
        }
    }

    // Surface an asynchronous watcher failure before another message can be claimed.
    private void ThrowWatcherFailure()
    {
        Exception? error = Volatile.Read(ref watcherFailure);
        if (error is not null)
        {
            throw new FatalProcessingException("Reliable monitoring of the input queue was lost.", error);
        }
    }
}
