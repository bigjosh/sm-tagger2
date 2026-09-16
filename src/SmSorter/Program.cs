using SmTagger.Shared;

namespace SmSorter;

public static class Program
{
    // Own invocation validation, singleton lifetime, cancellation, and process exit handling.
    public static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("sm-sorter requires the approved Windows/NTFS deployment.");
            }

            Invocation invocation;
            try
            {
                invocation = Invocation.ParseSorter(args);
            }
            catch (ArgumentException error)
            {
                ConsoleErrors.Write(Console.Error, "ERROR " + ConsoleErrors.Quote(error.Message));
                ConsoleErrors.Write(Console.Error, Invocation.SorterUsage);
                return 1;
            }

            string inputDirectory = Path.Combine(invocation.SpoolDirectory, "proc");
            using SingletonLock singleton = SingletonLock.Acquire(Path.Combine(inputDirectory, "sm-sorter.lock"));
            using SorterDiagnostics diagnostics = SorterDiagnostics.Open(invocation.SorterLogPath, invocation.Verbose);
            diagnostics.Debug("-", "STARTUP", ("datadir", invocation.DataDirectory), ("spooldir", invocation.SpoolDirectory),
                ("mode", invocation.IsWatchMode ? "watch" : "one-shot"), ("logfile", invocation.SorterLogPath));
            SorterProcessor processor = new(invocation.DataDirectory, invocation.SpoolDirectory, diagnostics: diagnostics);
            try
            {
                int exitCode = Run(invocation, inputDirectory, processor, diagnostics);
                diagnostics.Debug("-", "STOP", ("exitCode", exitCode));
                return exitCode;
            }
            catch (Exception error)
            {
                diagnostics.Debug("-", "FATAL", ("error", error));
                throw;
            }
        }
        catch (Exception error)
        {
            ConsoleErrors.WriteException(Console.Error, error);
            return 1;
        }
    }

    // Defer console termination until owned work finishes in either one-shot or watch mode.
    private static int Run(Invocation invocation, string inputDirectory, SorterProcessor processor, SorterDiagnostics diagnostics)
    {
        QueueWatcher? watcher = null;
        int stopRequested = 0;
        // Record cancellation even before watch discovery has been constructed.
        void RequestShutdown(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            Interlocked.Exchange(ref stopRequested, 1);
            watcher?.RequestStop();
        }

        Console.CancelKeyPress += RequestShutdown;
        try
        {
            if (!invocation.IsWatchMode)
            {
                if (Volatile.Read(ref stopRequested) != 0)
                {
                    return 0;
                }

                return processor.Process(invocation.Basename!) == MessageOutcome.Succeeded ? 0 : 1;
            }

            processor.ReportResiduals();
            watcher = new QueueWatcher(inputDirectory, basename => processor.Process(basename, watchMode: true),
                basenames => diagnostics.Debug("-", "QUEUE_SCAN", ("count", basenames.Count)));
            if (Volatile.Read(ref stopRequested) != 0)
            {
                watcher.RequestStop();
            }

            watcher.Run();
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= RequestShutdown;
            watcher?.Dispose();
        }
    }
}
