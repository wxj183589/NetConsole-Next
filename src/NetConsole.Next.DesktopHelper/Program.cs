using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

const string pipeName = "NetConsoleNext.DesktopHelper";
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("NetConsole Next Desktop Helper requires Windows.");
    return 2;
}

Console.WriteLine("NetConsole Next Desktop Helper listening for the local service.");
while (true)
{
    await using var pipe = CreateServer(pipeName);
    await pipe.WaitForConnectionAsync();
    try
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var line = await reader.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(line)) continue;
        var envelope = JsonSerializer.Deserialize<HelperEnvelope>(line, json);
        var result = envelope is null ? new DesktopHelperResponse("HELPER_PROTOCOL_ERROR", "Invalid request.", null) : Handle(envelope);
        await writer.WriteLineAsync(JsonSerializer.Serialize(result, json));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
    {
        try { await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true }; await writer.WriteLineAsync(JsonSerializer.Serialize(new DesktopHelperResponse("HELPER_PROTOCOL_ERROR", ex.GetType().Name, null), json)); } catch { }
    }
}

static NamedPipeServerStream CreateServer(string pipeName)
{
    var security = new PipeSecurity();
    Add(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
    try { Add(security, new NTAccount("NT SERVICE", "NetConsoleNext").Translate(typeof(SecurityIdentifier)) as SecurityIdentifier); } catch { }
    return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16 * 1024, 16 * 1024, security);

    static void Add(PipeSecurity security, SecurityIdentifier? sid)
    {
        if (sid is not null) security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
    }
}

static DesktopHelperResponse Handle(HelperEnvelope envelope)
{
    var requestId = envelope.RequestId;
    if (envelope.Request.ValueKind != JsonValueKind.Object) return new("HELPER_PROTOCOL_ERROR", "Request body is not an object.", requestId);
    var operation = GetString(envelope.Request, "operation");
    if (operation == "OPEN_DIRECTORY")
    {
        var path = GetString(envelope.Request, "directoryPath");
        if (!IsSafeExistingDirectory(path)) return new("INVALID_ARGUMENTS", "Directory is missing or unsafe.", requestId);
        return Start("explorer.exe", [path], requestId);
    }
    if (operation != "OPEN_EXTERNAL_TERMINAL") return new("INVALID_ARGUMENTS", "Unsupported helper operation.", requestId);
    var toolId = GetString(envelope.Request, "toolId").ToLowerInvariant();
    if (toolId is not ("securecrt" or "xshell" or "putty")) return new("INVALID_ARGUMENTS", "Tool is not allowlisted.", requestId);
    var executable = GetString(envelope.Request, "executablePath");
    var fileName = Path.GetFileName(executable).ToLowerInvariant();
    if ((toolId == "securecrt" && fileName != "securecrt.exe") ||
        (toolId == "xshell" && fileName != "xshell.exe") ||
        (toolId == "putty" && fileName != "putty.exe"))
        return new("INVALID_ARGUMENTS", "Executable does not match the registered tool.", requestId);
    if (!IsSafeExistingFile(executable)) return new("EXECUTABLE_NOT_FOUND", "Executable is missing or unsafe.", requestId);
    if (!envelope.Request.TryGetProperty("arguments", out var argumentElement) || argumentElement.ValueKind != JsonValueKind.Array)
        return new("INVALID_ARGUMENTS", "Arguments are required.", requestId);
    var args = argumentElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
    if (args.Length is 0 or > 12 || args.Any(HasUnsafeArgument)) return new("INVALID_ARGUMENTS", "Argument policy rejected the request.", requestId);
    return Start(executable, args, requestId);
}

static DesktopHelperResponse Start(string fileName, IEnumerable<string> arguments, string requestId)
{
    try
    {
        var info = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        Process.Start(info);
        return new("LAUNCHED", null, requestId);
    }
    catch (UnauthorizedAccessException) { return new("PERMISSION_DENIED", "Windows denied process launch.", requestId); }
    catch (Win32Exception) { return new("START_FAILED", "Windows could not start the host application.", requestId); }
}

static bool IsSafeExistingFile(string path) => Path.IsPathRooted(path) && File.Exists(path) && !IsReparse(path) && !path.Any(char.IsControl);
static bool IsSafeExistingDirectory(string path) => Path.IsPathRooted(path) && Directory.Exists(path) && !IsReparse(path) && !path.Any(char.IsControl);
static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
static bool HasUnsafeArgument(string value) => value.Length > 512 || value.Any(char.IsControl) || value.Any(c => c is '"' or '&' or '|' or '<' or '>');
static string GetString(JsonElement body, string name) => body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
file sealed record HelperEnvelope(string RequestId, JsonElement Request);
file sealed record DesktopHelperResponse(string Status, string? Detail, string? RequestId);
