namespace DotnetPackageSkills.Cli;

internal enum PickerMode
{
    Install,
    Uninstall,
}

/// <summary>Picker metadata supplied by the caller, never loaded by the UI.</summary>
internal sealed record SkillPickerItem(
    string Name,
    string Package,
    string Version,
    string? Description = null,
    string? DescriptionWarning = null);

/// <summary>
/// A paged checklist. Nothing starts ticked: a tick means install in install mode and remove in
/// uninstall mode, so accepting without a choice changes nothing either way.
/// </summary>
internal sealed class SkillPicker(ITerminal terminal)
{
    private static readonly TimeSpan InputPollInterval = TimeSpan.FromMilliseconds(100);

    /// <param name="note">An optional line shown under the title, such as what the list leaves out.</param>
    public IReadOnlySet<string>? Choose(
        IReadOnlyList<SkillPickerItem> items,
        string title,
        PickerMode mode = PickerMode.Install,
        string? note = null)
    {
        if (items.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        if (terminal.IsRedirected)
        {
            throw new PackageSkillsException(
                mode == PickerMode.Install
                    ? "--interactive needs a terminal, but input or output is redirected. " +
                      "Drop --interactive to install every discovered skill, or name the ones you want " +
                      "with --package."
                    : "--interactive needs a terminal, but input or output is redirected. " +
                      "Drop --interactive to remove every skill that the command matches, and add " +
                      "--dry-run to see which ones first.");
        }

        var selected = new HashSet<int>();
        var layout = Measure();
        var cursor = 0;
        int? pageOffset = null;
        var scroll = new int[items.Count];
        var frameTop = 0;
        var height = 0;
        var frameStarted = false;
        var resetViewport = true;
        var original = terminal.CaptureState();
        IDisposable? screen = null;

        try
        {
            terminal.UseUtf8Output();
            screen = terminal.EnterInteractiveScreen();
            terminal.CursorVisible = false;
            terminal.TreatControlCAsInput = true;
            terminal.ResetStyle();
            frameStarted = true;

            while (true)
            {
                Reflow();
                DrawFrame();
                ConsoleKeyInfo key;
                while (!terminal.TryReadKey(InputPollInterval, out key))
                {
                    if (Reflow())
                    {
                        DrawFrame();
                    }
                }

                // A key can arrive after a resize. Page keys must use the new boundaries,
                // and accept/cancel must not leave the old, differently sized frame behind.
                if (Reflow())
                {
                    DrawFrame();
                }

                if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    if (key.Key == ConsoleKey.C)
                    {
                        return null;
                    }

                    if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
                    {
                        scroll[cursor] = Math.Clamp(
                            scroll[cursor] + (key.Key == ConsoleKey.UpArrow ? -1 : 1),
                            0,
                            layout.MaxScroll(cursor));
                        continue;
                    }
                }

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        cursor = (cursor - 1 + items.Count) % items.Count;
                        pageOffset = null;
                        break;
                    case ConsoleKey.DownArrow:
                        cursor = (cursor + 1) % items.Count;
                        pageOffset = null;
                        break;
                    case ConsoleKey.LeftArrow or ConsoleKey.PageUp:
                        MovePage(-1);
                        break;
                    case ConsoleKey.RightArrow or ConsoleKey.PageDown:
                        MovePage(1);
                        break;
                    case ConsoleKey.Home:
                        cursor = 0;
                        pageOffset = null;
                        break;
                    case ConsoleKey.End:
                        cursor = items.Count - 1;
                        pageOffset = null;
                        break;
                    case ConsoleKey.Spacebar:
                        if (!selected.Add(cursor))
                        {
                            selected.Remove(cursor);
                        }

                        break;
                    case ConsoleKey.A:
                        selected.UnionWith(Enumerable.Range(0, items.Count));
                        break;
                    case ConsoleKey.C:
                        selected.Clear();
                        break;
                    case ConsoleKey.Enter:
                        return items.Where((_, index) => selected.Contains(index))
                            .Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    case ConsoleKey.Escape or ConsoleKey.Q:
                        return null;
                }
            }
        }
        finally
        {
            try
            {
                try
                {
                    terminal.ResetStyle();
                }
                finally
                {
                    if (frameStarted)
                    {
                        var bottom = Math.Clamp(frameTop + height, 0, terminal.WindowHeight - 1);
                        terminal.SetCursorPosition(0, bottom);
                        if (bottom < terminal.WindowHeight - 1)
                        {
                            terminal.WriteLine();
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    screen?.Dispose();
                }
                finally
                {
                    terminal.RestoreState(original);
                }
            }
        }

        PickerLayout Measure()
        {
            var size = terminal.GetWindowSize();
            return PickerLayout.For(items, title, mode, size.Width, size.Height, terminal.SupportsColor, note);
        }

        void DrawFrame()
        {
            while (true)
            {
                Reflow();
                var previousHeight = height;
                try
                {
                    if (resetViewport)
                    {
                        terminal.ResetStyle();
                        terminal.ClearViewport();
                        frameTop = 0;
                        height = 0;
                        resetViewport = false;
                    }

                    Render(items, selected, cursor, scroll, layout, frameTop, mode, ref height);
                    EnsureViewport(layout);
                    return;
                }
                catch (Exception ex) when (ex is ViewportChangedException ||
                    (ex is IOException or ArgumentOutOfRangeException or InvalidOperationException) && ViewportChanged(layout))
                {
                    height = Math.Max(previousHeight, height);
                    resetViewport = true;
                }
                catch
                {
                    // A failed redraw can leave the lower part of the previous frame intact.
                    height = Math.Max(previousHeight, height);
                    throw;
                }
            }
        }

        bool Reflow()
        {
            if (!ViewportChanged(layout))
            {
                return false;
            }

            layout = Measure();
            resetViewport = true;
            pageOffset = null;
            for (var item = 0; item < scroll.Length; item++)
            {
                scroll[item] = Math.Min(scroll[item], layout.MaxScroll(item));
            }

            return true;
        }

        void MovePage(int direction)
        {
            var page = layout.PageIndexFor(cursor);
            var target = Math.Clamp(page + direction, 0, layout.Pages.Count - 1);
            if (target == page)
            {
                return;
            }

            pageOffset ??= cursor - layout.Pages[page].First;
            var next = layout.Pages[target];
            cursor = next.First + Math.Min(pageOffset.Value, next.Count - 1);
        }
    }

    private void Render(
        IReadOnlyList<SkillPickerItem> items,
        HashSet<int> selected,
        int cursor,
        int[] scroll,
        PickerLayout layout,
        int frameTop,
        PickerMode mode,
        ref int height)
    {
        var previousHeight = height;
        height = 0;
        var pageIndex = layout.PageIndexFor(cursor);
        var page = layout.Pages[pageIndex];
        foreach (var line in layout.Header(pageIndex))
        {
            WriteRow(layout, frameTop, ref height, new Span(line));
        }

        WriteRow(layout, frameTop, ref height);
        for (var index = page.First; index < page.First + page.Count; index++)
        {
            var entry = layout.Entries[index];
            var isSelected = selected.Contains(index);
            var rowStyle = index == cursor ? TerminalStyle.Focus : TerminalStyle.Default;
            var offset = page.Scrollable ? scroll[index] : 0;
            var rows = page.Scrollable ? page.VisibleRows : entry.Height;
            // Continuations are wider than the space after the name, so scroll them below
            // the fixed skill row rather than placing one into its narrower first-line slot.
            WriteRow(
                layout, frameTop, ref height,
                new Span(index == cursor ? ">" : " ", rowStyle),
                new Span(" ", rowStyle),
                new Span("[", rowStyle),
                new Span(isSelected ? "X" : " ", isSelected ? TerminalStyle.Selected : rowStyle),
                new Span("]", rowStyle),
                new Span($" {entry.Label}", rowStyle),
                new Span($" - {entry.Description[0]}", rowStyle));
            for (var line = 1; line < rows; line++)
            {
                WriteRow(layout, frameTop, ref height,
                    new Span(new string(' ', layout.ContinuationColumn) + entry.Description[offset + line], rowStyle));
            }
        }

        WriteRow(layout, frameTop, ref height);
        foreach (var line in TerminalText.Wrap(
                     PickerLayout.Summary(selected.Count, items.Count, mode), layout.Width))
        {
            WriteRow(layout, frameTop, ref height, new Span(line));
        }

        foreach (var line in layout.Help)
        {
            WriteRow(layout, frameTop, ref height, new Span(line, TerminalStyle.Muted));
        }

        if (page.Scrollable)
        {
            foreach (var line in TerminalText.Wrap(
                         PickerLayout.ScrollHelp(scroll[cursor] + 2, scroll[cursor] + page.VisibleRows,
                             layout.Entries[cursor].Height),
                         layout.Width))
            {
                WriteRow(layout, frameTop, ref height, new Span(line, TerminalStyle.Muted));
            }
        }

        // Erase old content, but park at the actual footer, not at the end of the erased
        // rectangle. A short final page should not strand the eventual shell prompt.
        var erased = height;
        while (erased < previousHeight)
        {
            WriteRow(layout, frameTop, ref erased);
        }

        terminal.SetCursorPosition(0, frameTop + height);
    }

    private void WriteRow(PickerLayout layout, int frameTop, ref int height, params Span[] spans)
    {
        EnsureViewport(layout);
        var row = height++;
        terminal.SetCursorPosition(0, frameTop + row);
        var cells = 0;
        foreach (var span in spans)
        {
            terminal.SetStyle(layout.SupportsColor ? span.Style : TerminalStyle.Default);
            EnsureViewport(layout);
            terminal.Write(span.Text);
            EnsureViewport(layout);
            cells += TerminalText.Width(span.Text);
        }

        terminal.ResetStyle();
        EnsureViewport(layout);
        terminal.Write(new string(' ', layout.Width - cells));
        EnsureViewport(layout);
    }

    private bool ViewportChanged(PickerLayout layout)
    {
        var size = terminal.GetWindowSize();
        return layout.WindowWidth != size.Width || layout.WindowHeight != size.Height ||
               layout.SupportsColor != terminal.SupportsColor;
    }

    private void EnsureViewport(PickerLayout layout)
    {
        if (ViewportChanged(layout))
        {
            throw new ViewportChangedException();
        }
    }

    private readonly record struct Span(string Text, TerminalStyle Style = TerminalStyle.Default);

    private sealed class ViewportChangedException : Exception;
}
