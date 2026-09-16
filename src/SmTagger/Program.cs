using SmTagger.Engine;
using SmTagger.Shared;

namespace SmTagger;

public static class Program
{
    // Composes the tagger's single-owner lifecycle without exposing test dependencies to runtime inputs.
    public static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("sm-tagger requires the approved Windows/NTFS deployment.");
            }

            Invocation invocation;
            try
            {
                invocation = Invocation.ParseTagger(args);
            }
            catch (ArgumentException exception)
            {
                ConsoleErrors.Write(Console.Error, "ERROR " + ConsoleErrors.Quote(exception.Message));
                ConsoleErrors.Write(Console.Error, Invocation.TaggerUsage);
                return 1;
            }

            using var singleton = SingletonLock.Acquire(Path.Combine(invocation.DataDirectory, "sm-tagger.lock"));
            using var trace = TraceLog.Open(invocation.DataDirectory, invocation.Log);
            trace.Event("-", "STARTUP", "BEGIN", ("dataDirectory", invocation.DataDirectory),
                ("spoolDirectory", invocation.SpoolDirectory), ("basename", invocation.Basename),
                ("keep", invocation.Keep), ("watch", invocation.IsWatchMode));
            try
            {
                var configuration = SenderConfiguration.Load(invocation.DataDirectory, trace);
                var tags = TagStore.Load(invocation.DataDirectory, configuration, trace);
                var processDirectory = Path.Combine(invocation.DataDirectory, "process");
                trace.Transition("-", "create-directory", null, processDirectory,
                    () => Directory.CreateDirectory(processDirectory));
                var processor = new TaggerProcessor(invocation.DataDirectory, invocation.SpoolDirectory,
                    configuration, tags, trace, invocation.Keep);
                return Run(invocation, processor, trace);
            }
            catch (Exception exception)
            {
                trace.Event("-", "FATAL", "ERROR", ("exception", ConsoleErrors.FormatException(exception)));
                throw;
            }
        }
        catch (Exception exception)
        {
            ConsoleErrors.WriteException(Console.Error, exception);
            return 1;
        }
    }

    // Finishes owned work on console cancellation and keeps one-shot mode free of a watcher.
    private static int Run(Invocation invocation, TaggerProcessor processor, TraceLog trace)
    {
        QueueWatcher? watcher = null;
        var stopRequested = 0;
        ConsoleCancelEventHandler handler = (_, cancellation) =>
        {
            cancellation.Cancel = true;
            Interlocked.Exchange(ref stopRequested, 1);
            watcher?.RequestStop();
        };
        Console.CancelKeyPress += handler;
        try
        {
            if (!invocation.IsWatchMode)
            {
                if (Volatile.Read(ref stopRequested) != 0)
                {
                    return 0;
                }

                var outcome = processor.Process(invocation.Basename!);
                trace.Event("-", "SHUTDOWN", outcome == MessageOutcome.Succeeded ? "SUCCESS" : "ERROR");
                return outcome == MessageOutcome.Succeeded ? 0 : 1;
            }

            processor.ReportResiduals();
            watcher = new QueueWatcher(Path.Combine(invocation.DataDirectory, "process"),
                basename => processor.Process(basename, watchMode: true),
                basenames => trace.Event("-", "QUEUE_SCAN", "OK", ("count", basenames.Count), ("basenames", basenames)));
            if (Volatile.Read(ref stopRequested) != 0)
            {
                watcher.RequestStop();
            }

            watcher.Run();
            trace.Event("-", "SHUTDOWN", "SUCCESS");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            watcher?.Dispose();
        }
    }
}
