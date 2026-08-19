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

    /// <summary>
    /// Deliberately ASCII. Windows consoles default to an OEM code page that silently drops
    /// arrows and box-drawing glyphs, so a prettier legend renders as gaps on exactly the
    /// terminal most users are on.
    /// </summary>
    private const string NavigationHelp = "up/down move   left/right page   space toggle";

    private const string ActionHelp = "a all   c none   enter confirm   esc cancel";

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

        var pageSize = PageSize();
        var nameWidth = NameWidth(items);
        var cursor = 0;

        terminal.CursorVisible = false;

        try
        {
            // Scroll once up front so the frame's top row stays put for every later redraw.
            var frameTop = Reserve(pageSize + ChromeRows);

            while (true)
            {
                Render(items, selected, cursor, pageSize, nameWidth, title, frameTop);

                var key = terminal.ReadKey();

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        cursor = (cursor - 1 + items.Count) % items.Count;
                        break;
                    case ConsoleKey.DownArrow:
                        cursor = (cursor + 1) % items.Count;
                        break;
                    case ConsoleKey.LeftArrow or ConsoleKey.PageUp:
                        cursor = Math.Max(0, cursor - pageSize);
                        break;
                    case ConsoleKey.RightArrow or ConsoleKey.PageDown:
                        cursor = Math.Min(items.Count - 1, cursor + pageSize);
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
                        Render(items, selected, cursor, pageSize, nameWidth, title, frameTop);
                        Close(frameTop + pageSize + ChromeRows);
                        return Result(items, selected);
                    case ConsoleKey.Escape or ConsoleKey.Q:
                        Close(frameTop + pageSize + ChromeRows);
                        return null;
                }
            }
        }
        finally
        {
            terminal.CursorVisible = true;
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

    private void Render(
        IReadOnlyList<SkillPickerItem> items,
        HashSet<int> selected,
        int cursor,
        int pageSize,
        int nameWidth,
        string title,
        int frameTop)
    {
        var page = cursor / pageSize;
        var pages = (items.Count + pageSize - 1) / pageSize;
        var first = page * pageSize;
        var width = Math.Max(20, terminal.WindowWidth);

        terminal.SetCursorPosition(0, frameTop);

        WriteRow(Spread(title, $"page {page + 1} of {pages}", width), width);
        WriteRow(string.Empty, width);
        WriteRow($"  {NavigationHelp}", width);
        WriteRow($"  {ActionHelp}", width);
        WriteRow(string.Empty, width);

        for (var row = 0; row < pageSize; row++)
        {
            var index = first + row;

            WriteRow(
                index < items.Count
                    ? Row(items[index], index == cursor, selected.Contains(index), nameWidth)
                    : string.Empty,
                width);
        }

        WriteRow(string.Empty, width);
        WriteRow($"  {Summary(items, selected)}", width);
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
    /// Pads a row to just short of the window width, which erases whatever the previous frame
    /// left on that line without triggering the automatic wrap a full-width line causes.
    /// </summary>
    private void WriteRow(string text, int width) => terminal.WriteLine(Fit(text, width - 1).PadRight(width - 1));

    private static string Spread(string left, string right, int width)
    {
        var gap = width - 1 - left.Length - right.Length;
        return gap > 1 ? $"{left}{new string(' ', gap)}{right}" : $"{left}  {right}";
    }

    private static string Fit(string text, int width) => width switch
    {
        <= 0 => string.Empty,
        _ when text.Length <= width => text,
        <= 3 => text[..width],
        _ => text[..(width - 3)] + "...",
    };

    private int PageSize() =>
        Math.Clamp(terminal.WindowHeight - ChromeRows - 1, 1, MaxPageSize);

    private static int NameWidth(IReadOnlyList<SkillPickerItem> items) =>
        Math.Min(MaxNameWidth, items.Max(item => item.Skill.RelativePath.Length));
}
