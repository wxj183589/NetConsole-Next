using System.Diagnostics;

namespace NetConsole.Next.Service;

/// <summary>A shared process limit, with no connection pooling or reuse.</summary>
internal static class NetmikoWorkerBudget
{
    internal const int Limit = 16;
    private static readonly SemaphoreSlim Slots = new(Limit, Limit);
    internal static int AvailableSlots => Slots.CurrentCount;

    internal static async Task<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        await Slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease();
    }

    internal sealed class Lease : IDisposable
    {
        private int _released;

        // After a successful start the exit observer owns this lease and the
        // Process. Even a failed Kill must not admit another live worker.
        internal (Process Process, StreamWriter Input, StreamReader Output, StreamReader Error) Start(ProcessStartInfo start)
        {
            Process? process = null;
            StreamWriter? input = null;
            StreamReader? output = null;
            StreamReader? error = null;
            try
            {
                process = Process.Start(start) ?? throw new IOException("Netmiko worker could not start.");
                // Cache pipes before an immediately exited process can be disposed.
                input = process.StandardInput;
                output = process.StandardOutput;
                error = process.StandardError;
                _ = ReleaseAfterExitAsync(process);
                return (process, input, output, error);
            }
            catch
            {
                if (process is null) Dispose();
                else
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    try { input?.Dispose(); } catch { }
                    try { output?.Dispose(); } catch { }
                    try { error?.Dispose(); } catch { }
                    _ = ReleaseAfterExitAsync(process);
                }
                throw;
            }
        }

        private async Task ReleaseAfterExitAsync(Process process)
        {
            try { await process.WaitForExitAsync().ConfigureAwait(false); }
            catch { return; } // An unconfirmed exit must continue to consume its slot.
            Dispose();
            process.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) Slots.Release();
        }
    }
}
