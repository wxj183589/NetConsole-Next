using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NetConsole.Next.LocalManager;

internal static class DetachedWindowsProcess
{
    // Process.Start inherits the manager's redirected stdout/stderr handles on
    // Windows, keeping PowerShell pipelines alive after the manager exits. This
    // child has no console handles; service diagnostics remain in rolling logs.
    internal static Process Start(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Portable apphost management requires Windows.");
        var environment = string.Join('\0', start.Environment.Where(pair => pair.Value is not null)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)) + "\0";
        var environmentPointer = Marshal.StringToHGlobalUni(environment);
        try
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x100 }; // STARTF_USESTDHANDLES, all handles null
            if (!CreateProcessW(start.FileName, new StringBuilder('"' + start.FileName + '"'), IntPtr.Zero, IntPtr.Zero,
                    false, 0x08000400, environmentPointer, start.WorkingDirectory, ref startup, out var processInfo))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not launch the detached service apphost.");
            try { return Process.GetProcessById(unchecked((int)processInfo.ProcessId)); }
            finally
            {
                CloseHandle(processInfo.Thread);
                CloseHandle(processInfo.Process);
            }
        }
        finally { Marshal.FreeHGlobal(environmentPointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short ReservedBytes;
        public IntPtr ReservedPointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
