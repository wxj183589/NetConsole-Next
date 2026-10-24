using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed class DesktopHelperIpc
{
    public const string PipeName = "NetConsoleNext.DesktopHelper";
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<DesktopHelperResponse> SendAsync(object request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return new("HOST_DESKTOP_UNAVAILABLE", "Windows interactive host is required.", null);
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var requestId = Guid.NewGuid().ToString("N");
            var payload = JsonSerializer.Serialize(new { requestId, request }, _json);
            await writer.WriteLineAsync(payload);
            var response = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(response)) return new("HELPER_PROTOCOL_ERROR", "Desktop Helper returned no response.", requestId);
            return JsonSerializer.Deserialize<DesktopHelperResponse>(response, _json)
                ?? new("HELPER_PROTOCOL_ERROR", "Desktop Helper response was invalid.", requestId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new("HOST_DESKTOP_UNAVAILABLE", "Desktop Helper is not running in the host interactive session.", null);
        }
        catch (IOException)
        {
            return new("HOST_DESKTOP_UNAVAILABLE", "Desktop Helper pipe is unavailable.", null);
        }
        catch (UnauthorizedAccessException)
        {
            return new("PERMISSION_DENIED", "Desktop Helper pipe access was denied.", null);
        }
    }
}
