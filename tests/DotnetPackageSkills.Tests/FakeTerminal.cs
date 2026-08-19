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

    public bool CursorVisible
    {
        set => IsCursorVisible = value;
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

        return _keys.Count > 0
            ? _keys.Dequeue()
            : throw new InvalidOperationException(
                "The picker asked for a key the test did not script. Add one, or end with Enter or Escape.");
    }
}
