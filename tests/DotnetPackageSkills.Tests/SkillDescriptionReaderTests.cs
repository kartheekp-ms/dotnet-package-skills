using System.Text;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class SkillDescriptionReaderTests
{
    [Theory]
    [InlineData("description: Use widgets safely.", "Use widgets safely.")]
    [InlineData("description: Use widgets. # Not part of the description", "Use widgets.")]
    [InlineData("description: 'Use widgets: safely.'", "Use widgets: safely.")]
    [InlineData("description: 'Use ''quoted'' names.'", "Use 'quoted' names.")]
    [InlineData("description: \"Use \\\"quoted\\\" names and \\\\ paths.\"", "Use \"quoted\" names and \\ paths.")]
    [InlineData("description: 'C:\\widgets\\skills'", "C:\\widgets\\skills")]
    [InlineData("description: \"First\\nSecond\\t\\u263A\\U0001F680\"", "First\nSecond\t☺🚀")]
    [InlineData("description: \"  Keep surrounding spaces.  \"", "  Keep surrounding spaces.  ")]
    [InlineData("description: \"Keep\\e terminal\\r controls.\"", "Keep\u001b terminal\r controls.")]
    [InlineData("description: First line\n  continues here", "First line continues here")]
    [InlineData("description: First paragraph\n\n  second paragraph", "First paragraph\nsecond paragraph")]
    [InlineData("description: \"First line\n  continues here\"", "First line continues here")]
    [InlineData("description: >\n  First line\n  continues here", "First line continues here\n")]
    [InlineData("description: >-\n  First paragraph\n  continues here\n\n  Second paragraph", "First paragraph continues here\nSecond paragraph")]
    [InlineData("description: >+\n  First line\n  continues here\n", "First line continues here\n\n")]
    [InlineData("description: >-\n  First line\n    indented line\n  Last line", "First line\n  indented line\nLast line")]
    [InlineData("description: |\n  First line\n  Second line", "First line\nSecond line\n")]
    [InlineData("description: |-\n  First line\n  Second line", "First line\nSecond line")]
    [InlineData("description: |+\n  First line\n  Second line\n", "First line\nSecond line\n\n")]
    [InlineData("description: |2-\n    Indented line\n  Last line", "  Indented line\nLast line")]
    [InlineData("description: |-\n  ---\n  ...\n  description: Still scalar text", "---\n...\ndescription: Still scalar text")]
    [InlineData("\"description\": A quoted key works.", "A quoted key works.")]
    [InlineData("{description: 'A flow mapping works.', other: [one, two]}", "A flow mapping works.")]
    [InlineData("description: 42", "42")]
    [InlineData("description: true", "true")]
    public void A_top_level_scalar_preserves_the_YAML_description_value(string yaml, string expected)
    {
        using var temp = new TempDirectory();
        WriteFrontmatter(temp, yaml);

        var result = SkillDescriptionReader.Read(temp.Path);

        Assert.Equal(expected, result.Description);
        Assert.Null(result.Warning);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# Only a comment")]
    [InlineData("{}")]
    [InlineData("name: widget-usage")]
    [InlineData("metadata:\n  description: Nested descriptions are not the skill description.")]
    [InlineData("items:\n  - description: Nested descriptions are not the skill description.")]
    [InlineData("notes: |\n  description: This is other scalar text.")]
    [InlineData("? [description]\n: This is a collection key, not the description key.")]
    [InlineData("Description: Keys are case-sensitive.")]
    [InlineData("description:")]
    [InlineData("description: # Empty")]
    [InlineData("description: ''")]
    [InlineData("description: \"   \"")]
    [InlineData("description: \"\\n\\t\\r\"")]
    [InlineData("description: |\n  \n  ")]
    public void Missing_or_blank_descriptions_have_no_warning(string yaml)
    {
        using var temp = new TempDirectory();
        WriteFrontmatter(temp, yaml);

        var result = SkillDescriptionReader.Read(temp.Path);

        Assert.Null(result.Description);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Nested_descriptions_do_not_replace_the_top_level_description()
    {
        using var temp = new TempDirectory();
        WriteFrontmatter(temp, """
            before:
              description: Ignore this.
            description: Use this description.
            after:
              - description: Ignore this too.
            """);

        var result = SkillDescriptionReader.Read(temp.Path);

        Assert.Equal("Use this description.", result.Description);
        Assert.Null(result.Warning);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# Just Markdown\n---\ndescription: Not frontmatter.\n---")]
    [InlineData("\n---\ndescription: Not at the beginning.\n---")]
    [InlineData(" ---\ndescription: Indented delimiters do not open frontmatter.\n---")]
    [InlineData("---not-a-delimiter\ndescription: Not frontmatter.\n---")]
    public void Files_without_frontmatter_have_no_description_or_warning(string source)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("SKILL.md", source);

        var result = SkillDescriptionReader.Read(temp.Path);

        Assert.Null(result.Description);
        Assert.Null(result.Warning);
    }

    [Theory]
    [InlineData("Markdown")]
    [InlineData("---not-a-delimiter")]
    public void A_huge_Markdown_first_line_is_not_mistaken_for_oversized_frontmatter(string prefix)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("SKILL.md", prefix + new string('x', 128 * 1024));

        var result = SkillDescriptionReader.Read(temp.Path);

        Assert.Null(result.Description);
        Assert.Null(result.Warning);
    }

    [Theory]
    [InlineData(false, "\n")]
    [InlineData(true, "\n")]
    [InlineData(false, "\r\n")]
    [InlineData(true, "\r\n")]
    public void UTF8_BOM_and_line_endings_preserve_multiline_description_values(bool bom, string newline)
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("SKILL.md");
        File.WriteAllText(file, $"---{newline}description: |{newline}  First{newline}  Second{newline}---{newline}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom));

        var result = SkillDescriptionReader.Read(temp.Path);

        Assert.Equal("First\nSecond\n", result.Description);
        Assert.Null(result.Warning);
    }

    [Theory]
    [InlineData("---")]
    [InlineData("...")]
    [InlineData("--- \t")]
    [InlineData("... \t")]
    public void Closing_delimiters_end_parsing_before_a_large_invalid_Markdown_body(string delimiter)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("SKILL.md", $"--- \t\ndescription: Only the header.\n{delimiter}\n" +
            "description: !invalid *alias\n" + new string('[', 128 * 1024));

        var result = SkillDescriptionReader.Read(temp.Path);

        Assert.Equal("Only the header.", result.Description);
        Assert.Null(result.Warning);
    }

    [Theory]
    [InlineData("---")]
    [InlineData("...")]
    public void A_closing_delimiter_does_not_require_a_final_newline(string delimiter)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("SKILL.md", $"---\ndescription: Complete.\n{delimiter}");

        var result = SkillDescriptionReader.Read(temp.Path);

        Assert.Equal("Complete.", result.Description);
        Assert.Null(result.Warning);
    }

    [Theory]
    [InlineData("description: [unfinished")]
    [InlineData("description: \"unfinished")]
    [InlineData("description: Value\n  unexpected: mapping")]
    [InlineData("description: Valid first\nlater: {unfinished")]
    [InlineData("name: No description\nlater: [unfinished")]
    [InlineData("description: Valid first\nlater:\n\tinvalid: indentation")]
    public void Malformed_YAML_warns_instead_of_returning_partial_metadata(string yaml)
    {
        using var temp = new TempDirectory();
        WriteFrontmatter(temp, yaml);

        AssertWarning(SkillDescriptionReader.Read(temp.Path),
            "SKILL.md has malformed YAML frontmatter; fix the header.");
    }

    [Theory]
    [InlineData("description: [one, two]")]
    [InlineData("description: []")]
    [InlineData("description: {}")]
    [InlineData("description:\n  nested: mapping")]
    public void Collection_descriptions_warn_instead_of_being_converted_to_text(string yaml)
    {
        using var temp = new TempDirectory();
        WriteFrontmatter(temp, yaml);

        AssertWarning(SkillDescriptionReader.Read(temp.Path),
            "SKILL.md description must be a YAML scalar; replace the collection with text.");
    }

    [Theory]
    [InlineData("Just a scalar")]
    [InlineData("''")]
    [InlineData("[description, value]")]
    [InlineData("- description: Inside a sequence")]
    public void Non_mapping_frontmatter_explains_the_expected_shape(string yaml)
    {
        using var temp = new TempDirectory();
        WriteFrontmatter(temp, yaml);

        AssertWarning(SkillDescriptionReader.Read(temp.Path),
            "SKILL.md frontmatter must be a YAML mapping; use 'description: ...'.");
    }

    [Fact]
    public void Duplicate_top_level_descriptions_warn_instead_of_choosing_one()
    {
        using var temp = new TempDirectory();
        WriteFrontmatter(temp, "description: First\ndescription: Second");

        AssertWarning(SkillDescriptionReader.Read(temp.Path),
            "SKILL.md has duplicate description keys; keep only one top-level description.");
    }

    [Theory]
    [InlineData("description: !!str Text")]
    [InlineData("!!map {description: Text}")]
    [InlineData("description: !custom Text")]
    [InlineData("description: !<tag:example.com,2026:run> Text")]
    [InlineData("description: Otherwise valid\nother: !custom value")]
    public void Explicit_tags_are_rejected_without_type_deserialization(string yaml)
    {
        using var temp = new TempDirectory();
        WriteFrontmatter(temp, yaml);

        AssertWarning(SkillDescriptionReader.Read(temp.Path),
            "SKILL.md uses explicit YAML tags; remove the tags from its frontmatter.");
    }

    [Theory]
    [InlineData("description: &text Anchored text")]
    [InlineData("description: *missing")]
    [InlineData("&root {description: Text}")]
    [InlineData("description: Otherwise valid\nother: *missing")]
    [InlineData("a: &a [text]\nb: &b [*a, *a]\ndescription: *b")]
    [InlineData("a: &a [*a]\ndescription: Otherwise valid")]
    public void Anchors_and_aliases_are_rejected_without_expansion(string yaml)
    {
        using var temp = new TempDirectory();
        WriteFrontmatter(temp, yaml);

        AssertWarning(SkillDescriptionReader.Read(temp.Path),
            "SKILL.md uses YAML anchors or aliases; replace them with literal values.");
    }

    [Fact]
    public void The_nesting_limit_allows_32_collection_levels_including_the_root_mapping()
    {
        using var temp = new TempDirectory();
        WriteFrontmatter(temp, "other: " + new string('[', 31) + "leaf" + new string(']', 31) +
            "\ndescription: Within the limit.");

        var result = SkillDescriptionReader.Read(temp.Path);

        Assert.Equal("Within the limit.", result.Description);
        Assert.Null(result.Warning);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(5000)]
    public void Excessive_nesting_warns_even_in_irrelevant_metadata(int levels)
    {
        using var temp = new TempDirectory();
        WriteFrontmatter(temp, "description: Valid first\nother: " +
            new string('[', levels) + "leaf" + new string(']', levels));

        AssertWarning(SkillDescriptionReader.Read(temp.Path),
            "SKILL.md frontmatter exceeds 32 levels of nesting; simplify the header.");
    }

    [Fact]
    public void The_character_limit_includes_delimiters_and_allows_exactly_64_Ki_characters()
    {
        using var temp = new TempDirectory();
        const string prefix = "---\ndescription: ";
        const string suffix = "\n---";
        var description = new string('é', 64 * 1024 - prefix.Length - suffix.Length);
        temp.CreateFile("SKILL.md", prefix + description + suffix);

        var result = SkillDescriptionReader.Read(temp.Path);

        Assert.Equal(description, result.Description);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void One_character_past_the_frontmatter_limit_warns()
    {
        using var temp = new TempDirectory();
        const string prefix = "---\ndescription: ";
        const string suffix = "\n---";
        temp.CreateFile("SKILL.md", prefix + new string('x', 64 * 1024 + 1 - prefix.Length - suffix.Length) + suffix);

        AssertWarning(SkillDescriptionReader.Read(temp.Path),
            "SKILL.md frontmatter exceeds 64 KiB of text; shorten the header.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_huge_single_header_line_cannot_bypass_the_read_limit(bool terminated)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("SKILL.md", "---\ndescription: " + new string('x', 256 * 1024) +
            (terminated ? "\n---\n" : ""));

        AssertWarning(SkillDescriptionReader.Read(temp.Path),
            "SKILL.md frontmatter exceeds 64 KiB of text; shorten the header.");
    }

    [Fact]
    public void Many_short_header_lines_share_one_read_limit()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("SKILL.md", "---\n" + string.Concat(Enumerable.Repeat("# Notes\n", 10_000)) + "---\n");

        AssertWarning(SkillDescriptionReader.Read(temp.Path),
            "SKILL.md frontmatter exceeds 64 KiB of text; shorten the header.");
    }

    [Fact]
    public void A_huge_opening_delimiter_line_cannot_bypass_the_read_limit()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("SKILL.md", "---" + new string(' ', 64 * 1024));

        AssertWarning(SkillDescriptionReader.Read(temp.Path),
            "SKILL.md frontmatter exceeds 64 KiB of text; shorten the header.");
    }

    [Theory]
    [InlineData("---")]
    [InlineData("---\n")]
    [InlineData("---\ndescription: Not closed.")]
    [InlineData("---\ndescription: |-\n  An indented delimiter is scalar content.\n  ---\n")]
    public void Unterminated_frontmatter_explains_how_to_close_it(string source)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("SKILL.md", source);

        AssertWarning(SkillDescriptionReader.Read(temp.Path),
            "SKILL.md frontmatter has no closing delimiter; add a closing '---' line.");
    }

    [Fact]
    public void A_missing_SKILL_file_warns_without_searching_subdirectories()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("nested\\SKILL.md", "---\ndescription: Do not discover this.\n---\n");

        AssertWarning(SkillDescriptionReader.Read(temp.Path),
            "SKILL.md was not found; restore the file to read its description.");
    }

    [Fact]
    public void A_missing_skill_directory_warns()
    {
        using var temp = new TempDirectory();

        AssertWarning(SkillDescriptionReader.Read(temp.Combine("missing")),
            "SKILL.md was not found; restore the file to read its description.");
    }

    [Fact]
    public void An_unreadable_SKILL_path_warns()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("SKILL.md");

        AssertWarning(SkillDescriptionReader.Read(temp.Path),
            "SKILL.md could not be read; check file permissions.");
    }

    [Fact]
    public void A_locked_SKILL_file_warns_instead_of_throwing()
    {
        using var temp = new TempDirectory();
        var file = WriteFrontmatter(temp, "description: Locked.");
        using var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        AssertWarning(SkillDescriptionReader.Read(temp.Path),
            "SKILL.md could not be read; check the file path, permissions, and whether it is in use.");
    }

    [Fact]
    public void An_invalid_skill_directory_path_warns()
    {
        using var temp = new TempDirectory();

        AssertWarning(SkillDescriptionReader.Read(temp.Path + '\0'),
            "SKILL.md has an invalid path; check the skill directory path.");
    }

    [Theory]
    [InlineData("---\r\ndescription: |\r\n  Keep original bytes.\r\n---\r\n# Body\r\n", "Keep original bytes.\n", null)]
    [InlineData("# Only Markdown", null, null)]
    [InlineData("---\ndescription: [broken\n---\n", null, "SKILL.md has malformed YAML frontmatter; fix the header.")]
    [InlineData("---\ndescription: *alias\n---\n", null, "SKILL.md uses YAML anchors or aliases; replace them with literal values.")]
    public void Reading_leaves_every_file_byte_unchanged(string source, string? description, string? warning)
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("SKILL.md");
        File.WriteAllText(file, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var before = File.ReadAllBytes(file);
        var attributes = File.GetAttributes(file);
        File.SetAttributes(file, attributes | FileAttributes.ReadOnly);
        try
        {
            var result = SkillDescriptionReader.Read(temp.Path);

            Assert.Equal(description, result.Description);
            Assert.Equal(warning, result.Warning);
            Assert.Equal(before, File.ReadAllBytes(file));
        }
        finally
        {
            File.SetAttributes(file, attributes);
        }
    }

    private static string WriteFrontmatter(TempDirectory temp, string yaml) =>
        temp.CreateFile("SKILL.md", $"---\n{yaml}\n---\n");

    private static void AssertWarning(SkillDescriptionResult result, string expected)
    {
        Assert.Null(result.Description);
        Assert.Equal(expected, result.Warning);
    }
}
