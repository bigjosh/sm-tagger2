namespace SmTagger.LabServices;

internal static class Program
{
    // Runs this isolated lab helper or its real loopback socket checks; never starts a production processor.
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--self-test"]) return await LabSelfTest.RunAsync();
        if (args.Length == 0 || args is ["--help"])
        {
            Console.WriteLine("ONLY LOCAL LAB: sm-tagger-lab-services --config <lab.json> [--bind 127.0.0.2] [--dns-port 53] [--smtp-port 25] [--capture-dir <directory>]");
            Console.WriteLine("Use --self-test for isolated real UDP/TCP DNS and SMTP checks on ephemeral loopback ports.");
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || args[index] is not ("--config" or "--bind" or "--dns-port" or "--smtp-port" or "--capture-dir")
                    || !options.TryAdd(args[index], args[index + 1]))
                    throw new ArgumentException("Use --help; each supported option requires one value and may appear once.");
            }
            if (!options.TryGetValue("--config", out string? path)) throw new ArgumentException("--config is required.");
            LabConfiguration config = LabConfiguration.Load(path);
            if (options.TryGetValue("--bind", out string? bind)) config.BindAddress = bind;
            if (options.TryGetValue("--dns-port", out string? dns)) config.DnsPort = int.Parse(dns, System.Globalization.CultureInfo.InvariantCulture);
            if (options.TryGetValue("--smtp-port", out string? smtp)) config.SmtpPort = int.Parse(smtp, System.Globalization.CultureInfo.InvariantCulture);
            if (options.TryGetValue("--capture-dir", out string? capture)) config.CaptureDirectory = Path.GetFullPath(capture);
            var errors = new LabErrors();
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                await using var host = new LabHost(config, errors);
                Console.WriteLine($"READY ONLY LOCAL LAB DNS={host.BindAddress}:{host.DnsPort} UDP+TCP SMTP={host.BindAddress}:{host.SmtpPort} captures={config.CaptureDirectory}");
                await Task.WhenAny(host.ListenerFailure, Task.Delay(Timeout.InfiniteTimeSpan, stop.Token));
            }
            finally { Console.CancelKeyPress -= cancel; }
            Console.WriteLine($"STOPPED lab-errors={errors.Count}");
            return errors.Count == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("LAB STARTUP ERROR: " + exception);
            return 2;
        }
    }
}
