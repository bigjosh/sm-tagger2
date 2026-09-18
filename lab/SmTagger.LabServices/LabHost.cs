using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace SmTagger.LabServices;

internal sealed class LabHost : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly TcpListener dnsTcp;
    private readonly TcpListener smtpTcp;
    private readonly UdpClient dnsUdp;
    private readonly DnsResponder dns;
    private readonly SmtpCapture smtp;
    private readonly LabErrors errors;
    private readonly HashSet<Task> clients = [];
    private readonly Task[] loops;
    private readonly TaskCompletionSource listenerFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IPAddress BindAddress { get; }
    public int DnsPort { get; }
    public int SmtpPort { get; }
    public Task ListenerFailure => listenerFailure.Task;

    // Opens only IPv4 loopback listeners; the service itself never creates an outbound connection.
    public LabHost(LabConfiguration config, LabErrors errors)
    {
        BindAddress = config.Validate();
        this.errors = errors;
        dns = new(config.Zones, config.Records);
        smtp = new(config.CaptureDirectory);
        dnsTcp = new(BindAddress, config.DnsPort);
        smtpTcp = new(BindAddress, config.SmtpPort);
        UdpClient? openedUdp = null;
        try
        {
            dnsTcp.Start(32);
            DnsPort = ((IPEndPoint)dnsTcp.LocalEndpoint).Port;
            openedUdp = new UdpClient(new IPEndPoint(BindAddress, DnsPort));
            smtpTcp.Start(128);
            SmtpPort = ((IPEndPoint)smtpTcp.LocalEndpoint).Port;
            dnsUdp = openedUdp;
        }
        catch
        {
            openedUdp?.Dispose();
            dnsTcp.Stop();
            smtpTcp.Stop();
            stop.Dispose();
            throw;
        }
        // Accommodate the tested SmarterMail default of 50 outbound workers without capture-side rejection.
        loops = [RunUdpAsync(), AcceptAsync(dnsTcp, HandleDnsTcpAsync, 32, "DNS TCP"), AcceptAsync(smtpTcp, smtp.HandleAsync, 64, "SMTP")];
    }

    // Responds to bounded UDP questions without recursion, forwarding, or external DNS resolution.
    private async Task RunUdpAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult request = await dnsUdp.ReceiveAsync(stop.Token);
                byte[] response = dns.Respond(request.Buffer.Length <= 4096 ? request.Buffer : request.Buffer.AsSpan(0, 12).ToArray(), tcp: false);
                await dnsUdp.SendAsync(response, request.RemoteEndPoint, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { break; }
            catch (SocketException) when (stop.IsCancellationRequested) { break; }
            catch (ObjectDisposedException exception) { ReportListenerFailure("DNS UDP listener", exception); break; }
            catch (Exception exception) { errors.Report("DNS UDP", exception); }
        }
    }

    // Admits a fixed number of simultaneous sessions and tracks them for an orderly local shutdown.
    private async Task AcceptAsync(TcpListener listener, Func<TcpClient, CancellationToken, Task> handler, int maximumClients, string context)
    {
        using var capacity = new SemaphoreSlim(maximumClients);
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { break; }
            catch (SocketException) when (stop.IsCancellationRequested) { break; }
            catch (Exception exception) { ReportListenerFailure(context + " accept", exception); break; }
            if (!capacity.Wait(0)) { client.Dispose(); continue; }
            Task task = RunClientAsync(client, handler, capacity, context);
            lock (clients) clients.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (clients) clients.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        Task[] remaining;
        lock (clients) remaining = clients.ToArray();
        await Task.WhenAll(remaining);
    }

    // Makes loss of a listener observable to the process owner instead of leaving a partially running helper.
    private void ReportListenerFailure(string context, Exception exception)
    {
        listenerFailure.TrySetResult();
        errors.Report(context, exception);
    }

    // Exercises actual accept-loop failure in self-tests without a selectable server-mode fault control.
    internal void StopSmtpListenerForTesting() => smtpTcp.Stop();

    // Applies a one-minute whole-session bound while containing malformed or disconnected peers.
    private async Task RunClientAsync(TcpClient client, Func<TcpClient, CancellationToken, Task> handler, SemaphoreSlim capacity, string context)
    {
        using (client)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            lifetime.CancelAfter(TimeSpan.FromSeconds(60));
            try { await handler(client, lifetime.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception exception) { errors.Report(context + " session", exception); }
            finally { capacity.Release(); }
        }
    }

    // Serves standard length-prefixed DNS requests over one bounded TCP session.
    private async Task HandleDnsTcpAsync(TcpClient client, CancellationToken cancellation)
    {
        NetworkStream stream = client.GetStream();
        byte[] prefix = new byte[2];
        while (true)
        {
            int first = await stream.ReadAsync(prefix.AsMemory(0, 1), cancellation);
            if (first == 0) return;
            await stream.ReadExactlyAsync(prefix.AsMemory(1, 1), cancellation);
            int length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
            if (length > 4096) throw new InvalidDataException("DNS TCP question exceeds the 4096-byte lab bound.");
            byte[] request = new byte[length];
            await stream.ReadExactlyAsync(request, cancellation);
            byte[] response = dns.Respond(request, tcp: true);
            BinaryPrimitives.WriteUInt16BigEndian(prefix, checked((ushort)response.Length));
            await stream.WriteAsync(prefix, cancellation);
            await stream.WriteAsync(response, cancellation);
        }
    }

    // Cancels all listeners and active reads before releasing the local socket resources.
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        dnsTcp.Stop();
        smtpTcp.Stop();
        dnsUdp.Dispose();
        await Task.WhenAll(loops);
        stop.Dispose();
    }
}
