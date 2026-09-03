namespace DotnetPackageSkills.Cli;

/// <summary>The console operations the interactive picker needs.</summary>
/// <remarks>
/// An interface rather than direct <see cref="Console"/> calls, because tests in this repository
/// run offline and headless. A picker wired straight to the real console could only be exercised
/// by hand, which in practice means it would not be exercised at all.
/// </remarks>
internal interface ITerminal
{
    /// <summary>True when the picker cannot prompt, because input or output is not a terminal.</summary>
    bool IsRedirected { get; }

    int WindowHeight { get; }

    int WindowWidth { get; }

    int CursorTop { get; }

    bool CursorVisible { set; }

    /// <summary>
    /// When true, Ctrl+C arrives through <see cref="ReadKey"/> instead of killing the process,
    /// so the picker can leave the terminal as it found it.
    /// </summary>
    bool TreatControlCAsInput { set; }

    void SetCursorPosition(int left, int top);

    void WriteLine(string text = "");

    ConsoleKeyInfo ReadKey();
}

/// <summary>An <see cref="ITerminal"/> over the real console.</summary>
internal sealed class SystemTerminal : ITerminal
{
    /// <summary>
    /// Used when the console cannot report its size, which happens on headless and redirected
    /// hosts. The picker still needs some width and height to lay a frame out.
    /// </summary>
    private const int FallbackHeight = 24;
    private const int FallbackWidth = 80;

    public bool IsRedirected => Console.IsInputRedirected || Console.IsOutputRedirected;

    public int WindowHeight => Measure(static () => Console.WindowHeight, FallbackHeight);

    public int WindowWidth => Measure(static () => Console.WindowWidth, FallbackWidth);

    public int CursorTop => Measure(static () => Console.CursorTop, 0);

    public bool CursorVisible
    {
        // Hiding the cursor is a courtesy, not a requirement, and the setter throws on hosts
        // that do not support it. Failing the whole install over a cosmetic detail would be absurd.
        set => Ignoring(() => Console.CursorVisible = value);
    }

    public bool TreatControlCAsInput
    {
        // Unsupported on some hosts, and the picker still exits cleanly without it — Ctrl+C
        // just terminates the process instead of being handled as a cancel.
        set => Ignoring(() => Console.TreatControlCAsInput = value);
    }

    public void SetCursorPosition(int left, int top) =>
        Ignoring(() => Console.SetCursorPosition(left, Math.Max(0, top)));

    public void WriteLine(string text = "") => Console.WriteLine(text);

    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);

    private static int Measure(Func<int> read, int fallback)
    {
        try
        {
            var value = read();
            return value > 0 ? value : fallback;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
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
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException)
        {
        }
    }
}
