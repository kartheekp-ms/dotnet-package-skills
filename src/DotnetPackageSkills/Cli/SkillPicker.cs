using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Cli;

/// <summary>One row in the picker: a discovered skill and whether it is already installed.</summary>
internal sealed record SkillPickerItem(BundledSkill Skill, bool Installed);

/// <summary>
/// Lets the user choose which discovered skills to install, a page at a time.
/// </summary>
/// <remarks>
/// Paging is not decoration. A solution can reference many packages that ship skills, and a list
/// long enough to scroll off the top is a list nobody reads before agreeing to it. The frame is a
/// fixed height and redraws in place, so only one page is ever on screen.
/// </remarks>
internal sealed class SkillPicker(ITerminal terminal)
{
    /// <summary>Rows a page never exceeds, however tall the window is.</summary>
    private const int MaxPageSize = 10;

    /// <summary>Frame rows that are not skills: title, help, status, and their blank separators.</summary>
    private const int ChromeRows = 7;

    /// <summary>Widest the skill name column grows before names are truncated.</summary>
    private const int MaxNameWidth = 44;

    private const string InstalledLabel = "installed";
    private const string NewLabel = "new";

    private const string ActionHelp = "a all   c none   enter confirm   esc cancel";

    /// <summary>
    /// Builds the movement legend from the keys that actually do something here. Offering
    /// "left/right page" on a single page, or "up/down move" on a single skill, teaches the
    /// reader a control that does nothing when they try it.
    /// </summary>
    /// <remarks>
    /// Deliberately ASCII. Windows consoles default to an OEM code page that silently drops
    /// arrows and box-drawing glyphs, so a prettier legend renders as gaps on exactly the
    /// terminal most users are on.
    /// </remarks>
    private static string NavigationHelp(int itemCount, int pages)
    {
        var keys = new List<string>(3);

        if (itemCount > 1)
        {
            keys.Add("up/down move");
        }

        if (pages > 1)
        {
            keys.Add("left/right page");
        }

        keys.Add("space toggle");

        return string.Join("   ", keys);
    }

    /// <summary>
    /// Runs the picker. Returns null when the user cancelled, in which case nothing should be
    /// written to the destination.
    /// </summary>
    public SkillChoice? Choose(IReadOnlyList<SkillPickerItem> items, string title)
    {
        if (items.Count == 0)
        {
            return new SkillChoice([], []);
        }

        if (terminal.IsRedirected)
        {
            throw new PackageSkillsException(
                "--interactive needs a terminal, but input or output is redirected. " +
                "Drop --interactive to install every discovered skill, or name the ones you want " +
                "with --package.");
        }

        // Pre-check what is already installed so pressing enter immediately changes nothing.
        var selected = new HashSet<int>(
            items.Select((item, index) => (item, index))
                .Where(entry => entry.item.Installed)
                .Select(entry => entry.index));

        var layout = Layout.For(terminal, items, title);
        var cursor = 0;

        terminal.CursorVisible = false;

        // Take Ctrl+C as a key so it cancels through the same path as esc. Left to the
        // runtime it kills the process mid-frame, skipping the restore below and leaving
        // the user with a hidden cursor.
        terminal.TreatControlCAsInput = true;

        try
        {
            // Scroll once up front so the frame's top row stays put for every later redraw.
            var frameTop = Reserve(layout.PageSize + ChromeRows);
            var height = 0;

            while (true)
            {
                height = Render(items, selected, cursor, layout, title, frameTop);

                var key = terminal.ReadKey();

                // 'c' clears the selection, so the modifier has to be tested before the
                // switch below reaches that case.
                if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    Close(frameTop + height);
                    return null;
                }

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        cursor = (cursor - 1 + items.Count) % items.Count;
                        break;
                    case ConsoleKey.DownArrow:
                        cursor = (cursor + 1) % items.Count;
                        break;
                    case ConsoleKey.LeftArrow or ConsoleKey.PageUp:
                        cursor = Math.Max(0, cursor - layout.PageSize);
                        break;
                    case ConsoleKey.RightArrow or ConsoleKey.PageDown:
                        cursor = Math.Min(items.Count - 1, cursor + layout.PageSize);
                        break;
                    case ConsoleKey.Home:
                        cursor = 0;
                        break;
                    case ConsoleKey.End:
                        cursor = items.Count - 1;
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
                        height = Render(items, selected, cursor, layout, title, frameTop);
                        Close(frameTop + height);
                        return Result(items, selected);
                    case ConsoleKey.Escape or ConsoleKey.Q:
                        Close(frameTop + height);
                        return null;
                }
            }
        }
        finally
        {
            terminal.CursorVisible = true;
            terminal.TreatControlCAsInput = false;
        }
    }

    private static SkillChoice Result(IReadOnlyList<SkillPickerItem> items, HashSet<int> selected) =>
        new(
            [.. items.Where((_, index) => selected.Contains(index)).Select(item => item.Skill)],
            [
                .. items
                    .Where((item, index) => item.Installed && !selected.Contains(index))
                    .Select(item => item.Skill.RelativePath),
            ]);

    /// <summary>
    /// Writes the frame's worth of blank lines so any scrolling happens before the first render,
    /// and reports the row the frame now starts on.
    /// </summary>
    private int Reserve(int height)
    {
        for (var row = 0; row < height; row++)
        {
            terminal.WriteLine();
        }

        return Math.Max(0, terminal.CursorTop - height);
    }

    private void Close(int bottom)
    {
        terminal.SetCursorPosition(0, bottom);
        terminal.WriteLine();
    }

    /// <summary>Draws one frame at <paramref name="frameTop"/> and reports how many rows it used.</summary>
    /// <remarks>
    /// A partial last page draws only the skills it has, so the summary follows the final skill
    /// instead of a run of blanks. Rows an earlier, taller frame wrote below this one are then
    /// blanked, because in-place redrawing can only erase by overwriting.
    /// </remarks>
    private int Render(
        IReadOnlyList<SkillPickerItem> items,
        HashSet<int> selected,
        int cursor,
        Layout layout,
        string title,
        int frameTop)
    {
        var page = cursor / layout.PageSize;
        var first = page * layout.PageSize;
        var rows = Math.Min(layout.PageSize, items.Count - first);
        var width = layout.Width;

        terminal.SetCursorPosition(0, frameTop);

        // A lone page has no "other" page to be on, so the counter is noise.
        WriteRow(
            layout.Pages > 1 ? Spread(title, $"page {page + 1} of {layout.Pages}", width) : title,
            width);
        WriteRow(string.Empty, width);
        WriteRow($"  {NavigationHelp(items.Count, layout.Pages)}", width);
        WriteRow($"  {ActionHelp}", width);
        WriteRow(string.Empty, width);

        for (var row = 0; row < rows; row++)
        {
            var index = first + row;
            WriteRow(Row(items[index], index == cursor, selected.Contains(index), layout.NameWidth), width);
        }

        WriteRow(string.Empty, width);
        WriteRow($"  {Summary(items, selected)}", width);

        var height = ChromeRows + rows;

        for (var row = height; row < layout.PageSize + ChromeRows; row++)
        {
            WriteRow(string.Empty, width);
        }

        // Park just below the content. Anything that ends the process without unwinding —
        // Ctrl+C on a host that will not hand it to us — then leaves the shell prompt against
        // the summary rather than at the bottom of the rows this frame reserved.
        terminal.SetCursorPosition(0, frameTop + height);

        return height;
    }

    private static string Row(SkillPickerItem item, bool focused, bool isSelected, int nameWidth) =>
        $"{(focused ? '>' : ' ')} [{(isSelected ? 'x' : ' ')}] " +
        $"{Fit(item.Skill.RelativePath, nameWidth).PadRight(nameWidth)}  " +
        $"{(item.Installed ? InstalledLabel : NewLabel).PadRight(InstalledLabel.Length)}  " +
        $"{item.Skill.PackageId} {item.Skill.PackageVersion}";

    private static string Summary(IReadOnlyList<SkillPickerItem> items, HashSet<int> selected)
    {
        var removing = items
            .Select((item, index) => (item, index))
            .Count(entry => entry.item.Installed && !selected.Contains(entry.index));

        var summary = $"{selected.Count} of {items.Count} selected";

        return removing == 0 ? summary : $"{summary}   {removing} to remove";
    }

    /// <summary>
    /// Pads a row to the frame width, which erases whatever the previous frame left on that
    /// line. The width is content-derived and always narrower than the window, so this never
    /// triggers the automatic wrap a full-width line causes.
    /// </summary>
    private void WriteRow(string text, int width) => terminal.WriteLine(Fit(text, width).PadRight(width));

    private static string Spread(string left, string right, int width)
    {
        var gap = width - left.Length - right.Length;
        return gap > 1 ? $"{left}{new string(' ', gap)}{right}" : $"{left}  {right}";
    }

    private static string Fit(string text, int width) => width switch
    {
        <= 0 => string.Empty,
        _ when text.Length <= width => text,
        <= 3 => text[..width],
        _ => text[..(width - 3)] + "...",
    };

    private static int MeasureNameWidth(IReadOnlyList<SkillPickerItem> items) =>
        Math.Min(MaxNameWidth, items.Max(item => item.Skill.RelativePath.Length));

    /// <summary>Frame dimensions, measured once so every redraw lands on the same grid.</summary>
    /// <param name="PageSize">Skill rows shown at once.</param>
    /// <param name="Pages">Total pages, which decides whether paging chrome is worth showing.</param>
    /// <param name="NameWidth">Width of the skill-name column.</param>
    /// <param name="Width">
    /// Width every row is padded to. Derived from the content rather than the window, so the
    /// page counter sits beside the title instead of stranded at the far edge of a wide
    /// terminal, and no row trails padding past the text it belongs to.
    /// </param>
    private sealed record Layout(int PageSize, int Pages, int NameWidth, int Width)
    {
        public static Layout For(ITerminal terminal, IReadOnlyList<SkillPickerItem> items, string title)
        {
            // As many rows as fit, but never more than there are skills. Padding a short list
            // out to a full page is what left a single skill stranded above blank lines.
            var pageSize = Math.Clamp(
                terminal.WindowHeight - ChromeRows - 1,
                1,
                Math.Min(MaxPageSize, items.Count));

            var pages = (items.Count + pageSize - 1) / pageSize;
            var nameWidth = MeasureNameWidth(items);

            // Measure the widest line any frame could produce. Anything narrower would leave
            // characters from a previous frame behind when a later one is shorter.
            var content = new List<int>
            {
                pages > 1 ? title.Length + 2 + $"page {pages} of {pages}".Length : title.Length,
                NavigationHelp(items.Count, pages).Length + 2,
                ActionHelp.Length + 2,
                WidestSummary(items.Count) + 2,
            };

            content.AddRange(items.Select(item => Row(item, focused: true, isSelected: true, nameWidth).Length));

            // Stay a column short of the window so a full-width line cannot wrap.
            return new Layout(pageSize, pages, nameWidth, Math.Min(content.Max(), Math.Max(20, terminal.WindowWidth) - 1));
        }

        /// <summary>
        /// Longest the summary can grow: every count at its maximum, with the removal clause
        /// present. Measured rather than observed, because the live summary shrinks and grows.
        /// </summary>
        private static int WidestSummary(int itemCount) =>
            $"{itemCount} of {itemCount} selected   {itemCount} to remove".Length;
    }
}
