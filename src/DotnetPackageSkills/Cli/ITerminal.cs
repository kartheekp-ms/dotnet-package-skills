using System.Diagnostics;
using System.Text;

namespace DotnetPackageSkills.Cli;

internal enum TerminalStyle
{
    Default,
    Focus,
    Selected,
    Remove,
    Muted,
}

internal readonly record struct TerminalState(
    bool CursorVisible,
    bool TreatControlCAsInput,
    ConsoleColor? Foreground,
    ConsoleColor? Background,
    TerminalStyle Style,
    Encoding OutputEncoding);

/// <summary>The console operations the interactive picker needs, in viewport coordinates.</summary>
internal interface ITerminal
{
    bool IsRedirected { get; }

    bool SupportsColor { get; }

    int WindowHeight { get; }

    int WindowWidth { get; }

    (int Width, int Height) GetWindowSize() => (WindowWidth, WindowHeight);

    int CursorTop { get; }

    bool CursorVisible { set; }

    bool TreatControlCAsInput { set; }

    TerminalState CaptureState();

    void RestoreState(TerminalState state);

    /// <summary>Uses lossless Unicode output for this interaction; RestoreState restores the encoding.</summary>
    void UseUtf8Output();

    IDisposable EnterInteractiveScreen();

    void SetStyle(TerminalStyle style);

    void ResetStyle();

    void SetCursorPosition(int left, int top);

    void Write(string text);

    void WriteLine(string text = "");

    /// <summary>Starts a fresh viewport after a resize, without discarding scrollback.</summary>
    void ClearViewport();

    /// <summary>Waits at most the timeout; false lets the picker observe an idle resize.</summary>
    bool TryReadKey(TimeSpan timeout, out ConsoleKeyInfo key);

    ConsoleKeyInfo ReadKey();
}

/// <summary>An <see cref="ITerminal"/> over the real console.</summary>
internal sealed class SystemTerminal : ITerminal
{
    private const int FallbackHeight = 24;
    private const int FallbackWidth = 80;
    private TerminalStyle _style;
    private TerminalStyle? _appliedStyle;

    public bool IsRedirected => Console.IsInputRedirected || Console.IsOutputRedirected;

    public bool SupportsColor => CanUseColor(
        IsRedirected,
        Environment.GetEnvironmentVariable("NO_COLOR"),
        Environment.GetEnvironmentVariable("TERM"),
        OperatingSystem.IsWindows());

    public int WindowHeight => GetWindowSize().Height;

    public int WindowWidth => GetWindowSize().Width;

    public (int Width, int Height) GetWindowSize()
    {
        var (width, height) = Read(ConsoleViewport.Size, (FallbackWidth, FallbackHeight));
        return (width > 0 ? width : FallbackWidth, height > 0 ? height : FallbackHeight);
    }

    public int CursorTop => Math.Clamp(
        Read(static () => Console.CursorTop, 0) - Read(static () => Console.WindowTop, 0),
        0,
        WindowHeight - 1);

    public bool CursorVisible
    {
        set => Ignoring(() => Console.CursorVisible = value);
    }

    public bool TreatControlCAsInput
    {
        set => Ignoring(() => Console.TreatControlCAsInput = value);
    }

    public TerminalState CaptureState() => new(
        Read(static () => OperatingSystem.IsWindows() ? Console.CursorVisible : true, true),
        Read(static () => Console.TreatControlCAsInput, false),
        ReadColor(static () => Console.ForegroundColor),
        ReadColor(static () => Console.BackgroundColor),
        _style,
        Console.OutputEncoding);

    public void UseUtf8Output() => Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public IDisposable EnterInteractiveScreen() => InteractiveScreen.Enter();

    public void RestoreState(TerminalState state)
    {
        try
        {
            ResetStyle();
            if (SupportsColor)
            {
                if (state.Foreground is { } foreground)
                {
                    Ignoring(() => Console.ForegroundColor = foreground);
                }

                if (state.Background is { } background)
                {
                    Ignoring(() => Console.BackgroundColor = background);
                }
            }

            _style = state.Style;
            _appliedStyle = null;
        }
        finally
        {
            try
            {
                CursorVisible = state.CursorVisible;
            }
            finally
            {
                try
                {
                    TreatControlCAsInput = state.TreatControlCAsInput;
                }
                finally
                {
                    Console.OutputEncoding = state.OutputEncoding;
                }
            }
        }
    }

    public void SetStyle(TerminalStyle style)
    {
        var supportsColor = SupportsColor;
        _style = supportsColor ? style : TerminalStyle.Default;
        if (!supportsColor || _appliedStyle == style)
        {
            return;
        }

        Ignoring(Console.ResetColor);
        var color = style switch
        {
            TerminalStyle.Focus => ConsoleColor.Blue,
            TerminalStyle.Selected => ConsoleColor.Blue,
            TerminalStyle.Remove => ConsoleColor.Red,
            TerminalStyle.Muted => ConsoleColor.DarkGray,
            _ => (ConsoleColor?)null,
        };

        if (color is { } foreground)
        {
            Ignoring(() => Console.ForegroundColor = foreground);
        }

        _appliedStyle = style;
    }

    public void ResetStyle() => SetStyle(TerminalStyle.Default);

    public void SetCursorPosition(int left, int top) => ConsoleViewport.SetCursorPosition(left, top);

    public void Write(string text) => Console.Write(text);

    public void WriteLine(string text = "") => Console.WriteLine(text);

    public void ClearViewport()
    {
        ConsoleViewport.Clear();
        SetCursorPosition(0, 0);
    }

    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);

    public bool TryReadKey(TimeSpan timeout, out ConsoleKeyInfo key)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (Console.KeyAvailable)
            {
                key = ReadKey();
                return true;
            }

            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                key = default;
                return false;
            }

            // Keep key latency low without spinning, including the final fractional millisecond.
            Thread.Sleep((int)Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 1, 25));
        }
    }

    internal static bool CanUseColor(bool redirected, string? noColor, string? term, bool windows)
    {
        if (redirected || noColor is not null)
        {
            return false;
        }

        var capability = term?.ToLowerInvariant();
        if (capability is "dumb" or "unknown" or "vt100" or "vt102" or "vt220")
        {
            return false;
        }

        return windows ||
               capability is "linux" or "ansi" or "cygwin" ||
               capability is not null &&
               (capability.Contains("color", StringComparison.Ordinal) ||
                capability.StartsWith("xterm", StringComparison.Ordinal) ||
                capability.StartsWith("screen", StringComparison.Ordinal) ||
                capability.StartsWith("tmux", StringComparison.Ordinal) ||
                capability.StartsWith("rxvt", StringComparison.Ordinal));
    }

    private static ConsoleColor? ReadColor(Func<ConsoleColor> read)
    {
        var color = Read(read, (ConsoleColor)(-1));
        return (int)color is >= 0 and <= 15 ? color : null;
    }

    private static T Read<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or InvalidOperationException)
        {
            return fallback;
        }
    }

    private static void Ignoring(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or
                                       ArgumentOutOfRangeException or InvalidOperationException)
        {
        }
    }
}
