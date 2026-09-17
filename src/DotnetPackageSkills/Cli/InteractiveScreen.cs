using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DotnetPackageSkills.Cli;

/// <summary>Keeps transient picker frames out of the shell's reflowable scrollback.</summary>
internal sealed class InteractiveScreen : IDisposable
{
    private const uint EnableProcessedOutput = 0x0001;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private readonly nint _output;
    private readonly uint? _originalMode;
    private bool _disposed;

    private InteractiveScreen(nint output, uint? originalMode)
    {
        _output = output;
        _originalMode = originalMode;
    }

    public static InteractiveScreen Enter()
    {
        nint output = nint.Zero;
        uint? originalMode = null;
        if (OperatingSystem.IsWindows())
        {
            output = GetStdHandle(-11);
            if (!GetConsoleMode(output, out var mode) ||
                !SetConsoleMode(output, mode | EnableProcessedOutput | EnableVirtualTerminalProcessing))
            {
                throw new PackageSkillsException(
                    "This terminal cannot open an interactive screen. Use a terminal with virtual-terminal support " +
                    "or run the command without --interactive.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            originalMode = mode;
        }

        var screen = new InteractiveScreen(output, originalMode);
        try
        {
            Console.Write("\x1b[?1049h");
            Console.Out.Flush();
            return screen;
        }
        catch
        {
            screen.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Console.Write("\x1b[?1049l");
            Console.Out.Flush();
        }
        finally
        {
            if (_originalMode is { } mode && !SetConsoleMode(_output, mode))
            {
                throw new IOException("Could not restore the terminal output mode.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(nint handle, uint mode);
}
