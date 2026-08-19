using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class SkillPickerTests
{
    private const string Title = "Skills for App.slnx";

    [Fact]
    public void Picker_shows_one_page_of_skills_at_a_time()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        var frame = terminal.Frames[0];
        Assert.Contains("skill-01", frame);
        Assert.Contains("skill-10", frame);
        Assert.DoesNotContain("skill-11", frame);
        Assert.Contains("page 1 of 3", frame);
    }

    [Fact]
    public void Already_installed_skills_start_selected_and_are_labelled()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(3, installed: [2]), Title);

        var frame = terminal.Frames[0];
        Assert.Contains("[ ] skill-01", frame);
        Assert.Contains("[x] skill-02", frame);
        Assert.Contains("installed", frame);
        Assert.Contains("new", frame);
    }

    [Fact]
    public void Pressing_enter_immediately_keeps_exactly_what_is_installed()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        var choice = new SkillPicker(terminal).Choose(Items(5, installed: [2, 4]), Title);

        Assert.NotNull(choice);
        Assert.Equal(["skill-02", "skill-04"], choice.Selected.Select(skill => skill.RelativePath));
        Assert.Empty(choice.Deselected);
    }

    [Fact]
    public void Moving_past_the_last_item_on_a_page_shows_the_next_page()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.DownArrow, times: 10).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        // Frame 0 is the opening page; frame 10 is what the tenth press produced.
        var frame = terminal.Frames[10];
        Assert.Contains("page 2 of 3", frame);
        Assert.Contains("> [ ] skill-11", frame);
        Assert.DoesNotContain("skill-10", frame);
    }

    [Fact]
    public void Moving_up_from_the_first_skill_wraps_to_the_last_page()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.UpArrow).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        var frame = terminal.Frames[1];
        Assert.Contains("page 3 of 3", frame);
        Assert.Contains("> [ ] skill-24", frame);
    }

    [Fact]
    public void Right_arrow_pages_forward_without_moving_within_the_page()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.RightArrow).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        Assert.Contains("> [ ] skill-11", terminal.Frames[1]);
    }

    [Fact]
    public void Space_selects_the_focused_skill()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, ConsoleKey.Enter);

        var choice = new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.NotNull(choice);
        Assert.Equal("skill-01", Assert.Single(choice.Selected).RelativePath);
    }

    [Fact]
    public void Turning_off_an_installed_skill_reports_it_for_removal()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, ConsoleKey.Enter);

        var choice = new SkillPicker(terminal).Choose(Items(3, installed: [1]), Title);

        Assert.NotNull(choice);
        Assert.Empty(choice.Selected);
        Assert.Equal("skill-01", Assert.Single(choice.Deselected));
    }

    [Fact]
    public void A_selects_every_skill_on_every_page()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.A, ConsoleKey.Enter);

        var choice = new SkillPicker(terminal).Choose(Items(24), Title);

        Assert.NotNull(choice);
        Assert.Equal(24, choice.Selected.Count);
        Assert.Empty(choice.Deselected);
    }

    [Fact]
    public void C_clears_every_skill_and_marks_the_installed_ones_for_removal()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.C, ConsoleKey.Enter);

        var choice = new SkillPicker(terminal).Choose(Items(24, installed: [3, 17]), Title);

        Assert.NotNull(choice);
        Assert.Empty(choice.Selected);
        Assert.Equal(["skill-03", "skill-17"], choice.Deselected);
    }

    [Fact]
    public void The_summary_counts_selections_and_pending_removals()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(5, installed: [1, 2]), Title);

        Assert.Contains("2 of 5 selected", terminal.Frames[0]);
        Assert.DoesNotContain("to remove", terminal.Frames[0]);
        Assert.Contains("1 of 5 selected", terminal.Frames[1]);
        Assert.Contains("1 to remove", terminal.Frames[1]);
    }

    [Fact]
    public void Escape_cancels_without_choosing_anything()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.A, ConsoleKey.Escape);

        Assert.Null(new SkillPicker(terminal).Choose(Items(3), Title));
    }

    [Fact]
    public void Q_cancels_without_choosing_anything()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Q);

        Assert.Null(new SkillPicker(terminal).Choose(Items(3), Title));
    }

    [Fact]
    public void A_short_window_shrinks_the_page_rather_than_overflowing_it()
    {
        var terminal = new FakeTerminal(windowHeight: 12).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        var frame = terminal.Frames[0];
        Assert.Contains("skill-04", frame);
        Assert.DoesNotContain("skill-05", frame);
        Assert.Contains("page 1 of 6", frame);
    }

    [Fact]
    public void A_window_too_short_for_a_page_still_shows_one_skill()
    {
        var terminal = new FakeTerminal(windowHeight: 4).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.Contains("page 1 of 3", terminal.Frames[0]);
    }

    [Fact]
    public void The_frame_never_grows_beyond_the_rows_it_reserved()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.DownArrow, times: 30).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        // A page of ten plus seven rows of chrome, however far the cursor has travelled.
        Assert.All(terminal.Frames, frame => Assert.Equal(17, frame.Split(Environment.NewLine).Length));
    }

    [Fact]
    public void The_frame_stays_ascii_so_a_legacy_console_code_page_renders_all_of_it()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24, installed: [1]), Title);

        // Windows consoles default to an OEM code page that silently drops arrows and box
        // glyphs, so a legend built from them reads as gaps on the most common terminal.
        Assert.All(
            terminal.Frames[0].Replace(Environment.NewLine, string.Empty),
            character => Assert.InRange(character, ' ', '~'));
    }

    [Fact]
    public void Long_skill_names_are_truncated_rather_than_wrapped()
    {
        var terminal = new FakeTerminal(windowWidth: 40).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(2), Title);

        Assert.All(
            terminal.Frames[0].Split(Environment.NewLine),
            line => Assert.True(line.Length < 40, $"'{line}' is {line.Length} characters wide"));
    }

    [Fact]
    public void The_cursor_is_put_back_when_the_picker_leaves()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Escape);

        new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.True(terminal.IsCursorVisible);
    }

    [Fact]
    public void A_redirected_terminal_is_refused_with_guidance()
    {
        var terminal = new FakeTerminal { IsRedirected = true };

        var error = Assert.Throws<PackageSkillsException>(
            () => new SkillPicker(terminal).Choose(Items(3), Title));

        Assert.Contains("--interactive needs a terminal", error.Message);
        Assert.Contains("--package", error.Message);
    }

    [Fact]
    public void Nothing_to_choose_between_never_prompts()
    {
        var terminal = new FakeTerminal { IsRedirected = true };

        var choice = new SkillPicker(terminal).Choose([], Title);

        Assert.NotNull(choice);
        Assert.Empty(choice.Selected);
        Assert.Empty(terminal.Frames);
    }

    private static IReadOnlyList<SkillPickerItem> Items(int count, params int[] installed) =>
    [
        .. Enumerable.Range(1, count).Select(number =>
        {
            var name = $"skill-{number:00}";

            return new SkillPickerItem(
                new BundledSkill(
                    $"Package.{number}",
                    "1.0.0",
                    name,
                    $"/packages/package.{number}/1.0.0/skills/{name}",
                    name),
                installed.Contains(number));
        }),
    ];
}
