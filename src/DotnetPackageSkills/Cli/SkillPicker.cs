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

    /// <summary>Frame rows that are not skills: title, help, status, and their blank separators.</summary>
    private const int ChromeRows = 7;

    /// <summary>
    /// Columns a skill row spends on everything except the name and the status: the cursor and
    /// checkbox that open it, and the gap before the status.
    /// </summary>
    private const int RowFurniture = 6 + 2;

    /// <summary>
    /// Narrowest the name column is allowed to get before it stops giving ground to the
    /// package column. Below this a truncated name says nothing useful.
    /// </summary>
    private const int MinNameWidth = 12;

    private const string InstalledLabel = "installed";
    private const string WillInstallLabel = "will install";
    private const string WillRemoveLabel = "will remove";

    /// <summary>Widest any status can be, so the column is one width for every row.</summary>
    private static readonly int StatusWidth =
        new[] { InstalledLabel, WillInstallLabel, WillRemoveLabel }.Max(label => label.Length);

    /// <summary>
    /// Legend for browsing: what you do while looking around, then the key that does it.
    /// </summary>
    /// <remarks>
    /// Action first, key in brackets. "space toggle" reads as jargon to anyone who has not
    /// already been told what it means, whereas "toggle selection (space)" answers the
    /// question the reader is actually asking, which is what they can do here.
    ///
    /// Built from the keys that do something: offering "change page" on a single page, or
    /// "move" on a single skill, teaches a control that does nothing when they try it.
    ///
    /// Deliberately ASCII. Windows consoles default to an OEM code page that silently drops
    /// arrows and box-drawing glyphs, so a prettier legend renders as gaps on exactly the
    /// terminal most users are on.
    /// </remarks>
    private static string BrowseHelp(int itemCount, int pages)
    {
        var entries = new List<string>(3);

        if (itemCount > 1)
        {
            entries.Add("move (up/down)");
        }

        if (pages > 1)
        {
            entries.Add("change page (left/right)");
        }

        entries.Add("toggle selection (space)");

        return string.Join("   ", entries);
    }

    /// <summary>Legend for the rest: acting on everything at once, and leaving.</summary>
    private static string CommitHelp(int itemCount)
    {
        var entries = new List<string>(4);

        // With one skill, "select all" and "clear all" are just a slower way to press space.
        if (itemCount > 1)
        {
            entries.Add("select all (a)");
            entries.Add("clear all (c)");
        }

        entries.Add("confirm (enter)");
        entries.Add("cancel (esc)");

        return string.Join("   ", entries);
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
        WriteRow($"  {BrowseHelp(items.Count, layout.Pages)}", width);
        WriteRow($"  {CommitHelp(items.Count)}", width);
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

    private static string Row(SkillPickerItem item, bool focused, bool isSelected, int nameWidth)
    {
        var name = $"{item.Skill.RelativePath} ({item.Skill.PackageId} {item.Skill.PackageVersion})";

        return $"{(focused ? '>' : ' ')} [{(isSelected ? 'x' : ' ')}] " +
               $"{Fit(name, nameWidth).PadRight(nameWidth)}  " +
               Status(item, isSelected);
    }

    /// <summary>
    /// What confirming would do to this row, rather than what it is.
    /// </summary>
    /// <remarks>
    /// The column used to read "new" or "installed", which classified the skill instead of
    /// telling you the consequence of the box beside it — and on a first run every row said
    /// "new", so a whole column carried nothing. A row that changes nothing now says nothing.
    /// </remarks>
    private static string Status(SkillPickerItem item, bool isSelected) => (item.Installed, isSelected) switch
    {
        (false, true) => WillInstallLabel,
        (true, false) => WillRemoveLabel,
        (true, true) => InstalledLabel,
        _ => string.Empty,
    };

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

    /// <summary>
    /// Width of the name column, which carries the skill and the package it came from: as wide
    /// as the longest of those, unless the terminal is too narrow to hold it beside the status.
    /// </summary>
    /// <remarks>
    /// This used to be capped at a constant, so a long name lost its tail even on a
    /// two-hundred-column window. The only real limit is the window.
    /// </remarks>
    private static int MeasureNameWidth(IReadOnlyList<SkillPickerItem> items, int windowWidth)
    {
        var longest = items.Max(item =>
            item.Skill.RelativePath.Length + item.Skill.PackageId.Length +
            item.Skill.PackageVersion.Length + 4);

        var available = windowWidth - 1 - RowFurniture - StatusWidth;

        return Math.Min(longest, Math.Max(MinNameWidth, available));
    }

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
            // As many rows as the window has space for, and never more than there are skills.
            // A fixed ceiling would page a list that already fits, and padding a short list
            // out to a full page is what left a single skill stranded above blank lines.
            var pageSize = Math.Clamp(
                terminal.WindowHeight - ChromeRows - 1,
                1,
                items.Count);

            var pages = (items.Count + pageSize - 1) / pageSize;
            var windowWidth = Math.Max(20, terminal.WindowWidth);
            var nameWidth = MeasureNameWidth(items, windowWidth);

            // Measure the widest line any frame could produce. Anything narrower would leave
            // characters from a previous frame behind when a later one is shorter.
            var content = new List<int>
            {
                pages > 1 ? title.Length + 2 + $"page {pages} of {pages}".Length : title.Length,
                BrowseHelp(items.Count, pages).Length + 2,
                CommitHelp(items.Count).Length + 2,
                WidestSummary(items.Count) + 2,
            };

            // Both tick states, because the status changes with the box and "will install" is
            // wider than "installed". Measuring only one leaves the other clipped.
            content.AddRange(items.Select(item => Row(item, focused: true, isSelected: true, nameWidth).Length));
            content.AddRange(items.Select(item => Row(item, focused: true, isSelected: false, nameWidth).Length));

            // Stay a column short of the window so a full-width line cannot wrap.
            return new Layout(pageSize, pages, nameWidth, Math.Min(content.Max(), windowWidth - 1));
        }

        /// <summary>
        /// Longest the summary can grow: every count at its maximum, with the removal clause
        /// present. Measured rather than observed, because the live summary shrinks and grows.
        /// </summary>
        private static int WidestSummary(int itemCount) =>
            $"{itemCount} of {itemCount} selected   {itemCount} to remove".Length;
    }
}
