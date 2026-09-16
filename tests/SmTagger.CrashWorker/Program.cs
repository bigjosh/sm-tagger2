using SmTagger.Engine;
using SmTagger.Shared;

namespace SmTagger.CrashWorker;

public static class Program
{
    // Pause the real managed pipeline at one test-selected boundary until the parent test kills this worker.
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 5)
                throw new ArgumentException("The test worker requires data directory, spool directory, basename, phase, and signal path.");
            string dataDirectory = args[0];
            string spoolDirectory = args[1];
            string basename = args[2];
            string phase = args[3];
            string signalPath = args[4];
            using var singleton = SingletonLock.Acquire(Path.Combine(dataDirectory, "sm-tagger.lock"));
            using var trace = TraceLog.Open(dataDirectory, false);
            SenderConfiguration configuration = SenderConfiguration.Load(dataDirectory, trace);
            byte proposal = 0;
            TagStore tags = TagStore.Load(dataDirectory, configuration, trace, randomBytes: bytes =>
            {
                PauseIfSelected("STAGING_READY", phase, signalPath);
                Array.Fill(bytes, ++proposal);
            });
            tags.BeforeCacheInsert = _ => PauseIfSelected("MAPPING_PUBLISHED", phase, signalPath);
            var processor = new TaggerProcessor(dataDirectory, spoolDirectory, configuration, tags, trace)
            {
                ObserveCheckpoint = checkpoint => PauseIfSelected(checkpoint.Phase, phase, signalPath)
            };
            MessageOutcome outcome = processor.Process(basename);
            ConsoleErrors.Write(Console.Error, $"The requested crash boundary {phase} was not reached; message outcome was {outcome}.");
            return 2;
        }
        catch (Exception exception)
        {
            ConsoleErrors.WriteException(Console.Error, exception);
            return 1;
        }
    }

    // Publish a closed readiness signal after the selected operation, then perform no further mail work.
    private static void PauseIfSelected(string observedPhase, string selectedPhase, string signalPath)
    {
        if (observedPhase != selectedPhase)
            return;
        string pendingSignal = signalPath + ".tmp";
        File.WriteAllText(pendingSignal, observedPhase);
        File.Move(pendingSignal, signalPath, overwrite: false);
        using var neverReleased = new ManualResetEventSlim();
        neverReleased.Wait();
    }
}
