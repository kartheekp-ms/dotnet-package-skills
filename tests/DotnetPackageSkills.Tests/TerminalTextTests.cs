using DotnetPackageSkills.Cli;

namespace DotnetPackageSkills.Tests;

public class TerminalTextTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("plain", 5)]
    [InlineData("界", 2)]
    [InlineData("Ａ", 2)]
    [InlineData("e\u0301", 1)]
    [InlineData("😀", 2)]
    [InlineData("👩🏽‍💻", 2)]
    [InlineData("🇺🇸", 2)]
    [InlineData("❤️", 2)]
    [InlineData("♥", 1)]
    [InlineData("1️⃣", 2)]
    [InlineData("𐍈", 1)]
    [InlineData("가", 2)]
    [InlineData("\u0301", 0)]
    [InlineData("a\u200d", 1)]
    [InlineData("\u303f", 1)]
    public void Width_counts_display_cells_not_UTF16_code_units(string text, int cells)
    {
        Assert.Equal(cells, TerminalText.Width(text));
    }

    [Fact]
    public void Wrapping_prefers_word_boundaries()
    {
        Assert.Equal(["One two three", "four five"], TerminalText.Wrap("One two three four five", 13));
    }

    [Fact]
    public void Wrapping_keeps_explicit_paragraph_breaks()
    {
        Assert.Equal(["one", "", "two", "three"], TerminalText.Wrap("one\n\ntwo three", 6));
    }

    [Fact]
    public void Long_words_are_split_without_losing_their_tail()
    {
        Assert.Equal(["abcde", "fghij", "kl"], TerminalText.Wrap("abcdefghijkl", 5));
    }

    [Fact]
    public void Wrapping_never_splits_a_combining_sequence_flag_or_joined_emoji()
    {
        const string Text = "ab👩🏽‍💻e\u0301🇺🇸界";

        var lines = TerminalText.Wrap(Text, 3);

        Assert.Equal(["ab", "👩🏽‍💻e\u0301", "🇺🇸", "界"], lines);
        Assert.Equal(Text, string.Concat(lines));
        Assert.All(lines, line => Assert.InRange(TerminalText.Width(line), 1, 3));
    }

    [Fact]
    public void An_exactly_fitting_grapheme_is_not_replaced_or_split()
    {
        Assert.Equal(["👩🏽‍💻", "🇺🇸", "界"], TerminalText.Wrap("👩🏽‍💻🇺🇸界", 2));
    }

    [Fact]
    public void A_column_that_cannot_fit_one_grapheme_is_rejected_instead_of_losing_it()
    {
        Assert.Throws<ArgumentException>(() => TerminalText.Wrap("界", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalText.Wrap("text", 0));
    }

    [Fact]
    public void Continuation_lines_can_use_more_space_than_the_first_line()
    {
        Assert.Equal(
            ["One two", "three four five six", "seven eight nine"],
            TerminalText.Wrap("One two three four five six seven eight nine", 7, 19));
    }

    [Fact]
    public void Wider_continuations_keep_paragraph_breaks_and_long_word_tails()
    {
        Assert.Equal(
            ["abc", "defgh", "", "one two three"],
            TerminalText.Wrap("abcdefgh\n\none two three", 3, 13));
    }

    [Fact]
    public void Wider_continuations_preserve_unicode_graphemes()
    {
        const string text = "界👩🏽‍💻e\u0301界";

        var lines = TerminalText.Wrap(text, 2, 5);

        Assert.Equal(["界", "👩🏽‍💻e\u0301界"], lines);
        Assert.Equal(TerminalText.Elements(text), lines.SelectMany(TerminalText.Elements));
        Assert.Equal(["a", "界"], TerminalText.Wrap("a界", 1, 2));
    }

    [Fact]
    public void Both_wrapping_widths_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalText.Wrap("text", 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalText.Wrap("text", 10, 0));
    }

    [Theory]
    [InlineData(6, "e\u0301界...")]
    [InlineData(4, "e\u0301...")]
    [InlineData(3, "...")]
    [InlineData(2, "..")]
    [InlineData(1, ".")]
    [InlineData(0, "")]
    public void Clipping_names_preserves_graphemes_and_reserves_the_ellipsis(int width, string expected)
    {
        var clipped = TerminalText.Clip("e\u0301界👩🏽‍💻rest", width);

        Assert.Equal(expected, clipped);
        Assert.True(TerminalText.Width(clipped) <= width);
        Assert.DoesNotContain('\ufffd', clipped);
    }

    [Fact]
    public void Fitting_text_is_not_given_an_ellipsis()
    {
        Assert.Equal("👩🏽‍💻e\u0301", TerminalText.Clip("👩🏽‍💻e\u0301", 3));
    }

    [Fact]
    public void Padding_aligns_cells_instead_of_surrogates_or_combining_marks()
    {
        Assert.Equal("界e\u0301   ", TerminalText.PadRight("界e\u0301", 6));
    }

    [Fact]
    public void Sanitizing_strips_ANSI_commands_hyperlinks_controls_and_direction_overrides()
    {
        const string Text =
            "before\x1b[31mRED\x1b[0m\x1b]8;;https://invalid.example\a" +
            "link\x1b]8;;\x1b\\\r\nnext\tpart\0\a\u007f\u009b2J\u202eafter\u2066\u2069";

        Assert.Equal("beforeREDlink\nnext partafter", TerminalText.Sanitize(Text, multiline: true));
        Assert.Equal("beforeREDlink next partafter", TerminalText.Sanitize(Text));
    }

    [Fact]
    public void Sanitizing_trims_boundaries_by_default_but_can_preserve_them()
    {
        const string Text = " \t\u001b[31mfirst\u001b[0m\r\n    second \r\n";
        const string Preserved = "  first\n    second \n";

        Assert.Equal("first\n    second", TerminalText.Sanitize(Text, multiline: true));
        Assert.Equal("first\n    second", TerminalText.Sanitize(Text, multiline: true, trim: true));
        Assert.Equal(Preserved, TerminalText.Sanitize(Text, multiline: true, trim: false));
        Assert.Equal(Preserved.Replace('\n', ' ').Trim(), TerminalText.Sanitize(Text));
        Assert.Equal(Preserved.Replace('\n', ' '), TerminalText.Sanitize(Text, trim: false));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(" \r\n\t", " \n ")]
    public void Preserving_boundaries_keeps_blank_text_without_inventing_content(string? text, string expected)
    {
        Assert.Equal(string.Empty, TerminalText.Sanitize(text, multiline: true));
        Assert.Equal(expected, TerminalText.Sanitize(text, multiline: true, trim: false));
    }

    [Fact]
    public void Preserving_boundaries_still_discards_unterminated_escape_payloads()
    {
        const string Text = "  before  \u001b]52;c;SECRET\r\n";

        Assert.Equal("before", TerminalText.Sanitize(Text, multiline: true));
        Assert.Equal("  before  ", TerminalText.Sanitize(Text, multiline: true, trim: false));
    }

    [Theory]
    [InlineData("a\x1bPignored\x1b\\b", "ab")]
    [InlineData("a\u009dignored\u009cb", "ab")]
    [InlineData("a\x1b(0b", "ab")]
    [InlineData("a\x1b[999mvisible", "avisible")]
    [InlineData("a\x1b]unterminated", "a")]
    [InlineData("a\x1b", "a")]
    public void Terminal_escape_strings_do_not_leak_their_payload(string input, string expected)
    {
        Assert.Equal(expected, TerminalText.Sanitize(input));
    }

    [Fact]
    public void Authored_unicode_is_not_forced_to_ASCII()
    {
        const string Text = "👩🏽‍💻 Café e\u0301 中文 🇨🇦";

        Assert.Equal(Text, TerminalText.Sanitize(Text));
    }

    [Fact]
    public void Unpaired_surrogates_become_visible_replacement_characters()
    {
        Assert.Equal("before\ufffdafter\ufffd", TerminalText.Sanitize("before\ud800after\udfff"));
    }

    [Fact]
    public void Line_separators_are_normalized_without_allowing_terminal_control_characters()
    {
        Assert.Equal("one\ntwo\nthree\nfour",
            TerminalText.Sanitize("one\r\ntwo\rthree\u2028four", multiline: true));
        Assert.Equal("one two three four", TerminalText.Sanitize("one\r\ntwo\rthree\u2028four"));
    }
}
