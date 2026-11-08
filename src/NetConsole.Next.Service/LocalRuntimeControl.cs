using System.IO.Pipes;

namespace NetConsole.Next.Service;

// Optional control channel for the Local Manager's own interactive child only.
// SCM instances continue to use Windows service control. No HTTP shutdown API.
internal sealed class LocalRuntimeControl(IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var name = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_LOCAL_CONTROL_PIPE");
        if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService() ||
            string.IsNullOrEmpty(name) || name.Length != 55 ||
            !name.StartsWith("NetConsoleNext-", StringComparison.Ordinal) ||
            !name[15..].All(Uri.IsHexDigit)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                var command = new byte[4];
                await pipe.ReadExactlyAsync(command, requestTimeout.Token);
                if (!command.AsSpan().SequenceEqual("STOP"u8)) continue;
                await pipe.WriteAsync("OK"u8.ToArray(), requestTimeout.Token);
                await pipe.FlushAsync(requestTimeout.Token);
                lifetime.StopApplication();
                return;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
}
