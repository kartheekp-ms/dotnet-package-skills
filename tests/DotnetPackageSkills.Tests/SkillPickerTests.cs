using System.Text;
using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class SkillPickerTests
{
    private const string Title = "Skills for App.slnx";

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(8, true, false)]
    [InlineData(58, true, false)]
    [InlineData(8, false, false)]
    [InlineData(0, true, true)]
    [InlineData(8, true, true)]
    [InlineData(58, true, true)]
    [InlineData(8, false, true)]
    public void The_first_frame_starts_at_the_top_regardless_of_the_shell_cursor(
        int cursorRow, bool color, bool uninstall)
    {
        var terminal = new FakeTerminal(windowHeight: 70, windowWidth: 140)
        {
            SupportsColor = color,
        }.Press(ConsoleKey.Escape);
        terminal.WriteLine("previous shell output");
        terminal.SetCursorPosition(12, cursorRow);
        var originalScreen = terminal.Screen;
        var originalState = terminal.CaptureState();
        terminal.BeforeOperation = operation =>
        {
            if (operation == nameof(FakeTerminal.ClearViewport))
            {
                Assert.True(terminal.IsInteractiveScreen);
            }
        };

        new SkillPicker(terminal).Choose(Items(3), Title,
            uninstall ? PickerMode.Uninstall : PickerMode.Install);

        Assert.StartsWith(Title, Assert.Single(terminal.Frames));
        Assert.Equal(0, Assert.Single(terminal.Writes, write => write.Text == Title).Top);
        Assert.Equal(originalScreen, terminal.Screen);
        Assert.Equal(cursorRow, terminal.FinalCursorTop);
        Assert.Equal(originalState, terminal.CaptureState());
        Assert.Equal(1, terminal.ViewportClears);
    }

    [Theory]
    [InlineData(ConsoleKey.Enter)]
    [InlineData(ConsoleKey.Escape)]
    [InlineData(ConsoleKey.Q)]
    public void Picker_frames_are_isolated_from_prior_shell_output_and_removed_on_exit(ConsoleKey exit)
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, exit);
        terminal.WriteLine("earlier shell output");
        var before = terminal.Screen;
        var cursor = terminal.CursorTop;
        var state = terminal.CaptureState();
        terminal.BeforeOperation = operation =>
        {
            if (operation == nameof(FakeTerminal.ReadKey))
            {
                Assert.True(terminal.IsInteractiveScreen);
                Assert.DoesNotContain("earlier shell output", terminal.Screen);
            }
        };

        new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.Equal(before, terminal.Screen);
        Assert.Equal(cursor, terminal.CursorTop);
        Assert.Equal(state, terminal.CaptureState());
        Assert.Contains(Title, terminal.LastPickerScreen);
        Assert.DoesNotContain(Title, terminal.Screen);
        Assert.Equal(1, terminal.ScreenEntries);
        Assert.Equal(1, terminal.ScreenExits);
        Assert.False(terminal.IsInteractiveScreen);
    }

    [Fact]
    public void Ctrl_C_restores_the_normal_screen_without_retaining_a_picker_copy()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.A).PressWith(ConsoleModifiers.Control, ConsoleKey.C);
        terminal.WriteLine("prior output");
        var before = terminal.Screen;

        Assert.Null(new SkillPicker(terminal).Choose(Items(24), Title));

        Assert.Equal(before, terminal.Screen);
        Assert.False(terminal.IsInteractiveScreen);
        Assert.Equal(1, terminal.ScreenExits);
    }

    [Fact]
    public void Rendering_failure_still_restores_the_original_screen()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar);
        terminal.WriteLine("prior output");
        var before = terminal.Screen;
        terminal.BeforeOperation = operation =>
        {
            if (operation == nameof(FakeTerminal.Write) && terminal.CurrentStyle == TerminalStyle.Selected)
            {
                throw new IOException("failed during selection render");
            }
        };

        Assert.Throws<IOException>(() => new SkillPicker(terminal).Choose(Items(3), Title));

        Assert.Equal(before, terminal.Screen);
        Assert.False(terminal.IsInteractiveScreen);
        Assert.Equal(1, terminal.ScreenExits);
    }

    [Fact]
    public void A_note_under_the_title_is_shown_on_every_page_and_fits_the_frame()
    {
        const string Note = "Installed skills aren't listed.";
        var terminal = new FakeTerminal(windowHeight: 18, windowWidth: 46)
            .Press(ConsoleKey.PageDown, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title, PickerMode.Install, Note);

        Assert.All(terminal.Frames, frame =>
        {
            var lines = frame.Split(Environment.NewLine).Select(line => line.TrimEnd()).ToArray();
            Assert.StartsWith(Title, lines[0]);
            Assert.Equal(Note, lines[1]);
            Assert.Equal(string.Empty, lines[2]);
        });
        Assert.Contains("page 2 of", terminal.Frames[1]);
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void Picker_shows_one_page_of_skills_at_a_time()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        var frame = terminal.Frames[0];
        Assert.Contains("skill-01", frame);
        Assert.Contains("skill-08", frame);
        Assert.DoesNotContain("skill-09", frame);
        Assert.Contains("page 1 of 3", frame);
    }

    [Fact]
    public void An_install_list_starts_with_nothing_ticked()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        var choice = new SkillPicker(terminal).Choose(Items(3), Title);

        var frame = terminal.Frames[0];
        Assert.Contains("[ ] skill-01", frame);
        Assert.Contains("[ ] skill-02", frame);
        Assert.Contains("[ ] skill-03", frame);
        Assert.DoesNotContain("[X]", frame);
        Assert.DoesNotContain("installed", frame);
        Assert.Empty(choice!);
        Assert.Equal(TerminalStyle.Focus, SkillSpan(terminal, 0, 1).Style);
        Assert.Equal(TerminalStyle.Default, SkillSpan(terminal, 0, 2).Style);
    }

    [Fact]
    public void Neutral_rows_show_the_description_instead_of_a_status_column()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(4), Title);

        var rows = Rows(terminal.Frames[0]);

        Assert.Equal("[ ] skill-01 - No description provided.", rows[0]);
        Assert.Equal("[ ] skill-02 - No description provided.", rows[1]);
    }

    [Fact]
    public void Ticking_a_new_skill_colors_only_its_X_blue_when_it_is_not_focused()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, ConsoleKey.DownArrow, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.Equal(TerminalStyle.Focus, SkillSpan(terminal, 0, 1).Style);
        Assert.Equal(TerminalStyle.Selected, CheckboxSpan(terminal, 2, 1, "X").Style);
        Assert.Equal(TerminalStyle.Default, CheckboxSpan(terminal, 2, 1, "[").Style);
        Assert.Equal(TerminalStyle.Default, CheckboxSpan(terminal, 2, 1, "]").Style);
        Assert.Equal(TerminalStyle.Default, SkillSpan(terminal, 2, 1).Style);
        Assert.Equal("[X] skill-01 - No description provided.", Rows(terminal.Frames[1])[0]);
        Assert.DoesNotContain("will install", terminal.Frames[1]);
    }

    [Fact]
    public void An_install_list_never_marks_a_row_for_removal()
    {
        var terminal = new FakeTerminal()
            .Press(ConsoleKey.Spacebar, ConsoleKey.Spacebar, ConsoleKey.A, ConsoleKey.C, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.All(terminal.FrameWrites, writes =>
            Assert.DoesNotContain(writes, write => write.Style == TerminalStyle.Remove));
        Assert.All(terminal.Frames, frame =>
            Assert.DoesNotContain("remove", frame, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Package_attribution_does_not_appear_after_the_authored_skill_name()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.Contains("skill-01 - No description provided.", terminal.Frames[0]);
        Assert.DoesNotContain("Package.1", terminal.Frames[0]);
        Assert.DoesNotContain("1.0.0", terminal.Frames[0]);
    }

    /// <summary>The skill rows of a frame, trimmed of the cursor column and padding.</summary>
    private static List<string> Rows(string frame) =>
    [
        .. frame.Split(Environment.NewLine)
            .Where(line => line.Contains('[', StringComparison.Ordinal))
            .Select(line => line[line.IndexOf('[')..].TrimEnd()),
    ];

    private static TerminalWrite SkillSpan(FakeTerminal terminal, int frame, int skill) =>
        Assert.Single(terminal.FrameWrites[frame], write =>
            write.Text == $" skill-{skill:00}");

    private static TerminalWrite CheckboxSpan(FakeTerminal terminal, int frame, int skill, string part)
    {
        var row = SkillSpan(terminal, frame, skill).Top;
        return Assert.Single(terminal.FrameWrites[frame], write => write.Top == row && write.Text == part);
    }

    [Fact]
    public void Uninstalling_starts_with_nothing_ticked()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        // Every row is installed, and a tick deletes. Pre-ticking them would make a mistaken
        // enter wipe the lot.
        var choice = new SkillPicker(terminal)
            .Choose(Items(5), Title, PickerMode.Uninstall);

        Assert.NotNull(choice);
        Assert.Empty(choice);
    }

    [Fact]
    public void Uninstalling_marks_a_ticked_row_for_removal()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, ConsoleKey.Enter);

        var choice = new SkillPicker(terminal)
            .Choose(Items(3), Title, PickerMode.Uninstall);

        Assert.Equal("skill-01", Assert.Single(choice!));
        Assert.Equal("[X] skill-01 - No description provided.", Rows(terminal.Frames[1])[0]);
        Assert.Equal(TerminalStyle.Remove, CheckboxSpan(terminal, 1, 1, "[").Style);
        Assert.Equal(TerminalStyle.Remove, CheckboxSpan(terminal, 1, 1, "]").Style);
        Assert.Equal(TerminalStyle.Selected, CheckboxSpan(terminal, 1, 1, "X").Style);
        Assert.Equal(TerminalStyle.Focus, SkillSpan(terminal, 1, 1).Style);
    }

    [Fact]
    public void Uninstalling_says_nothing_about_rows_left_alone()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(3), Title, PickerMode.Uninstall);

        var frame = terminal.Frames[0];
        Assert.DoesNotContain("installed", frame);
        Assert.DoesNotContain("will install", frame);
        Assert.Equal("[ ] skill-01 - No description provided.", Rows(frame)[0]);
        Assert.Equal(TerminalStyle.Focus, SkillSpan(terminal, 0, 1).Style);
    }

    [Fact]
    public void Uninstalling_counts_selected_removals_without_an_install_count()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(4), Title, PickerMode.Uninstall);

        Assert.Contains("0 of 4 selected; 0 to remove", terminal.Frames[0]);
        Assert.Contains("1 of 4 selected; 1 to remove", terminal.Frames[1]);
        Assert.DoesNotContain("to install", terminal.Frames[1]);
    }

    [Fact]
    public void Uninstalling_can_take_everything_with_one_key()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.A, ConsoleKey.Enter);

        var choice = new SkillPicker(terminal)
            .Choose(Items(24), Title, PickerMode.Uninstall);

        Assert.Equal(24, choice!.Count);
    }

    [Fact]
    public void Uninstalling_cancelled_removes_nothing()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.A, ConsoleKey.Escape);

        Assert.Null(new SkillPicker(terminal)
            .Choose(Items(5), Title, PickerMode.Uninstall));
    }

    [Fact]
    public void Uninstalling_without_a_terminal_says_what_to_do_instead()
    {
        var terminal = new FakeTerminal { IsRedirected = true };

        var error = Assert.Throws<PackageSkillsException>(
            () => new SkillPicker(terminal).Choose(Items(3), Title, PickerMode.Uninstall));

        // The install wording tells you to drop the flag and install everything, which is the
        // opposite of what this command would then do. It also can't point at --package, which
        // uninstall --stale refuses.
        Assert.Contains("remove every skill that the command matches", error.Message);
        Assert.DoesNotContain("--package", error.Message);
    }

    [Fact]
    public void Pressing_enter_immediately_installs_nothing()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        var choice = new SkillPicker(terminal).Choose(Items(5), Title);

        Assert.NotNull(choice);
        Assert.Empty(choice);
    }

    [Fact]
    public void Moving_past_the_last_item_on_a_page_shows_the_next_page()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.DownArrow, times: 8).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        var frame = terminal.Frames[8];
        Assert.Contains("page 2 of 3", frame);
        Assert.Contains("> [ ] skill-09", frame);
        Assert.DoesNotContain("skill-08", frame);
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

        Assert.Contains("> [ ] skill-09", terminal.Frames[1]);
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
        Assert.Contains("> [ ] skill-09", frame);
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
        Assert.Equal("skill-01", Assert.Single(choice));
    }

    [Fact]
    public void Pressing_space_again_unticks_the_skill()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, ConsoleKey.Spacebar, ConsoleKey.Enter);

        var choice = new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.NotNull(choice);
        Assert.Empty(choice);
    }

    [Fact]
    public void A_selects_every_skill_on_every_page()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.A, ConsoleKey.Enter);

        var choice = new SkillPicker(terminal).Choose(Items(24), Title);

        Assert.NotNull(choice);
        Assert.Equal(24, choice.Count);
    }

    [Fact]
    public void C_clears_every_skill_on_every_page()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.A, ConsoleKey.C, ConsoleKey.Enter);

        var choice = new SkillPicker(terminal).Choose(Items(24), Title);

        Assert.NotNull(choice);
        Assert.Empty(choice);
    }

    [Fact]
    public void The_install_summary_counts_selections_only()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(5), Title);

        Assert.Contains("0 of 5 selected", terminal.Frames[0]);
        Assert.Contains("1 of 5 selected", terminal.Frames[1]);
        Assert.DoesNotContain("to install", terminal.Frames[1]);
        Assert.DoesNotContain("to remove", terminal.Frames[1]);
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
        Assert.Null(new SkillPicker(terminal).Choose(Items(5), Title));
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
    public void Cancelling_on_a_partial_page_parks_the_picker_then_restores_the_shell_cursor()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.RightArrow);
        terminal.PressWith(ConsoleModifiers.Control, ConsoleKey.C);

        new SkillPicker(terminal).Choose(Items(12), Title);

        // Four complete entries, nine measured chrome rows, and the closing blank line.
        Assert.Equal(14, terminal.LastPickerCursorTop);
        Assert.Equal(0, terminal.FinalCursorTop);
    }

    [Fact]
    public void A_partial_page_parks_the_cursor_under_the_footer_while_it_waits()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.RightArrow, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(12), Title);

        // Where the cursor rests between keys is where a prompt lands if the process is
        // killed outright, which is what Ctrl+C does on a host that will not hand it over.
        // Leaving it at the bottom of the reserved rows is the whitespace bug itself.
        Assert.Equal(13, terminal.CursorTopAwaitingKey);
    }

    [Fact]
    public void A_full_page_parks_the_cursor_under_the_footer_while_it_waits()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(12), Title);

        Assert.Equal(17, terminal.CursorTopAwaitingKey);
    }

    [Fact]
    public void Cancelling_on_a_full_page_restores_the_original_screen_cursor()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Escape);

        new SkillPicker(terminal).Choose(Items(12), Title);

        Assert.Equal(17, terminal.LastPickerCursorTop);
        Assert.Equal(0, terminal.FinalCursorTop);
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

        // The entire list and its measured, non-paging footer fit in this viewport.
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
        // Eighteen entries fit beside the measured footer. A fixed ten-row cap would hide
        // eight entries that fit, even before descriptions change their rendered heights.
        var terminal = new FakeTerminal(windowHeight: 28).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(60), Title);

        var frame = terminal.Frames[0];
        Assert.Contains("skill-18", frame);
        Assert.DoesNotContain("skill-19", frame);
        Assert.Contains("page 1 of 4", frame);
    }

    [Fact]
    public void A_short_window_shrinks_the_page_rather_than_overflowing_it()
    {
        var terminal = new FakeTerminal(windowHeight: 12).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        var frame = terminal.Frames[0];
        Assert.Contains("skill-02", frame);
        Assert.DoesNotContain("skill-03", frame);
        Assert.Contains("page 1 of 12", frame);
    }

    [Fact]
    public void A_window_too_short_for_essential_controls_gives_actionable_guidance()
    {
        var terminal = new FakeTerminal(windowHeight: 4).Press(ConsoleKey.Enter);

        var error = Assert.Throws<PackageSkillsException>(
            () => new SkillPicker(terminal).Choose(Items(3), Title));

        Assert.Contains("too small", error.Message);
        Assert.Contains("Enlarge the window", error.Message);
        Assert.Contains("--package", error.Message);
        Assert.Empty(terminal.Frames);
        Assert.Empty(terminal.Writes);
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
        // A blank separator, not padding out to a full page.
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
        Assert.DoesNotContain("skill-08", frame);
        Assert.Contains("skill-09", frame);
        Assert.Contains("skill-12", frame);
    }

    [Fact]
    public void Paging_back_to_a_full_page_redraws_every_row()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.RightArrow, ConsoleKey.LeftArrow, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(12), Title);

        var frame = terminal.Frames[2];
        Assert.Contains("skill-01", frame);
        Assert.Contains("skill-08", frame);
        Assert.DoesNotContain("skill-09", frame);
    }

    [Fact]
    public void A_full_last_page_is_unchanged_by_the_partial_page_handling()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.End, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(16), Title);

        var lines = terminal.Frames[1].Split(Environment.NewLine);
        var lastSkill = Array.FindIndex(lines, line => line.Contains("skill-16", StringComparison.Ordinal));
        var summary = Array.FindIndex(lines, line => line.Contains("of 16 selected", StringComparison.Ordinal));

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
                new SkillPickerItem(name, "Some.Package", "1.0.0"),
            ],
            Title);

        Assert.Contains(name, terminal.Frames[0]);
    }

    [Fact]
    public void A_name_far_longer_than_any_constant_survives_on_a_wide_terminal()
    {
        var terminal = new FakeTerminal(windowWidth: 200).Press(ConsoleKey.Enter);

        // The column used to stop at a hardcoded 44, so this lost its tail with most of the
        // window still empty beside it. The terminal is the only thing that gets to decide.
        var name = "contoso.widgets-extremely-long-skill-name-that-keeps-going-and-going";

        new SkillPicker(terminal).Choose(
            [
                new SkillPickerItem(name, "Contoso.Widgets", "2.3.0"),
            ],
            Title);

        Assert.Contains(name, terminal.Frames[0]);
        Assert.DoesNotContain("...", terminal.Frames[0]);
    }

    [Fact]
    public void A_narrow_terminal_truncates_the_name_rather_than_overflowing_the_row()
    {
        var terminal = new FakeTerminal(windowWidth: 50).Press(ConsoleKey.Enter);
        var name = "contoso.widgets-extremely-long-skill-name-that-keeps-going";

        new SkillPicker(terminal).Choose(
            [
                new SkillPickerItem(name, "Contoso.Widgets", "2.3.0"),
            ],
            Title);

        var frame = terminal.Frames[0];
        Assert.Contains("...", frame);
        Assert.All(
            frame.Split(Environment.NewLine),
            line => Assert.True(line.Length < 50, $"'{line}' is {line.Length} columns wide"));
    }

    [Fact]
    public void The_name_column_grows_with_the_terminal()
    {
        const string Name = "contoso.widgets-a-name-of-some-considerable-length-indeed";

        static int DescriptionColumnAt(int windowWidth)
        {
            var terminal = new FakeTerminal(windowWidth: windowWidth).Press(ConsoleKey.Enter);

            new SkillPicker(terminal).Choose(
                [
                    new SkillPickerItem(Name, "Contoso.Widgets", "2.3.0"),
                ],
                Title);

            return terminal.Frames[0]
                .Split(Environment.NewLine)
                .Single(line => line.StartsWith("> [", StringComparison.Ordinal))
                .IndexOf(" - ", StringComparison.Ordinal);
        }

        Assert.True(DescriptionColumnAt(140) > DescriptionColumnAt(70), "a wider terminal should give the name more room");
    }

    [Fact]
    public void Rows_are_padded_so_a_shorter_frame_cannot_leave_the_previous_one_behind()
    {
        // Changing counts or focus must not leave an earlier row showing through.
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(3), Title);

        var lengths = terminal.Frames[0].Split(Environment.NewLine).Select(line => line.Length).Distinct();
        Assert.Single(lengths);
    }

    [Fact]
    public void A_single_skill_does_not_leave_a_page_of_blank_rows_behind_it()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(1), Title);

        // A single entry and its useful footer, not a page's worth of reserved skill rows.
        Assert.Equal(8, terminal.Frames[0].Split(Environment.NewLine).Length);
    }

    [Fact]
    public void A_short_list_shrinks_the_frame_to_fit_it()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.Equal(11, terminal.Frames[0].Split(Environment.NewLine).Length);
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
        Assert.Contains("<up>/<down> to move", frame);
    }

    [Fact]
    public void A_single_skill_offers_neither_paging_nor_movement()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(1), Title);

        var frame = terminal.Frames[0];
        Assert.DoesNotContain("<up>/<down>", frame);
        Assert.DoesNotContain("change page", frame);
        // With one skill, select-all and clear-all are a slower way to press space.
        Assert.DoesNotContain("select all", frame);
        Assert.DoesNotContain("clear all", frame);
        Assert.Contains(PickerLayout.PrimaryHelp, frame);
        Assert.Contains("(Press <Esc>/<q>/<Ctrl+C> to cancel)", frame);
    }

    [Fact]
    public void The_bottom_help_uses_Aspire_key_syntax_and_subdued_spans()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        var frame = terminal.Frames[0];
        Assert.Contains("(Press <space> to select, <enter> to accept)", frame);
        Assert.Contains("(Press <up>/<down> to move", frame);
        Assert.Contains("(Press <left>/<right>, <PageUp>/<PageDown> to change page)", frame);
        Assert.Contains("<Home>/<End> for first/last", frame);
        Assert.Contains("(Press <a> to select all", frame);
        Assert.Contains("<c> to clear all", frame);
        Assert.Contains("<Esc>/<q>/<Ctrl+C> to cancel", frame);
        Assert.All(
            terminal.FrameWrites[0].Where(write => write.Text.StartsWith("(", StringComparison.Ordinal)),
            write => Assert.StartsWith("(Press <", write.Text));
        var help = Assert.Single(terminal.FrameWrites[0], write => write.Text == PickerLayout.PrimaryHelp);
        Assert.Equal(TerminalStyle.Muted, help.Style);
        Assert.True(help.Top > SkillSpan(terminal, 0, 8).Top);
    }

    [Fact]
    public void More_than_one_page_still_shows_the_counter_and_the_paging_key()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        var frame = terminal.Frames[0];
        Assert.Contains("page 1 of 3", frame);
        Assert.Contains("(Press <left>/<right>, <PageUp>/<PageDown> to change page)", frame);
    }

    [Fact]
    public void A_single_skill_can_still_be_toggled_and_confirmed()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, ConsoleKey.Enter);

        var choice = new SkillPicker(terminal).Choose(Items(1), Title);

        Assert.NotNull(choice);
        Assert.Equal("skill-01", Assert.Single(choice));
    }

    [Fact]
    public void Moving_within_a_single_page_never_leaves_it()
    {
        var terminal = new FakeTerminal()
            .Press(ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.RightArrow, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.All(
            terminal.Frames,
            frame => Assert.Equal(11, frame.Split(Environment.NewLine).Length));
        Assert.Contains("> [ ] skill-03", terminal.Frames[^1]);
    }

    [Fact]
    public void The_frame_never_grows_beyond_the_rows_it_reserved()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.DownArrow, times: 30).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

        // Eight entry rows and nine measured chrome rows, however far the cursor travels.
        Assert.All(terminal.Frames, frame => Assert.Equal(17, frame.Split(Environment.NewLine).Length));
    }

    [Fact]
    public void The_frame_stays_ascii_so_a_legacy_console_code_page_renders_all_of_it()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(24), Title);

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
        Assert.Empty(choice);
        Assert.Empty(terminal.Frames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_modes_put_the_description_immediately_after_the_skill_name(bool uninstall)
    {
        var terminal = new FakeTerminal(windowHeight: 24, windowWidth: 80).Press(ConsoleKey.Enter);
        var mode = uninstall ? PickerMode.Uninstall : PickerMode.Install;

        new SkillPicker(terminal).Choose(
            [new SkillPickerItem("alpha", "Pkg", "1.2.3", "Short description.")], Title, mode);

        var row = Assert.Single(Rows(terminal.Frames[0]));
        Assert.Equal("[ ] alpha - Short description.", row);
        var description = Assert.Single(terminal.FrameWrites[0], write => write.Text == " - Short description.");
        Assert.Equal(11, description.Left);
        Assert.Equal(TerminalStyle.Focus, description.Style);
        Assert.Equal(1, terminal.Frames[0].Split("Short description.", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("will install", row);
        Assert.DoesNotContain("will remove", row);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    [InlineData("\x1b[31m\x1b[0m\u202e")]
    [InlineData("\u200d")]
    public void Missing_or_invisible_description_text_has_an_explicit_fallback(string? description)
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, ConsoleKey.Enter);

        var chosen = new SkillPicker(terminal).Choose(
            [new SkillPickerItem("alpha", "Pkg", "1", description)], Title);

        Assert.Equal("alpha", Assert.Single(chosen!));
        Assert.Contains(" - No description provided.", terminal.Frames[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Description_warnings_are_visible_and_do_not_change_eligibility(bool uninstall)
    {
        var terminal = new FakeTerminal(windowWidth: 160).Press(ConsoleKey.Spacebar, ConsoleKey.Enter);
        var mode = uninstall ? PickerMode.Uninstall : PickerMode.Install;

        var chosen = new SkillPicker(terminal).Choose(
            [new SkillPickerItem("alpha", "Pkg", "1", "stale text", "cannot read SKILL.md.")],
            Title, mode);

        Assert.Equal("alpha", Assert.Single(chosen!));
        Assert.Contains(" - Description unavailable: cannot read SKILL.md.", terminal.Frames[0]);
        Assert.DoesNotContain("stale text", terminal.Frames[0]);
        Assert.DoesNotContain("No description provided.", terminal.Frames[0]);
    }

    [Fact]
    public void Descriptions_continue_at_the_skill_text_edge_without_a_name_sized_gap()
    {
        var terminal = new FakeTerminal(windowHeight: 18, windowWidth: 46).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(
            [new SkillPickerItem("alpha", "Pkg", "1.2.3",
                "One two three four five six seven eight nine ten.")], Title);

        var lines = terminal.Frames[0].Split(Environment.NewLine).Select(line => line.TrimEnd()).ToArray();
        Assert.Equal("> [ ] alpha - One two three four five six", lines[2]);
        Assert.Equal(new string(' ', 6) + "seven eight nine ten.", lines[3]);
        Assert.Equal(string.Empty, lines[4]);
        Assert.Equal("0 of 1 selected", lines[5]);
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void Descriptions_follow_differently_sized_names_without_padding()
    {
        var terminal = new FakeTerminal(windowHeight: 24, windowWidth: 80).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(
            [
                new SkillPickerItem("alpha", "Pkg", "1.2.3", "First."),
                new SkillPickerItem("beta", "Pkg", "1.2.3", "Second."),
            ],
            Title);

        var lines = terminal.Frames[0].Split(Environment.NewLine);
        Assert.Equal("> [ ] alpha - First.", lines[2].TrimEnd());
        Assert.Equal("  [ ] beta - Second.", lines[3].TrimEnd());
        var descriptions = terminal.FrameWrites[0].Where(write => write.Text.StartsWith(" - ", StringComparison.Ordinal)).ToArray();
        Assert.Equal([11, 10], descriptions.Select(write => write.Left));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Picker_names_keep_the_package_prefix_but_omit_the_metadata_suffix(bool uninstall, bool color)
    {
        var terminal = new FakeTerminal(windowHeight: 24, windowWidth: 100) { SupportsColor = color };
        terminal.Press(ConsoleKey.Spacebar, ConsoleKey.Enter);
        const string name = "contoso.widgets-batching";

        var chosen = new SkillPicker(terminal).Choose(
            [new SkillPickerItem(name, "Contoso.Widgets", "2.3.0", "Batching widget calls.")],
            Title,
            uninstall ? PickerMode.Uninstall : PickerMode.Install);

        Assert.Equal(name, Assert.Single(chosen!));
        Assert.All(terminal.Frames, frame =>
        {
            Assert.Contains($"{name} - Batching widget calls.", frame);
            Assert.DoesNotContain("(Contoso.Widgets 2.3.0)", frame);
        });
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void Help_wraps_instead_of_truncating_the_Aspire_prompt()
    {
        var terminal = new FakeTerminal(windowHeight: 24, windowWidth: 40).Press(ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(1), Title);

        var lines = terminal.Frames[0].Split(Environment.NewLine).Select(line => line.TrimEnd()).ToArray();
        var first = Array.FindIndex(lines, line => line.StartsWith("(Press ", StringComparison.Ordinal));
        Assert.Equal("(Press <space> to select, <enter> to", lines[first]);
        Assert.Equal("accept)", lines[first + 1]);
        Assert.Equal("(Press <space> to select, <enter> to accept)", $"{lines[first]} {lines[first + 1]}");
        Assert.Contains("(Press <Esc>/<q>/<Ctrl+C> to cancel)", string.Join(" ", lines));
        Assert.All(terminal.FrameWrites[0].Where(write => write.Top == first || write.Top == first + 1),
            write => Assert.True(string.IsNullOrWhiteSpace(write.Text) || write.Style == TerminalStyle.Muted));
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void Ticked_additions_are_counted_and_only_their_X_is_colored()
    {
        var terminal = new FakeTerminal()
            .Press(ConsoleKey.Spacebar, ConsoleKey.DownArrow, ConsoleKey.Spacebar, ConsoleKey.Enter);

        var chosen = new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.Equal(["skill-01", "skill-02"], chosen!.Order(StringComparer.Ordinal));
        Assert.Contains("2 of 3 selected", terminal.Frames[^1]);
        Assert.Equal(TerminalStyle.Default, SkillSpan(terminal, 3, 1).Style);
        Assert.Equal(TerminalStyle.Selected, CheckboxSpan(terminal, 3, 1, "X").Style);
        Assert.Equal(TerminalStyle.Default, CheckboxSpan(terminal, 3, 1, "[").Style);
        Assert.Equal(TerminalStyle.Selected, CheckboxSpan(terminal, 3, 2, "X").Style);
        Assert.Equal(TerminalStyle.Focus, CheckboxSpan(terminal, 3, 2, "[").Style);
        Assert.Equal(TerminalStyle.Focus, SkillSpan(terminal, 3, 2).Style);
        Assert.Equal(TerminalStyle.Default, SkillSpan(terminal, 3, 3).Style);
    }

    [Fact]
    public void Blue_focus_covers_the_row_but_removal_brackets_stay_red()
    {
        var terminal = new FakeTerminal()
            .Press(ConsoleKey.Spacebar, ConsoleKey.DownArrow, ConsoleKey.Spacebar, ConsoleKey.UpArrow, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(3), Title, PickerMode.Uninstall);

        Assert.Equal(TerminalStyle.Focus, SkillSpan(terminal, 1, 1).Style);
        Assert.Equal(TerminalStyle.Default, SkillSpan(terminal, 2, 1).Style);
        Assert.Equal(TerminalStyle.Remove, CheckboxSpan(terminal, 3, 2, "[").Style);
        Assert.Equal(TerminalStyle.Focus, SkillSpan(terminal, 4, 1).Style);
        Assert.Equal(TerminalStyle.Default, SkillSpan(terminal, 4, 2).Style);
        Assert.Equal(TerminalStyle.Remove, CheckboxSpan(terminal, 4, 2, "]").Style);
        Assert.All(terminal.FrameWrites, writes =>
        {
            var focus = Assert.Single(writes, write => write.Text == ">");
            Assert.Equal(TerminalStyle.Focus, focus.Style);
            Assert.Equal(0, focus.Left);
            Assert.All(writes.Where(write => write.Text.StartsWith(" - ", StringComparison.Ordinal)),
                write => Assert.Equal(write.Top == focus.Top ? TerminalStyle.Focus : TerminalStyle.Default, write.Style));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Focus_colors_every_wrapped_line_and_clears_from_the_previous_skill(bool uninstall)
    {
        var terminal = new FakeTerminal(windowHeight: 30, windowWidth: 80)
            .Press(ConsoleKey.DownArrow, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(
            [
                new SkillPickerItem("alpha", "Pkg", "1", "First line.\nSecond line.\nThird line."),
                new SkillPickerItem("beta", "Pkg", "1", "Another line.\nAnother continuation."),
            ],
            Title, uninstall ? PickerMode.Uninstall : PickerMode.Install);

        var alpha = Assert.Single(terminal.FrameWrites[0], write => write.Text == " alpha").Top;
        var beta = Assert.Single(terminal.FrameWrites[0], write => write.Text == " beta").Top;
        Assert.Equal(3, beta - alpha);
        for (var frame = 0; frame < 2; frame++)
        {
            Assert.All(
                terminal.FrameWrites[frame].Where(write => write.Top >= alpha && write.Top < beta &&
                    !string.IsNullOrWhiteSpace(write.Text)),
                write => Assert.Equal(frame == 0 ? TerminalStyle.Focus : TerminalStyle.Default, write.Style));
            Assert.All(
                terminal.FrameWrites[frame].Where(write => write.Top >= beta && write.Top < beta + 2 &&
                    !string.IsNullOrWhiteSpace(write.Text)),
                write => Assert.Equal(frame == 1 ? TerminalStyle.Focus : TerminalStyle.Default, write.Style));
        }

        AssertWithinWindow(terminal);
    }

    [Fact]
    public void Unticking_a_new_installation_returns_it_to_neutral()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, ConsoleKey.Spacebar, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.Equal(TerminalStyle.Selected, CheckboxSpan(terminal, 1, 1, "X").Style);
        Assert.DoesNotContain(terminal.FrameWrites[2], write => write.Text == "X");
        Assert.Equal(TerminalStyle.Focus, SkillSpan(terminal, 2, 1).Style);
        Assert.Contains("0 of 3 selected", terminal.Frames[2]);
    }

    [Fact]
    public void Unticking_an_uninstall_row_returns_it_to_neutral()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, ConsoleKey.Spacebar, ConsoleKey.Enter);

        var chosen = new SkillPicker(terminal).Choose(Items(3), Title, PickerMode.Uninstall);

        Assert.Empty(chosen!);
        Assert.Equal(TerminalStyle.Remove, CheckboxSpan(terminal, 1, 1, "[").Style);
        Assert.Equal(TerminalStyle.Focus, CheckboxSpan(terminal, 2, 1, "[").Style);
        Assert.Equal(TerminalStyle.Focus, SkillSpan(terminal, 2, 1).Style);
        Assert.Contains("0 of 3 selected; 0 to remove", terminal.Frames[2]);
    }

    [Fact]
    public void No_color_install_mode_uses_only_the_addition_marker()
    {
        var terminal = new FakeTerminal { SupportsColor = false };
        terminal.Press(ConsoleKey.A, ConsoleKey.C, ConsoleKey.Spacebar, ConsoleKey.Enter);

        var chosen = new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.Equal("skill-01", Assert.Single(chosen!));
        Assert.Contains(">   [ ] skill-01", terminal.Frames[0]);
        Assert.Contains("> + [X] skill-01", terminal.Frames[1]);
        Assert.Contains("  + [X] skill-02", terminal.Frames[1]);
        Assert.Contains("  + [X] skill-03", terminal.Frames[1]);
        Assert.Contains("    [ ] skill-02", terminal.Frames[2]);
        Assert.Contains("> + [X] skill-01", terminal.Frames[3]);
        Assert.Contains("    [ ] skill-02", terminal.Frames[3]);
        Assert.All(terminal.StyleEvents, style => Assert.Equal(TerminalStyle.Default, style));
        Assert.All(terminal.Frames, frame =>
        {
            Assert.Contains("+ install", frame);
            Assert.DoesNotContain("- remove", frame);
            Assert.DoesNotContain("- [", frame);
            Assert.DoesNotContain("will install", frame);
        });
    }

    [Fact]
    public void No_color_uninstall_mode_uses_only_the_removal_marker()
    {
        var terminal = new FakeTerminal { SupportsColor = false };
        terminal.Press(ConsoleKey.Spacebar, ConsoleKey.Spacebar, ConsoleKey.A, ConsoleKey.C, ConsoleKey.Enter);

        var chosen = new SkillPicker(terminal).Choose(Items(3), Title, PickerMode.Uninstall);

        Assert.Empty(chosen!);
        Assert.Contains("> - [X] skill-01", terminal.Frames[1]);
        Assert.Contains(">   [ ] skill-01", terminal.Frames[2]);
        Assert.Contains("  - [X] skill-03", terminal.Frames[3]);
        Assert.Contains("    [ ] skill-03", terminal.Frames[4]);
        Assert.All(terminal.StyleEvents, style => Assert.Equal(TerminalStyle.Default, style));
        Assert.All(terminal.Frames, frame =>
        {
            Assert.DoesNotContain("+ install", frame);
            Assert.DoesNotContain("> +", frame);
            Assert.Contains("- remove", frame);
        });
    }

    [Theory]
    [InlineData(false, null, null, true, true)]
    [InlineData(true, null, "xterm-256color", true, false)]
    [InlineData(false, "", "xterm-256color", true, false)]
    [InlineData(false, "1", "xterm-256color", false, false)]
    [InlineData(false, null, "dumb", true, false)]
    [InlineData(false, null, "DuMb", false, false)]
    [InlineData(false, null, "vt100", true, false)]
    [InlineData(false, null, "vt220", false, false)]
    [InlineData(false, null, "unknown", false, false)]
    [InlineData(false, null, null, false, false)]
    [InlineData(false, null, "xterm-256color", false, true)]
    [InlineData(false, null, "screen", false, true)]
    [InlineData(false, null, "tmux-256color", false, true)]
    [InlineData(false, null, "linux", false, true)]
    public void System_terminal_respects_NO_COLOR_redirection_and_color_capabilities(
        bool redirected, string? noColor, string? term, bool windows, bool expected)
    {
        Assert.Equal(expected, SystemTerminal.CanUseColor(redirected, noColor, term, windows));
    }

    [Fact]
    public void Mixed_description_pages_preserve_whole_entries_and_the_preferred_page_offset()
    {
        var items = MixedItems(1, 3, 2, 4, 1, 2, 3, 1, 5, 2, 1, 4, 2, 3);
        var terminal = new FakeTerminal(windowHeight: 24, windowWidth: 80)
            .Press(ConsoleKey.DownArrow, times: 3)
            .Press(ConsoleKey.Spacebar, ConsoleKey.RightArrow, ConsoleKey.Spacebar,
                ConsoleKey.RightArrow, ConsoleKey.LeftArrow, ConsoleKey.PageUp, ConsoleKey.Enter);

        var chosen = new SkillPicker(terminal).Choose(items, Title);

        Assert.Equal(["skill-04", "skill-10"], chosen!);
        Assert.Equal(Enumerable.Range(1, 6).Select(number => $"skill-{number:00}"), FrameSkillNames(terminal.Frames[0]));
        Assert.Equal(Enumerable.Range(7, 5).Select(number => $"skill-{number:00}"), FrameSkillNames(terminal.Frames[5]));
        Assert.Equal(Enumerable.Range(12, 3).Select(number => $"skill-{number:00}"), FrameSkillNames(terminal.Frames[7]));
        Assert.Contains("> [ ] skill-10", terminal.Frames[5]);
        Assert.Contains("> [ ] skill-14", terminal.Frames[7]);
        Assert.Contains("> [X] skill-10", terminal.Frames[8]);
        Assert.Contains("> [X] skill-04", terminal.Frames[9]);
        Assert.Contains("desc-06-2", terminal.Frames[0]);
        Assert.DoesNotContain("desc-07-1", terminal.Frames[0]);
        Assert.Contains("desc-09-5", terminal.Frames[5]);
        Assert.Contains("desc-11-1", terminal.Frames[5]);
        Assert.Contains("desc-12-4", terminal.Frames[7]);
        Assert.Contains("desc-14-3", terminal.Frames[7]);
        Assert.DoesNotContain("desc-11-1", terminal.Frames[7]);
        Assert.Equal(18, terminal.CursorTopsAwaitingKey[7]);
        Assert.All(terminal.Frames[7].Split(Environment.NewLine).Skip(18),
            line => Assert.True(string.IsNullOrWhiteSpace(line)));
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void Changing_actions_and_focus_never_changes_page_membership()
    {
        var items = Items(24)
            .Select(item => item with { Description = "Short." }).ToArray();
        var terminal = new FakeTerminal(windowHeight: 18, windowWidth: 46)
            .Press(ConsoleKey.C, ConsoleKey.A, ConsoleKey.DownArrow, ConsoleKey.Spacebar, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(items, Title);

        Assert.All(terminal.Frames, frame => Assert.Equal(FrameSkillNames(terminal.Frames[0]), FrameSkillNames(frame)));
        Assert.Equal(Enumerable.Range(1, 5).Select(number => $"skill-{number:00}"), FrameSkillNames(terminal.Frames[0]));
        Assert.Equal(SkillSpan(terminal, 0, 1).Top, SkillSpan(terminal, 4, 1).Top);
        Assert.Equal(TerminalStyle.Selected, CheckboxSpan(terminal, 2, 2, "X").Style);
        Assert.Equal(TerminalStyle.Focus, SkillSpan(terminal, 2, 1).Style);
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void A_growing_then_shrinking_summary_wraps_without_repaginating_or_leaving_old_footer_rows()
    {
        var items = Items(100).Select(item => item with { Description = "Short." }).ToArray();
        // At this width the empty uninstall summary fits on one line and the full one does not,
        // so selecting everything grows the footer by a row and clearing shrinks it again.
        var terminal = new FakeTerminal(windowHeight: 30, windowWidth: 33)
            .Press(ConsoleKey.A, ConsoleKey.C, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(items, Title, PickerMode.Uninstall);

        var tops = terminal.CursorTopsAwaitingKey;
        Assert.Equal([tops[0], tops[0] + 1, tops[0]], tops);
        Assert.All(terminal.Frames, frame => Assert.Equal(FrameSkillNames(terminal.Frames[0]), FrameSkillNames(frame)));
        Assert.Contains("100 of 100 selected; 100 to", terminal.Frames[1]);
        Assert.Contains("0 of 100 selected; 0 to remove", terminal.Frames[2]);
        Assert.True(string.IsNullOrWhiteSpace(terminal.Frames[2].Split(Environment.NewLine)[tops[0]]));
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void Normal_arrows_navigate_skills_even_when_a_description_is_scrollable()
    {
        var terminal = new FakeTerminal(windowHeight: 24, windowWidth: 80)
            .PressWith(ConsoleModifiers.Control, ConsoleKey.DownArrow)
            .Press(ConsoleKey.DownArrow, ConsoleKey.UpArrow, ConsoleKey.Enter);

        new SkillPicker(terminal).Choose(MixedItems(50, 1, 1), Title);

        Assert.Contains("      desc-01-3", terminal.Frames[1]);
        Assert.Contains(" - desc-01-1", terminal.Frames[1]);
        Assert.DoesNotContain("desc-01-2", terminal.Frames[1].Split(Environment.NewLine).Select(line => line.Trim()));
        Assert.Contains("> [ ] skill-02", terminal.Frames[2]);
        Assert.Contains("page 2 of 2", terminal.Frames[2]);
        Assert.DoesNotContain("<Ctrl+Up>", terminal.Frames[2]);
        Assert.Contains("> [ ] skill-01", terminal.Frames[3]);
        Assert.Contains("      desc-01-3", terminal.Frames[3]);
        Assert.Contains("<Ctrl+Up>/<Ctrl+Down>", terminal.Frames[3]);
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void Description_scrolling_reaches_every_line_and_clamps_both_ends_without_selecting()
    {
        var terminal = new FakeTerminal(windowHeight: 24, windowWidth: 80)
            .PressWith(ConsoleModifiers.Control, ConsoleKey.UpArrow, times: 3)
            .PressWith(ConsoleModifiers.Control, ConsoleKey.DownArrow, times: 80)
            .PressWith(ConsoleModifiers.Control, ConsoleKey.UpArrow, times: 80)
            .Press(ConsoleKey.Spacebar, ConsoleKey.Enter);
        var description = string.Join("\n", Enumerable.Range(1, 60).Select(line => $"detail-{line:00}"));

        var chosen = new SkillPicker(terminal).Choose(
            [new SkillPickerItem("alpha", "Pkg", "1", description)], Title);

        Assert.Equal("alpha", Assert.Single(chosen!));
        Assert.Equal(terminal.Frames[0], terminal.Frames[3]);
        Assert.Contains("detail-60", terminal.Frames[83]);
        Assert.Contains(" - detail-01", terminal.Frames[83]);
        Assert.DoesNotContain("detail-02", terminal.Frames[83]);
        Assert.Equal(terminal.Frames[82], terminal.Frames[83]);
        Assert.Equal(terminal.Frames[0], terminal.Frames[163]);
        for (var line = 1; line <= 60; line++)
        {
            Assert.Contains(terminal.Frames, frame => frame.Contains($"detail-{line:00}", StringComparison.Ordinal));
        }

        Assert.All(terminal.Frames.Take(164), frame => Assert.Contains("> [ ] alpha", frame));
        Assert.Contains("> [X] alpha", terminal.Frames[164]);
        Assert.All(terminal.Frames, frame => Assert.Contains("<Ctrl+Up>/<Ctrl+Down>", frame));
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void Scroll_controls_are_ignored_and_not_advertised_when_every_line_fits()
    {
        var terminal = new FakeTerminal().PressWith(ConsoleModifiers.Control, ConsoleKey.DownArrow)
            .PressWith(ConsoleModifiers.Control, ConsoleKey.UpArrow)
            .Press(ConsoleKey.Enter);

        var chosen = new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.Empty(chosen!);
        Assert.All(terminal.Frames, frame =>
        {
            Assert.Contains("> [ ] skill-01", frame);
            Assert.DoesNotContain("<Ctrl+Up>", frame);
            Assert.Equal(terminal.Frames[0], frame);
        });
    }

    [Fact]
    public void Resizing_down_and_up_preserves_focus_and_selections_and_erases_old_cells()
    {
        var terminal = new FakeTerminal(windowHeight: 40, windowWidth: 160)
            .Press(ConsoleKey.End, ConsoleKey.Spacebar, ConsoleKey.Home, ConsoleKey.Spacebar, ConsoleKey.End)
            .Resize(windowHeight: 18, windowWidth: 46)
            .Resize(windowHeight: 40, windowWidth: 160)
            .Press(ConsoleKey.Enter);

        var chosen = new SkillPicker(terminal).Choose(Items(24), Title);

        Assert.Equal(["skill-01", "skill-24"], chosen!);
        Assert.Equal(3, terminal.ViewportClears);
        Assert.Equal((46, 18), terminal.FrameSizes[6]);
        Assert.Contains("> [X] skill-24", terminal.Frames[6]);
        Assert.DoesNotContain("skill-01", terminal.Frames[6]);
        Assert.DoesNotContain("Package.24", terminal.Frames[6]);
        Assert.Contains("> [X] skill-24 - No description provided.", terminal.Frames[7]);
        Assert.Contains("[X] skill-01 - No description provided.", terminal.Frames[7]);
        Assert.Equal(24, Rows(terminal.Frames[7]).Count);
        Assert.DoesNotContain("change page", terminal.Frames[7]);
        AssertWithinWindow(terminal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Resizing_during_a_redraw_restarts_the_frame_without_losing_selection(bool uninstall, bool color)
    {
        var terminal = new FakeTerminal(windowHeight: 50, windowWidth: 120) { SupportsColor = color };
        terminal.Press(ConsoleKey.Spacebar, ConsoleKey.Enter);
        var resized = false;
        terminal.BeforeOperation = operation =>
        {
            if (!resized && operation == nameof(FakeTerminal.SetStyle) &&
                terminal.KeysRead.Count == 1 && terminal.Writes.LastOrDefault()?.Text.StartsWith('[') == true)
            {
                resized = true;
                terminal.ResizeNow(windowHeight: 18, windowWidth: 46);
            }
        };

        var selected = new SkillPicker(terminal).Choose(
            MixedItems(8, 8, 8), Title, uninstall ? PickerMode.Uninstall : PickerMode.Install);

        Assert.True(resized);
        Assert.Equal("skill-01", Assert.Single(selected!));
        Assert.Equal(2, terminal.ViewportClears);
        Assert.Contains("page 1 of 3", terminal.Frames[^1]);
        Assert.Contains(PickerLayout.PrimaryHelp, terminal.Frames[^1]);
        Assert.Contains("1 of 3 selected", terminal.Frames[^1]);
        AssertWithinWindow(terminal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Continuation_lines_use_the_width_beneath_the_skill_text(bool color)
    {
        var terminal = new FakeTerminal(windowHeight: 24, windowWidth: 46) { SupportsColor = color };
        terminal.Press(ConsoleKey.Spacebar, ConsoleKey.Enter);

        var selected = new SkillPicker(terminal).Choose(
            [new SkillPickerItem("longer-skill", "P", "1",
                "One two three four five six seven eight nine ten.")], Title);

        var lines = terminal.Frames[0].Split(Environment.NewLine).Select(line => line.TrimEnd()).ToArray();
        Assert.EndsWith("longer-skill - One two three four" + (color ? " five" : ""), lines[2]);
        Assert.Equal(
            new string(' ', color ? 6 : 8) + (color ? "six seven eight nine ten." : "five six seven eight nine ten."),
            lines[3]);
        Assert.Equal(string.Empty, lines[4]);
        Assert.Equal("longer-skill", Assert.Single(selected!));
        AssertWithinWindow(terminal);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void Resizing_without_a_key_reflows_before_input_and_preserves_focus_and_selections(
        bool uninstall, bool color)
    {
        var terminal = new FakeTerminal(windowHeight: 32, windowWidth: 120) { SupportsColor = color };
        terminal.Press(ConsoleKey.Spacebar, ConsoleKey.End, ConsoleKey.Spacebar)
            .ResizeWhileWaiting(windowHeight: 18, windowWidth: 46)
            .WaitWithoutKey(times: 2)
            .Press(ConsoleKey.Enter);
        var focused = color ? "> [X] skill-24" : uninstall ? "> - [X] skill-24" : "> + [X] skill-24";
        terminal.BeforeOperation = operation =>
        {
            if (operation == nameof(FakeTerminal.ReadKey) && terminal.KeysRead.Count == 3)
            {
                // Assert the resize is already visible BEFORE the next real key is consumed.
                Assert.Equal(2, terminal.ViewportClears);
                Assert.Contains("page 5 of 5", terminal.Screen);
                Assert.Contains(focused, terminal.Screen);
                Assert.Contains("(Press <space> to select, <enter> to accept)", terminal.Screen);
            }
        };
        var items = Items(24);

        var chosen = new SkillPicker(terminal).Choose(items, Title,
            uninstall ? PickerMode.Uninstall : PickerMode.Install);

        Assert.Equal(["skill-01", "skill-24"], chosen!);
        Assert.Contains("page 2 of 2", terminal.Frames[3]);
        Assert.Equal((46, 18), terminal.FrameSizes[4]);
        Assert.Contains("page 5 of 5", terminal.Frames[4]);
        Assert.Contains(focused, terminal.Frames[4]);
        Assert.DoesNotContain("Package.24", terminal.Frames[4]);
        Assert.Equal(color ? TerminalStyle.Focus : TerminalStyle.Default,
            SkillSpan(terminal, 4, 24).Style);
        Assert.Equal(color ? TerminalStyle.Selected : TerminalStyle.Default, CheckboxSpan(terminal, 4, 24, "X").Style);
        Assert.Equal(terminal.Frames[4], terminal.Frames[5]);
        Assert.Equal(terminal.Frames[4], terminal.Frames[6]);
        Assert.Empty(terminal.FrameWrites[5]);
        Assert.Empty(terminal.FrameWrites[6]);
        Assert.Equal([ConsoleKey.Spacebar, ConsoleKey.End, ConsoleKey.Spacebar, ConsoleKey.Enter],
            terminal.KeysRead.Select(key => key.Key));
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void Idle_input_waits_are_bounded_and_do_not_repaint_unchanged_frames()
    {
        var terminal = new FakeTerminal().WaitWithoutKey(times: 3).Press(ConsoleKey.Enter);

        var chosen = new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.Empty(chosen!);
        Assert.Equal(4, terminal.InputTimeouts.Count);
        Assert.All(terminal.InputTimeouts, timeout => Assert.Equal(TimeSpan.FromMilliseconds(100), timeout));
        Assert.All(terminal.Frames, frame => Assert.Equal(terminal.Frames[0], frame));
        Assert.All(terminal.FrameWrites.Skip(1), writes => Assert.Empty(writes));
        Assert.Equal(1, terminal.ViewportClears);
        Assert.Equal(ConsoleKey.Enter, Assert.Single(terminal.KeysRead).Key);
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void An_idle_resize_can_reveal_a_complete_description_and_remove_the_scroll_hint()
    {
        var terminal = new FakeTerminal(windowHeight: 18, windowWidth: 46)
            .Press(ConsoleKey.Spacebar)
            .PressWith(ConsoleModifiers.Control, ConsoleKey.DownArrow, times: 5)
            .ResizeWhileWaiting(windowHeight: 100, windowWidth: 120)
            .WaitWithoutKey(times: 2)
            .Press(ConsoleKey.Enter);
        terminal.BeforeOperation = operation =>
        {
            if (operation == nameof(FakeTerminal.ReadKey) && terminal.KeysRead.Count == 6)
            {
                Assert.Contains("desc-01-60", terminal.Screen);
                Assert.DoesNotContain("<Ctrl+Up>", terminal.Screen);
                Assert.Contains("> [X] skill-01 - desc-01-1", terminal.Screen);
            }
        };

        var chosen = new SkillPicker(terminal).Choose(MixedItems(60), Title);

        Assert.Equal("skill-01", Assert.Single(chosen!));
        Assert.Contains("      desc-01-7", terminal.Frames[6]);
        Assert.Contains(" - desc-01-1", terminal.Frames[6]);
        Assert.EndsWith(" - desc-01-1", terminal.Frames[7].Split(Environment.NewLine)[2].TrimEnd());
        Assert.Contains("desc-01-60", terminal.Frames[7]);
        Assert.DoesNotContain("<Ctrl+Up>", terminal.Frames[7]);
        Assert.Equal(terminal.Frames[7], terminal.Frames[8]);
        Assert.Equal(terminal.Frames[7], terminal.Frames[9]);
        Assert.Empty(terminal.FrameWrites[8]);
        Assert.Empty(terminal.FrameWrites[9]);
        Assert.Equal(7, terminal.KeysRead.Count);
        Assert.Equal(2, terminal.ViewportClears);
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void A_page_key_received_with_a_resize_uses_the_new_page_boundaries()
    {
        var terminal = new FakeTerminal(windowHeight: 40, windowWidth: 200)
            .Press(ConsoleKey.DownArrow, times: 2)
            .ResizeBeforeKey(ConsoleKey.PageDown, windowHeight: 18, windowWidth: 100)
            .Press(ConsoleKey.Spacebar, ConsoleKey.Enter);

        var chosen = new SkillPicker(terminal).Choose(Items(24), Title);

        Assert.Equal("skill-11", Assert.Single(chosen!));
        Assert.Contains("> [ ] skill-11", terminal.Frames[3]);
        Assert.Contains("page 2 of 3", terminal.Frames[3]);
        Assert.DoesNotContain("skill-01", terminal.Frames[3]);
        Assert.Equal(2, terminal.ViewportClears);
        AssertWithinWindow(terminal);
    }

    [Theory]
    [InlineData(ConsoleKey.Enter)]
    [InlineData(ConsoleKey.Escape)]
    [InlineData(ConsoleKey.Q)]
    public void Resizing_with_an_exit_key_still_redraws_and_restores_the_terminal(ConsoleKey exit)
    {
        var terminal = new FakeTerminal(windowHeight: 24, windowWidth: 80)
            .ResizeBeforeKey(exit, windowHeight: 18, windowWidth: 46);

        var chosen = new SkillPicker(terminal).Choose(Items(4), Title);

        if (exit == ConsoleKey.Enter)
        {
            Assert.Empty(chosen!);
        }
        else
        {
            Assert.Null(chosen);
        }

        Assert.Equal(2, terminal.ViewportClears);
        Assert.Contains("(Press <space> to select, <enter> to accept)", terminal.LastPickerScreen);
        Assert.All(terminal.LastPickerScreen.Split(Environment.NewLine), line => Assert.True(TerminalText.Width(line) < 46));
        Assert.Empty(terminal.Screen);
        Assert.InRange(terminal.FinalCursorTop, 0, 17);
        Assert.True(terminal.IsCursorVisible);
        Assert.False(terminal.IsControlCTakenAsInput);
        Assert.Equal(TerminalStyle.Default, terminal.CurrentStyle);
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void A_scrolled_description_is_clamped_after_resizing_and_fully_shown_when_it_fits()
    {
        var terminal = new FakeTerminal(windowHeight: 24, windowWidth: 80)
            .Press(ConsoleKey.Spacebar)
            .PressWith(ConsoleModifiers.Control, ConsoleKey.DownArrow, times: 5)
            .Resize(windowHeight: 18, windowWidth: 46)
            .Resize(windowHeight: 100, windowWidth: 160)
            .Resize(windowHeight: 18, windowWidth: 46)
            .Press(ConsoleKey.Enter);

        var chosen = new SkillPicker(terminal).Choose(MixedItems(60), Title);

        Assert.Equal("skill-01", Assert.Single(chosen!));
        Assert.Contains("      desc-01-7", terminal.Frames[7]);
        Assert.Contains(" - desc-01-1", terminal.Frames[7]);
        Assert.Contains(" - desc-01-1", terminal.Frames[8]);
        Assert.Contains("desc-01-60", terminal.Frames[8]);
        Assert.DoesNotContain("<Ctrl+Up>", terminal.Frames[8]);
        Assert.Contains(" - desc-01-1", terminal.Frames[9]);
        Assert.DoesNotContain("desc-01-60", terminal.Frames[9]);
        Assert.Contains("<Ctrl+Up>/<Ctrl+Down>", terminal.Frames[9]);
        Assert.All(terminal.Frames.Skip(1), frame => Assert.Contains("> [X] skill-01", frame));
        AssertWithinWindow(terminal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resizing_to_an_impossible_viewport_fails_safely_instead_of_drawing_outside_it(bool idle)
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar);
        if (idle)
        {
            terminal.ResizeWhileWaiting(windowHeight: 4, windowWidth: 5);
        }
        else
        {
            terminal.Resize(windowHeight: 4, windowWidth: 5);
        }

        var error = Assert.Throws<PackageSkillsException>(() => new SkillPicker(terminal).Choose(Items(3), Title));

        Assert.Contains("5x4", error.Message);
        Assert.Contains("Enlarge the window", error.Message);
        Assert.True(terminal.IsCursorVisible);
        Assert.False(terminal.IsControlCTakenAsInput);
        Assert.Equal(ConsoleColor.Gray, terminal.Foreground);
        Assert.Equal(ConsoleColor.Black, terminal.Background);
        Assert.Equal(TerminalStyle.Default, terminal.CurrentStyle);
        Assert.InRange(terminal.FinalCursorTop, 0, 3);
        AssertWithinWindow(terminal);
    }

    [Theory]
    [InlineData(46, 18, true)]
    [InlineData(46, 18, false)]
    [InlineData(80, 24, true)]
    [InlineData(80, 24, false)]
    [InlineData(160, 40, true)]
    [InlineData(240, 80, true)]
    public void All_navigation_and_selection_keys_stay_inside_the_viewport(
        int width, int height, bool color)
    {
        var terminal = new FakeTerminal(windowHeight: height, windowWidth: width) { SupportsColor = color };
        terminal.Press(
            ConsoleKey.DownArrow, ConsoleKey.RightArrow, ConsoleKey.PageDown, ConsoleKey.UpArrow,
            ConsoleKey.PageUp, ConsoleKey.LeftArrow, ConsoleKey.End, ConsoleKey.Home, ConsoleKey.A,
            ConsoleKey.C, ConsoleKey.Spacebar, ConsoleKey.Enter);
        var items = MixedItems(1, 3, 2, 4, 1, 2, 3, 1, 5, 2, 1, 4, 2, 3);

        var chosen = new SkillPicker(terminal).Choose(items, Title);

        Assert.Equal("skill-01", Assert.Single(chosen!));
        Assert.Contains("[X] skill-01", terminal.Frames[^1]);
        Assert.Contains("(Press <space> to select, <enter> to accept)", terminal.Frames[^1]);
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void A_picker_started_near_the_bottom_does_not_scroll_the_shell_buffer()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);
        terminal.SetCursorPosition(0, 16);
        terminal.Write("previous prompt");
        var before = terminal.Screen;

        new SkillPicker(terminal).Choose(Items(3), Title);

        var lines = terminal.Frames[0].Split(Environment.NewLine);
        Assert.Equal(Title, lines[0].TrimEnd());
        Assert.Contains("> [ ] skill-01", lines[2]);
        Assert.DoesNotContain("previous prompt", terminal.Frames[0]);
        Assert.Equal(11, terminal.CursorTopAwaitingKey);
        Assert.Equal(before, terminal.Screen);
        Assert.Equal(16, terminal.FinalCursorTop);
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void A_small_frame_is_separate_from_preceding_output_and_restores_it()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Enter);
        terminal.Write("earlier output");
        terminal.SetCursorPosition(0, 3);
        var before = terminal.Screen;

        new SkillPicker(terminal).Choose(Items(3), Title);

        var lines = terminal.Frames[0].Split(Environment.NewLine);
        Assert.Equal(Title, lines[0].TrimEnd());
        Assert.DoesNotContain("earlier output", terminal.Frames[0]);
        Assert.Equal(11, terminal.CursorTopAwaitingKey);
        Assert.Equal(before, terminal.Screen);
        Assert.Equal(3, terminal.FinalCursorTop);
        AssertWithinWindow(terminal);
    }

    [Theory]
    [InlineData(ConsoleKey.Enter)]
    [InlineData(ConsoleKey.Escape)]
    [InlineData(ConsoleKey.Q)]
    public void Exit_restores_the_original_colors_style_cursor_and_Ctrl_C_ownership(ConsoleKey exit)
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar, exit);
        terminal.SetStyle(TerminalStyle.Muted);
        terminal.Foreground = ConsoleColor.Yellow;
        terminal.Background = ConsoleColor.DarkMagenta;
        terminal.CursorVisible = false;
        terminal.TreatControlCAsInput = true;
        var original = terminal.CaptureState();

        new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.Contains(TerminalStyle.Selected, terminal.StyleEvents);
        Assert.Equal(original, terminal.CaptureState());
    }

    [Fact]
    public void Ctrl_C_restores_colors_after_a_colored_selection()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.A)
            .PressWith(ConsoleModifiers.Control, ConsoleKey.C);
        var original = terminal.CaptureState();

        var chosen = new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.Null(chosen);
        Assert.Contains(TerminalStyle.Selected, terminal.StyleEvents);
        Assert.Equal(original, terminal.CaptureState());
    }

    [Theory]
    [InlineData("UseUtf8Output")]
    [InlineData("EnterInteractiveScreen")]
    [InlineData("CursorVisible")]
    [InlineData("TreatControlCAsInput")]
    [InlineData("SetStyle")]
    [InlineData("Write")]
    [InlineData("SetCursorPosition")]
    [InlineData("TryReadKey")]
    [InlineData("ReadKey")]
    [InlineData("ClearViewport")]
    public void Exceptions_in_setup_render_input_or_resize_restore_all_terminal_state(string operation)
    {
        var terminal = new FakeTerminal()
            .Resize(windowHeight: 24, windowWidth: 80)
            .Press(ConsoleKey.Enter);
        terminal.Foreground = ConsoleColor.Yellow;
        terminal.Background = ConsoleColor.DarkMagenta;
        var original = terminal.CaptureState();
        var failed = false;
        terminal.BeforeOperation = current =>
        {
            if (!failed && current == operation)
            {
                failed = true;
                throw new IOException($"failure in {operation}");
            }
        };

        var error = Assert.Throws<IOException>(() => new SkillPicker(terminal).Choose(Items(3), Title));

        Assert.Equal($"failure in {operation}", error.Message);
        Assert.True(failed);
        Assert.Equal(original, terminal.CaptureState());
        Assert.False(terminal.IsInteractiveScreen);
        Assert.Equal(terminal.ScreenEntries, terminal.ScreenExits);
    }

    [Fact]
    public void An_input_exception_after_an_action_span_does_not_leak_its_color()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar);
        terminal.BeforeOperation = operation =>
        {
            if (operation == nameof(FakeTerminal.ReadKey) && terminal.Frames.Count == 2)
            {
                throw new IOException("input disappeared");
            }
        };

        Assert.Throws<IOException>(() => new SkillPicker(terminal).Choose(Items(3), Title));

        Assert.Contains(TerminalStyle.Selected, terminal.StyleEvents);
        Assert.Equal(TerminalStyle.Default, terminal.CurrentStyle);
        Assert.Equal(ConsoleColor.Gray, terminal.Foreground);
        Assert.Equal(ConsoleColor.Black, terminal.Background);
        Assert.True(terminal.IsCursorVisible);
        Assert.False(terminal.IsControlCTakenAsInput);
        Assert.Equal(12, terminal.LastPickerCursorTop);
        Assert.Equal(0, terminal.FinalCursorTop);
    }

    [Fact]
    public void A_failed_colored_write_restores_style_and_parks_below_the_last_complete_frame()
    {
        var terminal = new FakeTerminal().Press(ConsoleKey.Spacebar);
        terminal.Foreground = ConsoleColor.Yellow;
        terminal.Background = ConsoleColor.DarkMagenta;
        var original = terminal.CaptureState();
        terminal.BeforeOperation = operation =>
        {
            if (operation == nameof(FakeTerminal.Write) && terminal.CurrentStyle == TerminalStyle.Selected)
            {
                throw new IOException("colored write failed");
            }
        };

        var error = Assert.Throws<IOException>(() => new SkillPicker(terminal).Choose(Items(3), Title));

        Assert.Equal("colored write failed", error.Message);
        Assert.Contains(TerminalStyle.Selected, terminal.StyleEvents);
        Assert.Equal(original, terminal.CaptureState());
        Assert.Equal(12, terminal.LastPickerCursorTop);
        Assert.Equal(0, terminal.FinalCursorTop);
        Assert.Contains("Blue X: selected", terminal.LastPickerScreen);
        Assert.Empty(terminal.Screen);
    }

    [Theory]
    [InlineData(ConsoleKey.Enter)]
    [InlineData(ConsoleKey.Escape)]
    [InlineData(ConsoleKey.Q)]
    public void UTF8_output_is_scoped_to_the_picker_and_the_exact_original_encoding_is_restored(ConsoleKey exit)
    {
        var originalEncoding = (Encoding)Encoding.Latin1.Clone();
        originalEncoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        var terminal = new FakeTerminal { OutputEncoding = originalEncoding };
        terminal.Press(ConsoleKey.Spacebar, exit);
        terminal.BeforeOperation = operation =>
        {
            if (operation == nameof(FakeTerminal.ReadKey))
            {
                Assert.Equal(Encoding.UTF8.CodePage, terminal.OutputEncoding.CodePage);
                Assert.Empty(terminal.OutputEncoding.GetPreamble());
            }
        };

        new SkillPicker(terminal).Choose(Items(3), Title);

        Assert.Same(originalEncoding, terminal.OutputEncoding);
        Assert.Same(EncoderFallback.ExceptionFallback, terminal.OutputEncoding.EncoderFallback);
        Assert.Equal([Encoding.UTF8.CodePage, originalEncoding.CodePage],
            terminal.EncodingChanges.Select(encoding => encoding.CodePage));
        Assert.All(terminal.Writes, write => Assert.Equal(Encoding.UTF8.CodePage, write.OutputCodePage));
    }

    [Fact]
    public void Ctrl_C_restores_the_original_output_encoding()
    {
        var terminal = new FakeTerminal { OutputEncoding = Encoding.Latin1 };
        terminal.Press(ConsoleKey.Spacebar).PressWith(ConsoleModifiers.Control, ConsoleKey.C);
        var original = terminal.CaptureState();

        Assert.Null(new SkillPicker(terminal).Choose(Items(3), Title));

        Assert.Equal(original, terminal.CaptureState());
        Assert.Equal([Encoding.UTF8.CodePage, Encoding.Latin1.CodePage],
            terminal.EncodingChanges.Select(encoding => encoding.CodePage));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void Unicode_descriptions_are_encoded_losslessly_and_cannot_emit_terminal_controls(
        bool uninstall, bool color)
    {
        var terminal = new FakeTerminal(windowHeight: 24, windowWidth: 160)
        {
            OutputEncoding = Encoding.Latin1,
            SupportsColor = color,
        };
        terminal.Press(ConsoleKey.Spacebar, ConsoleKey.Enter);
        const string Description = "Café, 測試, 🧪, Cafe\u0301. \x1b[2JUNICODE-END";
        const string ExpectedSpan = " - Café, 測試, 🧪, Cafe\u0301. UNICODE-END";

        var chosen = new SkillPicker(terminal).Choose(
            [new SkillPickerItem("alpha", "Pkg", "1", Description)], Title,
            uninstall ? PickerMode.Uninstall : PickerMode.Install);

        Assert.Equal("alpha", Assert.Single(chosen!));
        var description = Assert.Single(terminal.FrameWrites[0],
            write => write.Text.StartsWith(" - ", StringComparison.Ordinal));
        Assert.Equal(ExpectedSpan, description.Text);
        Assert.Equal(Encoding.UTF8.GetBytes(ExpectedSpan), description.Bytes);
        Assert.Equal(Encoding.UTF8.CodePage, description.OutputCodePage);
        Assert.DoesNotContain((byte)0x1b, description.Bytes);
        Assert.Equal(color ? TerminalStyle.Focus : TerminalStyle.Default, description.Style);
        Assert.Contains("測試", terminal.Frames[0]);
        Assert.Contains("🧪", terminal.Frames[0]);
        Assert.Contains("Cafe\u0301", terminal.Frames[0]);
        Assert.DoesNotContain('?', description.Text);
        Assert.Same(Encoding.Latin1, terminal.OutputEncoding);
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void The_test_terminal_observes_legacy_encoding_loss_instead_of_assuming_Unicode_output()
    {
        var terminal = new FakeTerminal { OutputEncoding = Encoding.Latin1 };
        const string Text = "Café, 測試, 🧪, Cafe\u0301.";

        terminal.Write(Text);

        var write = Assert.Single(terminal.Writes);
        Assert.Equal(Encoding.Latin1.GetBytes(Text), write.Bytes);
        Assert.Equal(Encoding.Latin1.GetString(write.Bytes), terminal.Screen);
        Assert.DoesNotContain("測試", terminal.Screen);
        Assert.DoesNotContain("🧪", terminal.Screen);
        Assert.DoesNotContain("Cafe\u0301", terminal.Screen);
        Assert.NotEqual(Text, terminal.Screen);
    }

    [Fact]
    public void Noninteractive_and_empty_paths_do_not_change_output_encoding()
    {
        var redirected = new FakeTerminal { IsRedirected = true, OutputEncoding = Encoding.Latin1 };
        var empty = new FakeTerminal { IsRedirected = true, OutputEncoding = Encoding.Latin1 };

        Assert.Throws<PackageSkillsException>(() => new SkillPicker(redirected).Choose(Items(1), Title));
        Assert.Empty(new SkillPicker(empty).Choose([], Title)!);

        Assert.Empty(redirected.EncodingChanges);
        Assert.Empty(empty.EncodingChanges);
        Assert.Same(Encoding.Latin1, redirected.OutputEncoding);
        Assert.Same(Encoding.Latin1, empty.OutputEncoding);
        Assert.Empty(redirected.Writes);
        Assert.Empty(empty.Writes);
        Assert.Equal(0, redirected.ScreenEntries);
        Assert.Equal(0, empty.ScreenEntries);
    }

    [Fact]
    public void Authored_unicode_names_use_display_cells_for_column_alignment()
    {
        var terminal = new FakeTerminal(windowHeight: 24, windowWidth: 80).Press(ConsoleKey.A, ConsoleKey.Enter);

        var chosen = new SkillPicker(terminal).Choose(
            [
                new SkillPickerItem("猫", "P", "1", "First."),
                new SkillPickerItem("ab", "P", "1", "Second."),
                new SkillPickerItem("👩🏽‍💻", "P", "1", "Third."),
            ],
            Title);

        Assert.Equal(["猫", "ab", "👩🏽‍💻"], chosen!);
        var descriptions = terminal.FrameWrites[0].Where(write => write.Text.StartsWith(" - ", StringComparison.Ordinal)).ToArray();
        Assert.Equal([8, 8, 8], descriptions.Select(write => write.Left));
        Assert.Equal(
            [TerminalStyle.Focus, TerminalStyle.Default, TerminalStyle.Default],
            descriptions.Select(write => write.Style));
        Assert.Contains("> [X] 猫 - First.", terminal.Frames[1]);
        Assert.Contains("[X] 👩🏽‍💻 - Third.", terminal.Frames[1]);
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void Wrapped_unicode_descriptions_keep_every_grapheme_and_do_not_split_surrogates()
    {
        var terminal = new FakeTerminal(windowHeight: 18, windowWidth: 46).Press(ConsoleKey.Enter);
        var description = string.Concat(Enumerable.Repeat("界e\u0301👩🏽‍💻", 12));

        new SkillPicker(terminal).Choose(
            [new SkillPickerItem("alpha", "Pkg", "1.2.3", description)], Title);

        var indent = new string(' ', 6);
        var rendered = terminal.FrameWrites[0]
            .Where(write => write.Text.StartsWith(" - ", StringComparison.Ordinal) ||
                            write.Text.StartsWith(indent, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(write.Text))
            .Select(write => write.Text.StartsWith(" - ", StringComparison.Ordinal) ? write.Text[3..] : write.Text[6..])
            .ToArray();
        Assert.Equal(description, string.Concat(rendered));
        Assert.Equal(TerminalText.Elements(description), rendered.SelectMany(TerminalText.Elements));
        Assert.All(rendered, line => Assert.DoesNotContain('\ufffd', line));
        Assert.DoesNotContain("...", terminal.Frames[0]);
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void All_author_metadata_is_sanitized_but_returned_skill_identity_is_unchanged()
    {
        const string Name = "unsafe\x1b[2Jskill\r\nname\u202e";
        var terminal = new FakeTerminal(windowHeight: 24, windowWidth: 120)
            .Press(ConsoleKey.Spacebar, ConsoleKey.Enter);
        var item = new SkillPickerItem(Name, "P\a", "\u009b2J1",
            "\x1b]8;;malicious\aVisible\x1b]8;;\x1b\\\nrow\tend\u202e\0");

        var chosen = new SkillPicker(terminal).Choose(
            [item], "\x1b[31mTitle\x1b[0m\nnext\tline");

        Assert.Equal(Name, Assert.Single(chosen!));
        Assert.Contains("Title next line", terminal.Frames[0]);
        Assert.Contains("unsafeskill name - Visible", terminal.Frames[0]);
        Assert.Contains("row end", terminal.Frames[0]);
        Assert.DoesNotContain("malicious", terminal.Frames[0]);
        Assert.DoesNotContain('\u202e', terminal.Frames[0]);
        Assert.All(terminal.Frames, frame =>
            Assert.DoesNotContain(frame.Replace(Environment.NewLine, string.Empty), char.IsControl));
        AssertWithinWindow(terminal);
    }

    [Fact]
    public void Unicode_name_clipping_keeps_whole_graphemes_and_is_not_a_fixed_width_cap()
    {
        var name = string.Concat(Enumerable.Repeat("👩🏽‍💻e\u0301界", 16));
        var item = new SkillPickerItem(name, "P", "1", "Short.");
        var narrow = new FakeTerminal(windowHeight: 24, windowWidth: 46).Press(ConsoleKey.Enter);
        var wide = new FakeTerminal(windowHeight: 24, windowWidth: 240).Press(ConsoleKey.Enter);

        new SkillPicker(narrow).Choose([item], Title);
        new SkillPicker(wide).Choose([item], Title);

        Assert.Contains("...", narrow.Frames[0]);
        Assert.DoesNotContain('\ufffd', narrow.Frames[0]);
        Assert.Contains(name, wide.Frames[0]);
        Assert.DoesNotContain("...", wide.Frames[0]);
        AssertWithinWindow(narrow);
        AssertWithinWindow(wide);
    }

    private static IReadOnlyList<SkillPickerItem> MixedItems(params int[] heights) =>
        Items(heights.Length).Select((item, index) => item with
        {
            Description = string.Join("\n",
                Enumerable.Range(1, heights[index]).Select(line => $"desc-{index + 1:00}-{line}")),
        }).ToArray();

    private static string[] FrameSkillNames(string frame) => Rows(frame)
        .Select(row => System.Text.RegularExpressions.Regex.Match(row, @"\bskill-\d+\b").Value).ToArray();

    private static void AssertWithinWindow(FakeTerminal terminal)
    {
        Assert.All(terminal.Writes, write =>
        {
            Assert.InRange(write.Left, 0, write.WindowWidth - 1);
            Assert.InRange(write.Top, 0, write.WindowHeight - 1);
            Assert.True(write.Left + TerminalText.Width(write.Text) < write.WindowWidth,
                $"Write used the wrap column of {write.WindowWidth}x{write.WindowHeight}: '{write.Text}'");
        });
        for (var frame = 0; frame < terminal.Frames.Count; frame++)
        {
            var (width, height) = terminal.FrameSizes[frame];
            var lines = terminal.Frames[frame].Split(Environment.NewLine);
            Assert.True(lines.Length <= height);
            Assert.All(lines, line => Assert.True(TerminalText.Width(line) < width));
            var lastContent = Array.FindLastIndex(lines, line => !string.IsNullOrWhiteSpace(line));
            Assert.True(lastContent < height - 1);
            Assert.Equal(lastContent + 1, terminal.CursorTopsAwaitingKey[frame]);
        }
    }

    private static IReadOnlyList<SkillPickerItem> Items(int count) =>
    [
        .. Enumerable.Range(1, count).Select(number =>
            new SkillPickerItem($"skill-{number:00}", $"Package.{number}", "1.0.0")),
    ];
}
