using DotnetPackageSkills.Cli;

namespace DotnetPackageSkills.Tests;

public class PickerLayoutTests
{
    [Fact]
    public void Mixed_height_entries_have_exact_whole_entry_page_boundaries()
    {
        var layout = Layout(80, 24, [1, 3, 2, 4, 1, 2, 3, 1, 5, 2, 1, 4, 2, 3]);

        Assert.Equal(
            [
                new PickerLayout.Page(0, 6, 13, false),
                new PickerLayout.Page(6, 5, 12, false),
                new PickerLayout.Page(11, 3, 9, false),
            ],
            layout.Pages);
        Assert.Equal(0, layout.PageIndexFor(5));
        Assert.Equal(1, layout.PageIndexFor(6));
        Assert.Equal(2, layout.PageIndexFor(13));
        Assert.Equal(22, layout.MaxFrameHeight);
    }

    [Fact]
    public void Only_an_oversized_entry_gets_a_scrolling_one_item_page()
    {
        var layout = Layout(80, 24, [2, 50, 2, 1]);

        Assert.Equal(
            [
                new PickerLayout.Page(0, 1, 2, false),
                new PickerLayout.Page(1, 1, 13, true),
                new PickerLayout.Page(2, 2, 3, false),
            ],
            layout.Pages);
        Assert.Equal(37, layout.MaxScroll(1));
        Assert.Equal(0, layout.MaxScroll(0));
        Assert.Equal(0, layout.MaxScroll(2));
        Assert.Equal(23, layout.MaxFrameHeight);
    }

    [Fact]
    public void Forty_six_columns_measure_wrapped_rows_and_wrapped_controls()
    {
        var items = Enumerable.Range(1, 24)
            .Select(number => new SkillPickerItem($"skill-{number:00}", $"Package.{number}", "1.0.0"))
            .ToArray();

        var layout = PickerLayout.For(items, "Skills for App.slnx", PickerMode.Install, 46, 18, true);

        Assert.Equal(45, layout.Width);
        Assert.All(layout.Entries, entry =>
        {
            Assert.Equal(17, entry.DescriptionColumn);
            Assert.Equal(["No description provided."], entry.Description);
        });
        Assert.Equal([5, 5, 5, 5, 4], layout.Pages.Select(page => page.Count));
        Assert.Equal(17, layout.MaxFrameHeight);
        Assert.Contains("(Press <space> to select, <enter> to accept)", layout.Help);
        Assert.Contains("<Ctrl+C> to cancel)", string.Join(" ", layout.Help));
    }

    [Fact]
    public void A_normal_eighty_by_twenty_four_window_uses_its_real_row_budget()
    {
        var layout = Layout(80, 24, Enumerable.Repeat(1, 24).ToArray());

        Assert.Equal(new PickerLayout.Page(0, 14, 14, false), layout.Pages[0]);
        Assert.Equal(new PickerLayout.Page(14, 10, 10, false), layout.Pages[1]);
        Assert.Equal(23, layout.MaxFrameHeight);
    }

    [Fact]
    public void A_wide_tall_window_has_no_fixed_item_or_width_ceiling()
    {
        var layout = Layout(240, 100, Enumerable.Repeat(1, 60).ToArray());

        Assert.Equal(new PickerLayout.Page(0, 60, 60, false), Assert.Single(layout.Pages));
        Assert.Equal(68, layout.MaxFrameHeight);
        Assert.True(layout.Width < 80);
        Assert.DoesNotContain(layout.Help, line => line.Contains("change page", StringComparison.Ordinal));
    }

    [Fact]
    public void A_wrapped_title_takes_space_away_from_entries_not_from_the_footer()
    {
        var items = Enumerable.Range(1, 24)
            .Select(number => new SkillPickerItem($"skill-{number:00}", "P", "1", "short"))
            .ToArray();
        var title = string.Join(" ", Enumerable.Repeat("titleword", 20));

        var layout = PickerLayout.For(items, title, PickerMode.Install, 80, 24, true);

        Assert.Equal(3, layout.Header(0).Count);
        Assert.Equal(12, layout.Pages[0].Count);
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal($"{title} page 1 of 2", string.Join(" ", layout.Header(0)));
        Assert.Equal(23, layout.MaxFrameHeight);
    }

    [Fact]
    public void Page_counter_digit_growth_is_included_in_the_fixed_point()
    {
        var items = Enumerable.Range(1, 120)
            .Select(number => new SkillPickerItem($"skill-{number:000}", "Package", "1.0.0"))
            .ToArray();

        var layout = PickerLayout.For(items, new string('t', 32), PickerMode.Install, 46, 18, true);

        Assert.Equal(30, layout.Pages.Count);
        Assert.Single(layout.Header(0));
        Assert.Equal(2, layout.Header(29).Count);
        Assert.Equal(29, layout.PageIndexFor(119));
        Assert.All(layout.Pages, page => Assert.Equal(4, page.Count));
        Assert.True(layout.MaxFrameHeight < 18);
    }

    [Fact]
    public void Summary_reservation_measures_attainable_counts_not_impossible_combinations()
    {
        var items = Enumerable.Range(1, 10)
            .Select(number => new SkillPickerItem($"skill-{number:00}", "P", "1", "Short."))
            .ToArray();

        var layout = PickerLayout.For(items, "Skills", PickerMode.Install, 45, 18, true);

        Assert.Equal(
            [
                new PickerLayout.Page(0, 5, 5, false),
                new PickerLayout.Page(5, 5, 5, false),
            ],
            layout.Pages);
        Assert.Equal(17, layout.MaxFrameHeight);
    }

    [Fact]
    public void Package_metadata_does_not_consume_space_or_change_page_boundaries()
    {
        var items = Enumerable.Range(1, 12)
            .Select(number => new SkillPickerItem($"skill-{number:00}", "P", "1", "A short description."))
            .ToArray();
        var verboseMetadata = items.Select(item => item with
        {
            Package = new string('p', 120),
            Version = "1.0.0-a-very-long-prerelease-version",
        }).ToArray();

        var original = PickerLayout.For(items, "Skills", PickerMode.Install, 100, 24, true);
        var verbose = PickerLayout.For(verboseMetadata, "Skills", PickerMode.Install, 100, 24, true);

        Assert.Equal(original.Width, verbose.Width);
        Assert.Equal(original.Pages, verbose.Pages);
        Assert.Equal(original.Entries.Select(entry => entry.Label), verbose.Entries.Select(entry => entry.Label));
        Assert.Equal(
            original.Entries.SelectMany(entry => entry.Description),
            verbose.Entries.SelectMany(entry => entry.Description));
    }

    [Fact]
    public void Shorter_names_give_their_descriptions_more_space_to_wrap()
    {
        const string description = "One two three four five six seven eight nine ten.";
        var layout = PickerLayout.For(
            [
                new SkillPickerItem("alpha", "P", "1", description),
                new SkillPickerItem("longer-skill", "P", "1", description),
            ],
            "Skills", PickerMode.Install, 46, 24, true);

        Assert.Equal(14, layout.Entries[0].DescriptionColumn);
        Assert.Equal(
            ["One two three four five six", "seven eight nine ten."],
            layout.Entries[0].Description);
        Assert.Equal(21, layout.Entries[1].DescriptionColumn);
        Assert.Equal(
            ["One two three four five", "six seven eight nine ten."],
            layout.Entries[1].Description);
        Assert.Equal(6, layout.ContinuationColumn);
        Assert.Equal(new PickerLayout.Page(0, 2, 4, false), Assert.Single(layout.Pages));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_keyboard_hint_starts_with_Press_in_both_modes(bool uninstall)
    {
        var items = Enumerable.Range(1, 24)
            .Select(number => new SkillPickerItem($"skill-{number:00}", "P", "1", "Short."))
            .ToArray();
        var layout = PickerLayout.For(
            items, "Skills", uninstall ? PickerMode.Uninstall : PickerMode.Install, 100, 18, true);

        Assert.Contains(PickerLayout.PrimaryHelp, layout.Help);
        Assert.Contains("(Press <up>/<down> to move, <Home>/<End> for first/last)", layout.Help);
        Assert.Contains("(Press <left>/<right>, <PageUp>/<PageDown> to change page)", layout.Help);
        Assert.Contains("(Press <a> to select all, <c> to clear all, <Esc>/<q>/<Ctrl+C> to cancel)", layout.Help);
        Assert.All(layout.Help.Where(line => line.StartsWith('(')), line => Assert.StartsWith("(Press <", line));
        Assert.Equal(
            "(Press <Ctrl+Up>/<Ctrl+Down> to scroll description: 1-4/20)",
            PickerLayout.ScrollHelp(1, 4, 20));
    }

    [Fact]
    public void Both_pickers_share_one_legend_and_rows_start_in_the_same_column_without_color()
    {
        var items = new[] { new SkillPickerItem("alpha", "P", "1", "One two three four five six seven eight nine ten.") };

        var install = PickerLayout.For(items, "Skills", PickerMode.Install, 46, 24, true);
        var uninstall = PickerLayout.For(items, "Skills", PickerMode.Uninstall, 46, 24, true);
        var plain = PickerLayout.For(items, "Skills", PickerMode.Uninstall, 46, 24, false);

        Assert.Equal(install.Help, uninstall.Help);
        Assert.Contains("Blue X: selected", uninstall.Help);
        Assert.All(plain.Help, line => Assert.StartsWith("(Press <", line));
        Assert.Equal(install.ContinuationColumn, plain.ContinuationColumn);
        Assert.Equal(install.Entries[0].DescriptionColumn, plain.Entries[0].DescriptionColumn);
        Assert.Equal(install.Entries[0].Description, plain.Entries[0].Description);
    }

    [Fact]
    public void A_window_too_small_for_the_note_drops_the_note_rather_than_the_checklist()
    {
        // At 46x18 the scrolling description needs every row the rest of the frame leaves, so the
        // note under the title gives way. A roomier window keeps it.
        const string note = "Installed skills aren't listed.";
        const string title = "Which skills should be installed? (App.slnx)";
        var items = new[]
        {
            new SkillPickerItem("skill-01", "P", "1", "short"),
            new SkillPickerItem("skill-02", "P", "1", string.Join(" ", Enumerable.Repeat("word", 200))),
        };

        var small = PickerLayout.For(items, title, PickerMode.Install, 46, 18, true, note);
        var roomy = PickerLayout.For(items, title, PickerMode.Install, 100, 30, true, note);

        Assert.DoesNotContain(note, small.Header(0));
        Assert.True(small.MaxFrameHeight < 18);
        Assert.Contains(note, roomy.Header(0));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 20)]
    [InlineData(80, 4)]
    public void Impossible_viewports_are_rejected_with_resize_guidance(int width, int height)
    {
        var error = Assert.Throws<PackageSkillsException>(() => Layout(width, height, [1, 2]));

        Assert.Contains("Enlarge the window", error.Message);
        Assert.Contains($"{width}x{height}", error.Message);
    }

    private static PickerLayout Layout(int width, int height, int[] rows) => PickerLayout.For(
        rows.Select((count, index) => new SkillPickerItem(
            $"skill-{index + 1:00}", "P", "1",
            string.Join("\n", Enumerable.Range(1, count).Select(line => $"line {line:00}")))).ToArray(),
        "Skills", PickerMode.Install, width, height, true);
}
