using System.Text;
using DotnetPackageSkills.Cli;

namespace DotnetPackageSkills.Tests;

internal sealed record TerminalWrite(
    int Left,
    int Top,
    string Text,
    TerminalStyle Style,
    int WindowWidth,
    int WindowHeight,
    int OutputCodePage,
    byte[] Bytes);

/// <summary>A cell-addressed screen, scripted keys/resizes, and the styles of individual writes.</summary>
internal sealed class FakeTerminal(int windowHeight = 18, int windowWidth = 100) : ITerminal
{
    private readonly List<List<string?>> _screen = [];
    private readonly List<string> _frames = [];
    private readonly List<TerminalWrite> _writes = [];
    private readonly List<IReadOnlyList<TerminalWrite>> _frameWrites = [];
    private readonly Queue<(ConsoleKeyInfo? Key, Action? BeforeKey)> _keys = new();
    private int _cursorTop;
    private int _cursorLeft;
    private int _frameWriteStart;

    public bool IsRedirected { get; init; }

    public bool SupportsColor { get; init; } = true;

    public int WindowHeight { get; private set; } = windowHeight;

    public int WindowWidth { get; private set; } = windowWidth;

    public int CursorTop => _cursorTop;

    public bool IsCursorVisible { get; private set; } = true;

    public bool IsControlCTakenAsInput { get; private set; }

    public bool ControlCWasEverTakenAsInput { get; private set; }

    public TerminalStyle CurrentStyle { get; private set; }

    public ConsoleColor Foreground { get; set; } = ConsoleColor.Gray;

    public ConsoleColor Background { get; set; } = ConsoleColor.Black;

    public Encoding OutputEncoding { get; set; } = Encoding.ASCII;

    public int ViewportClears { get; private set; }

    public Action<string>? BeforeOperation { get; set; }

    public bool CursorVisible
    {
        set
        {
            BeforeOperation?.Invoke(nameof(CursorVisible));
            IsCursorVisible = value;
        }
    }

    public bool TreatControlCAsInput
    {
        set
        {
            BeforeOperation?.Invoke(nameof(TreatControlCAsInput));
            IsControlCTakenAsInput = value;
            ControlCWasEverTakenAsInput |= value;
        }
    }

    public IReadOnlyList<string> Frames => _frames;

    public IReadOnlyList<TerminalWrite> Writes => _writes;

    public IReadOnlyList<IReadOnlyList<TerminalWrite>> FrameWrites => _frameWrites;

    public List<(int Width, int Height)> FrameSizes { get; } = [];

    public List<int> CursorTopsAwaitingKey { get; } = [];

    public List<TerminalStyle> StyleEvents { get; } = [];

    public List<TimeSpan> InputTimeouts { get; } = [];

    public List<ConsoleKeyInfo> KeysRead { get; } = [];

    public List<Encoding> EncodingChanges { get; } = [];

    public string Screen => string.Join(Environment.NewLine,
        _screen.Take(WindowHeight).Select(row => string.Concat(row.Take(WindowWidth))));

    public int FinalCursorTop => _cursorTop;

    public int CursorTopAwaitingKey { get; private set; }

    public TerminalState CaptureState() => new(
        IsCursorVisible, IsControlCTakenAsInput, Foreground, Background, CurrentStyle, OutputEncoding);

    public void UseUtf8Output()
    {
        BeforeOperation?.Invoke(nameof(UseUtf8Output));
        OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        EncodingChanges.Add(OutputEncoding);
    }

    public void RestoreState(TerminalState state)
    {
        CurrentStyle = state.Style;
        Foreground = state.Foreground ?? ConsoleColor.Gray;
        Background = state.Background ?? ConsoleColor.Black;
        IsCursorVisible = state.CursorVisible;
        IsControlCTakenAsInput = state.TreatControlCAsInput;
        StyleEvents.Add(CurrentStyle);
        OutputEncoding = state.OutputEncoding;
        EncodingChanges.Add(OutputEncoding);
    }

    public void SetStyle(TerminalStyle style)
    {
        BeforeOperation?.Invoke(nameof(SetStyle));
        CurrentStyle = SupportsColor ? style : TerminalStyle.Default;
        if (SupportsColor)
        {
            Foreground = style switch
            {
                TerminalStyle.Focus => ConsoleColor.Blue,
                TerminalStyle.Install => ConsoleColor.Green,
                TerminalStyle.Remove => ConsoleColor.Red,
                TerminalStyle.Muted => ConsoleColor.DarkGray,
                _ => ConsoleColor.Gray,
            };
            Background = ConsoleColor.Black;
        }

        StyleEvents.Add(CurrentStyle);
    }

    public void ResetStyle() => SetStyle(TerminalStyle.Default);

    public FakeTerminal Press(params ConsoleKey[] keys)
    {
        foreach (var key in keys)
        {
            _keys.Enqueue((new ConsoleKeyInfo('\0', key, false, false, false), null));
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

    public FakeTerminal PressWith(ConsoleModifiers modifiers, ConsoleKey key, int times = 1)
    {
        for (var press = 0; press < times; press++)
        {
            _keys.Enqueue((new ConsoleKeyInfo(
                '\0', key,
                shift: (modifiers & ConsoleModifiers.Shift) != 0,
                alt: (modifiers & ConsoleModifiers.Alt) != 0,
                control: (modifiers & ConsoleModifiers.Control) != 0), null));
        }

        return this;
    }

    public FakeTerminal Resize(int windowHeight, int windowWidth) =>
        ResizeBeforeKey(ConsoleKey.NoName, windowHeight, windowWidth);

    public FakeTerminal ResizeBeforeKey(ConsoleKey key, int windowHeight, int windowWidth)
    {
        _keys.Enqueue((new ConsoleKeyInfo('\0', key, false, false, false),
            () => ApplyResize(windowHeight, windowWidth)));
        return this;
    }

    public FakeTerminal ResizeWhileWaiting(int windowHeight, int windowWidth)
    {
        _keys.Enqueue((null, () => ApplyResize(windowHeight, windowWidth)));
        return this;
    }

    public void ResizeNow(int windowHeight, int windowWidth) => ApplyResize(windowHeight, windowWidth);

    public FakeTerminal WaitWithoutKey(int times = 1)
    {
        for (var wait = 0; wait < times; wait++)
        {
            _keys.Enqueue((null, null));
        }

        return this;
    }

    public void SetCursorPosition(int left, int top)
    {
        BeforeOperation?.Invoke(nameof(SetCursorPosition));
        if (left < 0 || left >= WindowWidth || top < 0 || top >= WindowHeight)
        {
            throw new InvalidOperationException($"Cursor ({left}, {top}) is outside {WindowWidth}x{WindowHeight}.");
        }

        _cursorLeft = left;
        _cursorTop = top;
    }

    public void Write(string text)
    {
        BeforeOperation?.Invoke(nameof(Write));
        var bytes = OutputEncoding.GetBytes(text);
        var displayed = OutputEncoding.GetString(bytes);
        _writes.Add(new TerminalWrite(_cursorLeft, _cursorTop, displayed, CurrentStyle,
            WindowWidth, WindowHeight, OutputEncoding.CodePage, bytes));
        EnsureRow();
        var row = _screen[_cursorTop];
        foreach (var element in TerminalText.Elements(displayed))
        {
            if (element.Any(char.IsControl))
            {
                throw new InvalidOperationException("A terminal span contained an unsanitized control.");
            }

            var cells = TerminalText.CellWidth(element);
            if (_cursorLeft + cells > WindowWidth || _cursorTop >= WindowHeight)
            {
                throw new InvalidOperationException($"Write is outside {WindowWidth}x{WindowHeight}: '{text}'.");
            }

            while (row.Count < _cursorLeft + cells)
            {
                row.Add(" ");
            }

            if (cells == 0)
            {
                if (_cursorLeft > 0)
                {
                    var previous = _cursorLeft - 1;
                    while (previous > 0 && row[previous] is null)
                    {
                        previous--;
                    }

                    row[previous] += element;
                }

                continue;
            }

            for (var cell = _cursorLeft; cell < _cursorLeft + cells; cell++)
            {
                var start = cell;
                while (start > 0 && row[start] is null)
                {
                    start--;
                }

                var oldWidth = TerminalText.CellWidth(row[start] ?? " ");
                for (var old = start; old < Math.Min(row.Count, start + oldWidth); old++)
                {
                    row[old] = " ";
                }
            }

            row[_cursorLeft] = element;
            for (var cell = 1; cell < cells; cell++)
            {
                row[_cursorLeft + cell] = null;
            }

            _cursorLeft += cells;
            if (_cursorLeft == WindowWidth)
            {
                AdvanceRow();
                EnsureRow();
                row = _screen[_cursorTop];
            }
        }
    }

    public void WriteLine(string text = "")
    {
        Write(text);
        AdvanceRow();
    }

    public void ClearViewport()
    {
        BeforeOperation?.Invoke(nameof(ClearViewport));
        ViewportClears++;
        _screen.Clear();
        _cursorLeft = 0;
        _cursorTop = 0;
    }

    public bool TryReadKey(TimeSpan timeout, out ConsoleKeyInfo key)
    {
        InputTimeouts.Add(timeout);
        BeforeOperation?.Invoke(nameof(TryReadKey));
        if (_keys.TryPeek(out var next) && next.Key is null)
        {
            CaptureFrame();
            _keys.Dequeue().BeforeKey?.Invoke();
            key = default;
            return false;
        }

        key = ReadKey();
        return true;
    }

    public ConsoleKeyInfo ReadKey()
    {
        CaptureFrame();
        BeforeOperation?.Invoke(nameof(ReadKey));

        if (_keys.Count == 0)
        {
            throw new InvalidOperationException(
                "The picker asked for a key the test did not script. Add one, or end with Enter or Escape.");
        }

        var next = _keys.Dequeue();
        next.BeforeKey?.Invoke();
        var key = next.Key ?? throw new InvalidOperationException("An idle wait requires TryReadKey, not ReadKey.");
        KeysRead.Add(key);
        return key;
    }

    private void CaptureFrame()
    {
        _frames.Add(Screen);
        _frameWrites.Add(_writes.Skip(_frameWriteStart).ToArray());
        _frameWriteStart = _writes.Count;
        FrameSizes.Add((WindowWidth, WindowHeight));
        CursorTopAwaitingKey = _cursorTop;
        CursorTopsAwaitingKey.Add(_cursorTop);
    }

    private void ApplyResize(int height, int width)
    {
        WindowHeight = height;
        WindowWidth = width;
        _cursorTop = Math.Min(_cursorTop, WindowHeight - 1);
        _cursorLeft = Math.Min(_cursorLeft, WindowWidth - 1);
    }

    private void EnsureRow()
    {
        while (_screen.Count <= _cursorTop)
        {
            _screen.Add([]);
        }
    }

    private void AdvanceRow()
    {
        _cursorLeft = 0;
        if (_cursorTop < WindowHeight - 1)
        {
            _cursorTop++;
        }
        else
        {
            _screen.RemoveAt(0);
            _screen.Add([]);
        }
    }
}
