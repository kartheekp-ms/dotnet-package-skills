namespace DotnetPackageSkills.Cli;

/// <summary>A selection-independent layout, rebuilt only when the viewport changes.</summary>
internal sealed class PickerLayout
{
    internal const string PrimaryHelp = "(Press <space> to select, <enter> to accept)";

    internal sealed record Entry(string Label, int DescriptionColumn, IReadOnlyList<string> Description)
    {
        public int Height => Description.Count;
    }

    internal sealed record Page(int First, int Count, int VisibleRows, bool Scrollable);

    private readonly string _title;
    private readonly int _headerRows;
    private readonly int _footerRows;
    private readonly int[] _itemPages;

    private PickerLayout(
        int windowWidth,
        int windowHeight,
        int width,
        bool supportsColor,
        string title,
        IReadOnlyList<Entry> entries,
        IReadOnlyList<Page> pages,
        IReadOnlyList<string> help,
        int headerRows,
        int footerRows)
    {
        WindowWidth = windowWidth;
        WindowHeight = windowHeight;
        Width = width;
        SupportsColor = supportsColor;
        _title = title;
        Entries = entries;
        Pages = pages;
        Help = help;
        _headerRows = headerRows;
        _footerRows = footerRows;
        _itemPages = new int[entries.Count];
        for (var page = 0; page < pages.Count; page++)
        {
            Array.Fill(_itemPages, page, pages[page].First, pages[page].Count);
        }
    }

    public int WindowWidth { get; }

    public int WindowHeight { get; }

    public int Width { get; }

    public bool SupportsColor { get; }

    public int ContinuationColumn => SupportsColor ? 6 : 8;

    public IReadOnlyList<Entry> Entries { get; }

    public IReadOnlyList<Page> Pages { get; }

    public IReadOnlyList<string> Help { get; }

    public int MaxFrameHeight => Pages.Max(page =>
        _headerRows + 2 + _footerRows + page.VisibleRows +
        (page.Scrollable ? ScrollHelpRows(Entries[page.First].Height, Width) : 0));

    public int PageIndexFor(int item) => _itemPages[item];

    public int MaxScroll(int item)
    {
        var page = Pages[PageIndexFor(item)];
        return page.Scrollable ? Entries[item].Height - page.VisibleRows : 0;
    }

    public IReadOnlyList<string> Header(int page) => Header(_title, page + 1, Pages.Count, Width);

    public static PickerLayout For(
        IReadOnlyList<SkillPickerItem> items,
        string title,
        PickerMode mode,
        int windowWidth,
        int windowHeight,
        bool supportsColor)
    {
        var labels = items.Select(item => TerminalText.Sanitize(item.Name)).ToArray();
        var descriptions = items.Select(DescriptionFor).ToArray();
        var cleanTitle = TerminalText.Sanitize(title);
        var longestName = labels.Max(TerminalText.Width);
        var longestDescription = descriptions.Max(description => description.Split('\n').Max(TerminalText.Width));
        var prefix = supportsColor ? 6 : 8;
        var summaryBounds = SummaryBounds(items, mode).ToArray();
        var helpWidths = HelpFor(items.Count, items.Count, supportsColor, mode).Select(TerminalText.Width);
        var naturalWidth = new[]
        {
            labels.Select((label, index) => prefix + TerminalText.Width(label) + 3 +
                descriptions[index].Split('\n').Max(TerminalText.Width)).Max(),
            TerminalText.Width(cleanTitle) + (items.Count > 1 ? 2 + Counter(items.Count, items.Count).Length : 0),
            summaryBounds.Max(TerminalText.Width),
            helpWidths.Max(),
        }.Max();

        // Leave a column and a row untouched so neither padding nor a final newline can
        // force an automatic wrap in the middle of a frame.
        var width = Math.Min(naturalWidth, windowWidth - 1);
        var available = width - prefix - 3;
        var widestGrapheme = descriptions.SelectMany(TerminalText.Elements).Max(TerminalText.CellWidth);
        if (available < 1 + widestGrapheme)
        {
            throw TooSmall(windowWidth, windowHeight, $"at least {prefix + 3 + widestGrapheme + 2} columns");
        }

        // When both columns want more than the window, give the description at least half
        // the remaining cells. Short descriptions give that space back to a long name.
        var descriptionReserve = Math.Max(widestGrapheme, Math.Min(longestDescription, available / 2));
        var nameWidth = Math.Min(longestName, available - descriptionReserve);
        var entries = labels.Select((label, index) =>
        {
            var displayName = TerminalText.Clip(label, nameWidth);
            var descriptionColumn = prefix + TerminalText.Width(displayName) + 3;
            return new Entry(
                displayName,
                descriptionColumn,
                TerminalText.Wrap(descriptions[index], width - descriptionColumn, width - prefix));
        }).ToArray();

        var pageCount = 1;
        while (true)
        {
            var headerRows = Header(cleanTitle, pageCount, pageCount, width).Count;
            var help = HelpFor(items.Count, pageCount, supportsColor, mode)
                .SelectMany(line => TerminalText.Wrap(line, width)).ToArray();
            var footerRows = summaryBounds.Max(summary => TerminalText.Wrap(summary, width).Count) + help.Length;
            var budget = windowHeight - 1 - headerRows - 2 - footerRows;
            if (budget < 1)
            {
                throw TooSmall(windowWidth, windowHeight, $"at least {windowHeight + 1 - budget} rows at this width");
            }

            var pages = Paginate(entries, budget, width, windowWidth, windowHeight);
            if (pages.Count == pageCount)
            {
                return new PickerLayout(
                    windowWidth, windowHeight, width, supportsColor, cleanTitle,
                    entries, pages, help, headerRows, footerRows);
            }

            // Only paging chrome and counter digit growth can shrink the row budget.
            // Iterating to a fixed point avoids guessing how many rows that chrome uses.
            pageCount = pages.Count;
        }
    }

    public static string Summary(int selected, int total, int installing, int removing, PickerMode mode) =>
        mode == PickerMode.Uninstall
            ? $"{selected} of {total} selected; {removing} to remove"
            : $"{selected} of {total} selected; {installing} to install; {removing} to remove";

    public static string ScrollHelp(int first, int last, int total) =>
        $"(Press <Ctrl+Up>/<Ctrl+Down> to scroll description: {first}-{last}/{total})";

    private static IEnumerable<string> SummaryBounds(IReadOnlyList<SkillPickerItem> items, PickerMode mode)
    {
        if (mode == PickerMode.Uninstall)
        {
            yield return Summary(items.Count, items.Count, 0, items.Count, mode);
            yield break;
        }

        var installed = items.Count(item => item.Installed);
        var available = items.Count - installed;
        // Counts with the same number of digits wrap identically. In each pair of digit
        // ranges, the largest addition and smallest removal maximize the selected count.
        // This measures attainable summaries, not an impossible "all added AND all removed".
        for (long additionLimit = 9; ; additionLimit = additionLimit * 10 + 9)
        {
            var adding = (int)Math.Min(available, additionLimit);
            for (long removing = 0; removing <= installed; removing = removing == 0 ? 10 : removing * 10)
            {
                yield return Summary(installed - (int)removing + adding, items.Count, adding, (int)removing, mode);
            }

            if (additionLimit >= available)
            {
                yield break;
            }
        }
    }

    private static int ScrollHelpRows(int lines, int width) =>
        TerminalText.Wrap(ScrollHelp(lines, lines, lines), width).Count;

    private static List<Page> Paginate(
        IReadOnlyList<Entry> entries,
        int budget,
        int width,
        int windowWidth,
        int windowHeight)
    {
        var pages = new List<Page>();
        for (var first = 0; first < entries.Count;)
        {
            if (entries[first].Height > budget)
            {
                var visible = budget - ScrollHelpRows(entries[first].Height, width);
                // Keep the skill row visible alongside at least one scrolling continuation.
                if (visible < 2)
                {
                    throw TooSmall(windowWidth, windowHeight, $"at least {windowHeight + 2 - visible} rows at this width");
                }

                pages.Add(new Page(first++, 1, visible, Scrollable: true));
                continue;
            }

            var rows = 0;
            var end = first;
            while (end < entries.Count && rows + entries[end].Height <= budget)
            {
                rows += entries[end++].Height;
            }

            pages.Add(new Page(first, end - first, rows, Scrollable: false));
            first = end;
        }

        return pages;
    }

    private static string DescriptionFor(SkillPickerItem item)
    {
        var prefix = item.Retained ? "Installed copy; kept unless you uncheck it. " : string.Empty;
        if (!string.IsNullOrWhiteSpace(item.DescriptionWarning))
        {
            var warning = TerminalText.Sanitize(item.DescriptionWarning);
            return $"{prefix}Description unavailable: {(TerminalText.Width(warning) > 0 ? warning : "unreadable metadata.")}";
        }

        var description = TerminalText.Sanitize(item.Description, multiline: true);
        return prefix + (TerminalText.Width(description) > 0 ? description : "No description provided.");
    }

    private static IReadOnlyList<string> Header(string title, int page, int pages, int width)
    {
        var text = pages > 1 ? $"{title}  {Counter(page, pages)}" : title;
        return TerminalText.Width(text) <= width ? [text] : TerminalText.Wrap(text, width);
    }

    private static string Counter(int page, int pages) => $"page {page} of {pages}";

    private static IEnumerable<string> HelpFor(int items, int pages, bool supportsColor, PickerMode mode)
    {
        yield return PrimaryHelp;
        if (items > 1)
        {
            yield return "(Press <up>/<down> to move, <Home>/<End> for first/last)";
        }

        if (pages > 1)
        {
            yield return "(Press <left>/<right>, <PageUp>/<PageDown> to change page)";
        }

        yield return items > 1
            ? "(Press <a> to select all, <c> to clear all, <Esc>/<q>/<Ctrl+C> to cancel)"
            : "(Press <Esc>/<q>/<Ctrl+C> to cancel)";
        yield return (supportsColor, mode) switch
        {
            (true, PickerMode.Install) => "Green: install   Red: remove",
            (true, PickerMode.Uninstall) => "Red: remove",
            (false, PickerMode.Install) => "+ install   - remove",
            _ => "- remove",
        };
    }

    private static PackageSkillsException TooSmall(int width, int height, string minimum) => new(
        $"The terminal is too small for the interactive checklist ({width}x{height}). " +
        $"Enlarge the window to {minimum}, or use the command without --interactive " +
        "and with --package to limit the operation.");
}
