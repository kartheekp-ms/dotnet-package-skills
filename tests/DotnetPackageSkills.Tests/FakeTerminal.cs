using DotnetPackageSkills.Cli;

namespace DotnetPackageSkills.Tests;

/// <summary>
/// A terminal driven by a scripted key sequence, with a screen buffer the tests can read back.
/// </summary>
/// <remarks>
/// The buffer is modelled rather than the output concatenated, because the picker redraws in
/// place: appending every write would show frames layered on top of each other instead of the
/// one page a user actually sees.
/// </remarks>
internal sealed class FakeTerminal(int windowHeight = 24, int windowWidth = 100) : ITerminal
{
    private readonly List<string> _screen = [];
    private readonly List<string> _frames = [];
    private readonly Queue<ConsoleKeyInfo> _keys = new();
    private int _cursorTop;

    public bool IsRedirected { get; init; }

    public int WindowHeight { get; } = windowHeight;

    public int WindowWidth { get; } = windowWidth;

    public int CursorTop => _cursorTop;

    /// <summary>Exposed so a test can prove the picker puts the cursor back when it leaves.</summary>
    public bool IsCursorVisible { get; private set; } = true;

    /// <summary>Exposed so a test can prove Ctrl+C handling is turned off again on the way out.</summary>
    public bool IsControlCTakenAsInput { get; private set; }

    /// <summary>
    /// True if Ctrl+C was ever captured. The flag above is false by the time a test looks at
    /// it, because the picker restores it, so the transition has to be recorded separately.
    /// </summary>
    public bool ControlCWasEverTakenAsInput { get; private set; }

    public bool CursorVisible
    {
        set => IsCursorVisible = value;
    }

    public bool TreatControlCAsInput
    {
        set
        {
            IsControlCTakenAsInput = value;
            ControlCWasEverTakenAsInput |= value;
        }
    }

    /// <summary>What was on screen each time the picker asked for a key, oldest first.</summary>
    public IReadOnlyList<string> Frames => _frames;

    /// <summary>What is on screen now.</summary>
    public string Screen => string.Join(Environment.NewLine, _screen);

    public FakeTerminal Press(params ConsoleKey[] keys)
    {
        foreach (var key in keys)
        {
            _keys.Enqueue(new ConsoleKeyInfo('\0', key, false, false, false));
        }

        return this;
    }

    public FakeTerminal Press(ConsoleKey key, int times)
    {
        for (var press = 0; press < times; press++)
        {
            Press(key);
        }

        return this;
    }

    /// <summary>Queues a key held with a modifier, such as Ctrl+C.</summary>
    public FakeTerminal PressWith(ConsoleModifiers modifiers, ConsoleKey key)
    {
        _keys.Enqueue(new ConsoleKeyInfo(
            '\0',
            key,
            shift: (modifiers & ConsoleModifiers.Shift) != 0,
            alt: (modifiers & ConsoleModifiers.Alt) != 0,
            control: (modifiers & ConsoleModifiers.Control) != 0));

        return this;
    }

    /// <summary>Row the cursor was left on, which is where a shell prompt would land.</summary>
    public int FinalCursorTop => _cursorTop;

    public void SetCursorPosition(int left, int top) => _cursorTop = top;

    public void WriteLine(string text = "")
    {
        while (_screen.Count <= _cursorTop)
        {
            _screen.Add(string.Empty);
        }

        _screen[_cursorTop] = text;
        _cursorTop++;
    }

    public ConsoleKeyInfo ReadKey()
    {
        _frames.Add(Screen);
        CursorTopAwaitingKey = _cursorTop;

        return _keys.Count > 0
            ? _keys.Dequeue()
            : throw new InvalidOperationException(
                "The picker asked for a key the test did not script. Add one, or end with Enter or Escape.");
    }

    /// <summary>
    /// Row the cursor sat on while the picker waited for a key. This is where an abrupt exit
    /// leaves the shell prompt, so it is the only way to test what Ctrl+C looks like without
    /// actually killing the test host.
    /// </summary>
    public int CursorTopAwaitingKey { get; private set; }
}
