using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DotnetPackageSkills.Cli;

internal static class ConsoleViewport
{
    public static (int Width, int Height) Size()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (Console.WindowWidth, Console.WindowHeight);
        }

        var buffer = ReadBuffer();
        return (buffer.Window.Right - buffer.Window.Left + 1, buffer.Window.Bottom - buffer.Window.Top + 1);
    }

    public static void SetCursorPosition(int left, int top)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.SetCursorPosition(left, top);
            return;
        }

        var buffer = ReadBuffer();
        var position = new Coordinate
        {
            X = (short)Math.Clamp(buffer.Window.Left + left, buffer.Window.Left, buffer.Window.Right),
            Y = (short)Math.Clamp(buffer.Window.Top + top, buffer.Window.Top, buffer.Window.Bottom),
        };
        if (!SetConsoleCursorPosition(GetStdHandle(-11), position))
        {
            throw ConsoleError("Could not position the interactive terminal cursor.");
        }
    }

    public static void Clear()
    {
        if (OperatingSystem.IsWindows())
        {
            ClearWindows();
        }
        else
        {
            Console.Write("\x1b[2J\x1b[H");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ClearWindows()
    {
        var output = GetStdHandle(-11);
        var buffer = ReadBuffer();

        // Clear cells in place. Printing blank lines instead pushes stale picker frames
        // into scrollback, where the terminal can rewrap them independently after a resize.
        var width = (uint)(buffer.Window.Right - buffer.Window.Left + 1);
        for (var row = (int)buffer.Window.Top; row <= buffer.Window.Bottom; row++)
        {
            var position = new Coordinate { X = buffer.Window.Left, Y = (short)row };
            if (!FillConsoleOutputCharacter(output, ' ', width, position, out _) ||
                !FillConsoleOutputAttribute(output, buffer.Attributes, width, position, out _))
            {
                throw ConsoleError("Could not clear the interactive terminal viewport.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static ScreenBufferInfo ReadBuffer()
    {
        if (!GetConsoleScreenBufferInfo(GetStdHandle(-11), out var buffer))
        {
            throw ConsoleError("Could not read the interactive terminal viewport.");
        }

        return buffer;
    }

    private static IOException ConsoleError(string message) =>
        new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Sequential)]
    private struct Coordinate
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRectangle
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenBufferInfo
    {
        public Coordinate Size;
        public Coordinate Cursor;
        public ushort Attributes;
        public WindowRectangle Window;
        public Coordinate MaximumWindowSize;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleScreenBufferInfo(nint output, out ScreenBufferInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCursorPosition(nint output, Coordinate position);

    [DllImport("kernel32.dll", EntryPoint = "FillConsoleOutputCharacterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FillConsoleOutputCharacter(
        nint output, char character, uint length, Coordinate position, out uint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FillConsoleOutputAttribute(
        nint output, ushort attributes, uint length, Coordinate position, out uint written);
}
