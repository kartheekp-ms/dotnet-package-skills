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
    public void Moving_down_from_the_last_skill_wraps_to_the_first_page()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.End, ConsoleKey.DownArrow, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        var frame = terminal.Frames[2];
        Assert.Contains("page 1 of 3", frame);
        Assert.Contains("> [ ] skill-01", frame);
    }

    [Fact]
    public void Left_arrow_pages_back()
    {
        var terminal = new FakeTerminal()
            .Press(ConsoleKey.RightArrow, ConsoleKey.RightArrow, ConsoleKey.LeftArrow, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        Assert.Contains("page 3 of 3", terminal.Frames[2]);

        var frame = terminal.Frames[3];
        Assert.Contains("page 2 of 3", frame);
        Assert.Contains("> [ ] skill-11", frame);
    }

    [Fact]
    public void Page_up_and_page_down_page_like_the_arrows()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.PageDown, ConsoleKey.PageUp, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        Assert.Contains("page 2 of 3", terminal.Frames[1]);
        Assert.Contains("page 1 of 3", terminal.Frames[2]);
    }

    [Fact]
    public void Home_and_end_jump_to_the_first_and_last_skill()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.End, ConsoleKey.Home, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        Assert.Contains("> [ ] skill-24", terminal.Frames[1]);
        Assert.Contains("> [ ] skill-01", terminal.Frames[2]);
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
    public void Ctrl_c_cancels_without_choosing_anything()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.A).PressWith(ConsoleModifiers.Control, ConsoleKey.C);

        Assert.Null(new SkillPicker(terminal).Choose(Items(24), Title));
    }

    [Fact]
    public void Ctrl_c_is_not_mistaken_for_the_clear_all_key()
    {
        var terminal = new FakeTerminal().PressWith(ConsoleModifiers.Control, ConsoleKey.C);

        // A bare 'c' clears the selection and keeps going, so the modifier has to win.
        Assert.Null(new SkillPicker(terminal).Choose(Items(5, installed: [1, 2]), Title));
    }

    [Fact]
    public void Ctrl_c_restores_the_terminal_on_the_way_out()
    {
        var terminal = new FakeTerminal().PressWith(ConsoleModifiers.Control, ConsoleKey.C);

        new SkillPicker(terminal).Choose(Items(24), Title);

        Assert.True(terminal.IsCursorVisible);
        Assert.False(terminal.IsControlCTakenAsInput);
    }

    [Fact]
    public void Ctrl_c_is_taken_as_a_key_rather_than_killing_the_process()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Escape);

        new SkillPicker(terminal).Choose(Items(3), Title);

        // Left to the runtime, Ctrl+C ends the process mid-frame and the cursor is never
        // put back. Capturing it is what makes the restore reachable at all.
        Assert.True(terminal.ControlCWasEverTakenAsInput);
        Assert.False(terminal.IsControlCTakenAsInput);
    }

    [Fact]
    public void Cancelling_on_a_partial_page_leaves_the_cursor_under_the_summary()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.RightArrow);
        terminal.PressWith(ConsoleModifiers.Control, ConsoleKey.C);

        new SkillPicker(terminal).Choose(Items(12), Title);

        // Two skills plus seven rows of chrome, then the blank line Close writes. Landing
        // any lower is the gap of empty rows a shell prompt used to be pushed into.
        Assert.Equal(10, terminal.FinalCursorTop);
    }

    [Fact]
    public void A_partial_page_parks_the_cursor_under_the_summary_while_it_waits()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.RightArrow, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(12), Title);

        // Where the cursor rests between keys is where a prompt lands if the process is
        // killed outright, which is what Ctrl+C does on a host that will not hand it over.
        // Leaving it at the bottom of the reserved rows is the whitespace bug itself.
        Assert.Equal(9, terminal.CursorTopAwaitingKey);
    }

    [Fact]
    public void A_full_page_parks_the_cursor_under_the_summary_while_it_waits()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(12), Title);

        Assert.Equal(17, terminal.CursorTopAwaitingKey);
    }

    [Fact]
    public void Cancelling_on_a_full_page_leaves_the_cursor_under_the_summary()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Escape);

        new SkillPicker(terminal).Choose(Items(12), Title);

        Assert.Equal(18, terminal.FinalCursorTop);
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
    public void A_tall_window_shows_more_skills_per_page()
    {
        var terminal = new FakeTerminal(windowHeight: 40).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        // 40 rows less seven of chrome and one held back leaves 32, so 24 skills fit at once.
        var frame = terminal.Frames[0];
        Assert.Contains("skill-24", frame);
        Assert.DoesNotContain("page 1 of", frame);
    }

    [Fact]
    public void A_window_taller_than_the_list_does_not_page_at_all()
    {
        var terminal = new FakeTerminal(windowHeight: 40).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        // Nothing to page to, so the paging key is dropped along with the counter.
        Assert.DoesNotContain("change page", terminal.Frames[0]);
    }

    [Fact]
    public void The_page_is_the_window_height_not_a_fixed_ceiling()
    {
        // Twenty-eight rows leaves room for twenty skills. A hardcoded ten would have paged
        // this three times over and hidden two thirds of the list behind a keypress.
        var terminal = new FakeTerminal(windowHeight: 28).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(60), Title);

        var frame = terminal.Frames[0];
        Assert.Contains("skill-20", frame);
        Assert.DoesNotContain("skill-21", frame);
        Assert.Contains("page 1 of 3", frame);
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
    public void A_partial_last_page_puts_the_summary_under_its_final_skill()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.RightArrow, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(12), Title);

        var lines = terminal.Frames[1].Split(Environment.NewLine);
        var lastSkill = Array.FindIndex(lines, line => line.Contains("skill-12", StringComparison.Ordinal));
        var summary = Array.FindIndex(lines, line => line.Contains("of 12 selected", StringComparison.Ordinal));

        Assert.True(lastSkill > 0, "the last skill should be on the page");
        // One blank separator and nothing else: the two-skill page must not be padded out to ten.
        Assert.Equal(lastSkill + 2, summary);
    }

    [Fact]
    public void Paging_to_a_shorter_page_erases_what_the_taller_one_left_behind()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.RightArrow, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(12), Title);

        // In-place redrawing can only erase by overwriting, so page one's rows have to be
        // blanked rather than simply skipped.
        var frame = terminal.Frames[1];
        Assert.DoesNotContain("skill-01", frame);
        Assert.DoesNotContain("skill-10", frame);
        Assert.Contains("skill-11", frame);
        Assert.Contains("skill-12", frame);
    }

    [Fact]
    public void Paging_back_to_a_full_page_redraws_every_row()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.RightArrow, ConsoleKey.LeftArrow, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(12), Title);

        var frame = terminal.Frames[2];
        Assert.Contains("skill-01", frame);
        Assert.Contains("skill-10", frame);
        Assert.DoesNotContain("skill-11", frame);
    }

    [Fact]
    public void A_full_last_page_is_unchanged_by_the_partial_page_handling()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.End, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(20), Title);

        var lines = terminal.Frames[1].Split(Environment.NewLine);
        var lastSkill = Array.FindIndex(lines, line => line.Contains("skill-20", StringComparison.Ordinal));
        var summary = Array.FindIndex(lines, line => line.Contains("of 20 selected", StringComparison.Ordinal));

        Assert.Equal(lastSkill + 2, summary);
        Assert.Contains("page 2 of 2", terminal.Frames[1]);
    }

    [Fact]
    public void The_frame_is_only_as_wide_as_its_content_on_a_wide_terminal()
    {
        var terminal = new FakeTerminal(windowWidth: 200).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(3), Title);

        // Padding every row out to the window would strand the page counter at the far edge
        // and trail whitespace far past the text it belongs to.
        Assert.All(
            terminal.Frames[0].Split(Environment.NewLine),
            line => Assert.True(line.Length < 80, $"line is {line.Length} columns wide: '{line}'"));
    }

    [Fact]
    public void The_page_counter_sits_beside_the_title_not_at_the_far_edge()
    {
        var terminal = new FakeTerminal(windowWidth: 200).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        var header = terminal.Frames[0].Split(Environment.NewLine)[0];
        Assert.EndsWith("page 1 of 3", header.TrimEnd());
        Assert.True(header.TrimEnd().Length < 80, $"header is {header.TrimEnd().Length} columns wide");
    }

    [Fact]
    public void A_long_skill_name_still_widens_the_frame_to_fit()
    {
        var terminal = new FakeTerminal(windowWidth: 200).Press(ConsoleKey.Enter);
        var name = new string('x', 40);

        new SkillPicker(terminal).Choose(
            [
                new SkillPickerItem(
                    new BundledSkill("Some.Package", "1.0.0", name, $"/p/{name}", name),
                    Installed: false),
            ],
            Title);

        Assert.Contains(name, terminal.Frames[0]);
    }

    [Fact]
    public void Rows_are_padded_so_a_shorter_frame_cannot_leave_the_previous_one_behind()
    {
        // Selecting shrinks the summary from "N to remove" back to nothing; the padding is
        // what stops the old, longer text showing through.
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(3, installed: [1]), Title);

        var lengths = terminal.Frames[0].Split(Environment.NewLine).Select(line => line.Length).Distinct();
        Assert.Single(lengths);
    }

    [Fact]
    public void A_single_skill_does_not_leave_a_page_of_blank_rows_behind_it()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(1), Title);

        // One skill plus seven rows of chrome. Padding out to a full page is what stranded
        // the skill above most of a screen of nothing.
        Assert.Equal(8, terminal.Frames[0].Split(Environment.NewLine).Length);
    }

    [Fact]
    public void A_short_list_shrinks_the_frame_to_fit_it()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.Equal(10, terminal.Frames[0].Split(Environment.NewLine).Length);
    }

    [Fact]
    public void A_list_longer_than_a_page_still_fills_the_page()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        Assert.Equal(17, terminal.Frames[0].Split(Environment.NewLine).Length);
    }

    [Fact]
    public void One_page_of_skills_shows_no_page_counter()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(4), Title);

        var frame = terminal.Frames[0];
        Assert.Contains(Title, frame);
        Assert.DoesNotContain("page 1 of 1", frame);
        // Nothing to page to, so offering the key would teach a control that does nothing.
        Assert.DoesNotContain("change page", frame);
        Assert.Contains("move (up/down)", frame);
    }

    [Fact]
    public void A_single_skill_offers_neither_paging_nor_movement()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(1), Title);

        var frame = terminal.Frames[0];
        Assert.DoesNotContain("move (up/down)", frame);
        Assert.DoesNotContain("change page", frame);
        // With one skill, select-all and clear-all are a slower way to press space.
        Assert.DoesNotContain("select all", frame);
        Assert.DoesNotContain("clear all", frame);
        Assert.Contains("toggle selection (space)", frame);
        Assert.Contains("confirm (enter)", frame);
        Assert.Contains("cancel (esc)", frame);
    }

    [Fact]
    public void The_legend_names_the_action_before_the_key()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        // "space toggle" only reads to someone already told what it means. The reader is
        // asking what they can do here, so the answer comes first and the key follows.
        var frame = terminal.Frames[0];
        Assert.Contains("move (up/down)", frame);
        Assert.Contains("change page (left/right)", frame);
        Assert.Contains("toggle selection (space)", frame);
        Assert.Contains("select all (a)", frame);
        Assert.Contains("clear all (c)", frame);
        Assert.Contains("confirm (enter)", frame);
        Assert.Contains("cancel (esc)", frame);
    }

    [Fact]
    public void More_than_one_page_still_shows_the_counter_and_the_paging_key()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        var frame = terminal.Frames[0];
        Assert.Contains("page 1 of 3", frame);
        Assert.Contains("change page (left/right)", frame);
    }

    [Fact]
    public void A_single_skill_can_still_be_toggled_and_confirmed()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, ConsoleKey.Enter);

        var choice = new SkillPicker(terminal).Choose(Items(1, installed: [1]), Title);

        Assert.NotNull(choice);
        Assert.Empty(choice.Selected);
        Assert.Equal("skill-01", Assert.Single(choice.Deselected));
    }

    [Fact]
    public void Moving_within_a_single_page_never_leaves_it()
    {
        var terminal = new FakeTerminal()
            .Press(ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.RightArrow, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.All(
            terminal.Frames,
            frame => Assert.Equal(10, frame.Split(Environment.NewLine).Length));
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
