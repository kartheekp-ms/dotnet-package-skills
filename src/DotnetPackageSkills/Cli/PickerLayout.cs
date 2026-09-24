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
    private readonly string? _note;
    private readonly int _headerRows;
    private readonly int _footerRows;
    private readonly int[] _itemPages;

    private PickerLayout(
        int windowWidth,
        int windowHeight,
        int width,
        bool supportsColor,
        string title,
        string? note,
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
        _note = note;
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

    /// <summary>
    /// Where a wrapped description continues: past the cursor, the checkbox, and a space. Both
    /// pickers draw a row the same way, with or without color.
    /// </summary>
    public int ContinuationColumn => RowPrefix;

    private const int RowPrefix = 6;

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

    public IReadOnlyList<string> Header(int page) => Header(_title, _note, page + 1, Pages.Count, Width);

    public static PickerLayout For(
        IReadOnlyList<SkillPickerItem> items,
        string title,
        PickerMode mode,
        int windowWidth,
        int windowHeight,
        bool supportsColor,
        string? note = null)
    {
        if (note is null)
        {
            return Build(items, title, mode, windowWidth, windowHeight, supportsColor, note: null);
        }

        try
        {
            return Build(items, title, mode, windowWidth, windowHeight, supportsColor, note);
        }
        catch (PackageSkillsException)
        {
            // The note only explains what the list leaves out. A window without room for it
            // keeps the checklist and loses the note, rather than refusing to open.
            return Build(items, title, mode, windowWidth, windowHeight, supportsColor, note: null);
        }
    }

    private static PickerLayout Build(
        IReadOnlyList<SkillPickerItem> items,
        string title,
        PickerMode mode,
        int windowWidth,
        int windowHeight,
        bool supportsColor,
        string? note)
    {
        var labels = items.Select(item => TerminalText.Sanitize(item.Name)).ToArray();
        var descriptions = items.Select(DescriptionFor).ToArray();
        var cleanTitle = TerminalText.Sanitize(title);
        var cleanNote = note is null ? null : TerminalText.Sanitize(note);
        var longestName = labels.Max(TerminalText.Width);
        var longestDescription = descriptions.Max(description => description.Split('\n').Max(TerminalText.Width));
        var prefix = RowPrefix;
        var summaryBounds = new[] { Summary(items.Count, items.Count, mode) };
        var helpWidths = HelpFor(items.Count, items.Count, supportsColor).Select(TerminalText.Width);
        var naturalWidth = new[]
        {
            labels.Select((label, index) => prefix + TerminalText.Width(label) + 3 +
                descriptions[index].Split('\n').Max(TerminalText.Width)).Max(),
            TerminalText.Width(cleanTitle) + (items.Count > 1 ? 2 + Counter(items.Count, items.Count).Length : 0),
            cleanNote is null ? 0 : TerminalText.Width(cleanNote),
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
            var headerRows = Header(cleanTitle, cleanNote, pageCount, pageCount, width).Count;
            var help = HelpFor(items.Count, pageCount, supportsColor)
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
                    windowWidth, windowHeight, width, supportsColor, cleanTitle, cleanNote,
                    entries, pages, help, headerRows, footerRows);
            }

            // Only paging chrome and counter digit growth can shrink the row budget.
            // Iterating to a fixed point avoids guessing how many rows that chrome uses.
            pageCount = pages.Count;
        }
    }

    /// <summary>
    /// Nothing on an install list is installed, so every tick is one install and the count needs
    /// no second number. Every tick on an uninstall list is one removal, which is worth saying.
    /// The widest summary is the one with every row ticked, so that is what layout measures.
    /// </summary>
    public static string Summary(int selected, int total, PickerMode mode) =>
        mode == PickerMode.Uninstall
            ? $"{selected} of {total} selected; {selected} to remove"
            : $"{selected} of {total} selected";

    public static string ScrollHelp(int first, int last, int total) =>
        $"(Press <Ctrl+Up>/<Ctrl+Down> to scroll description: {first}-{last}/{total})";

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
        if (!string.IsNullOrWhiteSpace(item.DescriptionWarning))
        {
            var warning = TerminalText.Sanitize(item.DescriptionWarning);
            return $"Description unavailable: {(TerminalText.Width(warning) > 0 ? warning : "unreadable metadata.")}";
        }

        var description = TerminalText.Sanitize(item.Description, multiline: true);
        return TerminalText.Width(description) > 0 ? description : "No description provided.";
    }

    private static IReadOnlyList<string> Header(string title, string? note, int page, int pages, int width)
    {
        var text = pages > 1 ? $"{title}  {Counter(page, pages)}" : title;
        IReadOnlyList<string> lines = TerminalText.Width(text) <= width ? [text] : TerminalText.Wrap(text, width);
        return note is null ? lines : [.. lines, .. TerminalText.Wrap(note, width)];
    }

    private static string Counter(int page, int pages) => $"page {page} of {pages}";

    private static IEnumerable<string> HelpFor(int items, int pages, bool supportsColor)
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

        // Each checklist does one thing, so a tick needs no cue for what it does: the title and
        // the summary say that. The legend only explains the color, so without color it goes.
        if (supportsColor)
        {
            yield return "Blue X: selected";
        }
    }

    private static PackageSkillsException TooSmall(int width, int height, string minimum) => new(
        $"The terminal is too small for the interactive checklist ({width}x{height}). " +
        $"Enlarge the window to {minimum}, or use the command without --interactive " +
        "and with --package to limit the operation.");
}
