using System.Net;
using System.Text;
using NetConsole.Next.LocalManager;

internal static class LocalServiceReadinessTests
{
    internal static async Task RunAsync()
    {
        var requestCount = 0;
        using var delayedClient = new HttpClient(new SequenceHandler(() => Interlocked.Increment(ref requestCount)));
        await LocalServiceReadiness.WaitForHealthyAsync(delayedClient, 18771, TimeSpan.FromSeconds(2));
        Check(requestCount >= 2, "SCM startup must wait through a transient non-ready response");

        using var unhealthyClient = new HttpClient(new FixedHandler("{\"serviceState\":\"running\",\"healthState\":\"DEGRADED\",\"processId\":12}"));
        var failure = await CaptureFailure(() => LocalServiceReadiness.WaitForHealthyAsync(unhealthyClient, 18772, TimeSpan.FromMilliseconds(80)));
        Check(failure.Message.Contains("SERVICE_NOT_READY", StringComparison.Ordinal) &&
            failure.Message.Contains("healthState=DEGRADED", StringComparison.Ordinal),
            "SCM running state must not be accepted when application health remains degraded");

        using var invalidClient = new HttpClient(new FixedHandler(new string('x', 65_537)));
        var oversized = await CaptureFailure(() => LocalServiceReadiness.WaitForHealthyAsync(invalidClient, 18773, TimeSpan.FromSeconds(1)));
        Check(oversized is InvalidDataException, "oversized health response must fail closed");
        foreach (var processId in new[] { "\"42\"", "true", "null", "[]", "{}", "2147483648", "1.5", "0", "-1" })
        {
            using var invalidPidClient = new HttpClient(new FixedHandler(
                "{\"serviceState\":\"running\",\"healthState\":\"HEALTHY\",\"processId\":" + processId + "}"));
            var invalidPid = await CaptureFailure(() => LocalServiceReadiness.WaitForHealthyAsync(
                invalidPidClient, 18774, TimeSpan.FromMilliseconds(30)));
            Check(invalidPid.Message.Contains("SERVICE_NOT_READY", StringComparison.Ordinal) &&
                invalidPid.Message.Contains("processId=missing", StringComparison.Ordinal),
                "invalid process IDs must produce the bounded readiness diagnosis instead of a JSON type exception");
        }
        Console.WriteLine("LOCAL_SERVICE_READINESS_TESTS=4/4 PASS: startup retry, degraded health timeout, bounded health payload, invalid process ID diagnosis");
    }

    private static async Task<Exception> CaptureFailure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { return ex; }
        throw new Exception("Expected service readiness failure.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class SequenceHandler(Func<int> count) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.RequestUri is { Host: "127.0.0.1", Port: 18771, AbsolutePath: "/api/system/health" },
                "readiness probe must use the configured loopback endpoint");
            var response = count() == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    "{\"serviceState\":\"running\",\"healthState\":\"HEALTHY\",\"processId\":42}", Encoding.UTF8, "application/json") };
            return Task.FromResult(response);
        }
    }

    private sealed class FixedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host != "127.0.0.1") throw new Exception("Readiness probe left loopback.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
