using System.CommandLine;
using System.Text;
using System.Text.Json;
using DotnetPackageSkills.Cli;

namespace DotnetPackageSkills.Tests;

public class CommandLineDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Diagnostic_write_overloads_share_one_buffer_until_invocation_finishes(bool standardError)
    {
        using var destination = new StringWriter();
        using var other = new StringWriter();
        var root = new RootCommand();
        root.SetAction(result =>
        {
            TextWriter writer = standardError
                ? result.InvocationConfiguration.Error
                : result.InvocationConfiguration.Output;
            writer.Write("  first\u001b");
            writer.Flush();
            writer.Write(']');
            writer.Write("52;c;".ToCharArray());
            writer.Write("SECRET".AsSpan());
            writer.WriteAsync("\a").GetAwaiter().GetResult();
            writer.Write('\ud83d');
            writer.FlushAsync().GetAwaiter().GetResult();
            Assert.Empty(destination.ToString());
            writer.Write('\udc69');
            writer.Write(new StringBuilder("🏽‍💻 e\u0301界"));
            writer.Write('\r');
            writer.Flush();
            writer.Write('\n');
            writer.WriteLine("    next  ");
            return 23;
        });

        var exitCode = CommandLineDiagnostics.Invoke(
            root.Parse([]), standardError ? other : destination, standardError ? destination : other);

        Assert.Equal(23, exitCode);
        Assert.Equal(
            $"  first👩🏽‍💻 e\u0301界{Environment.NewLine}    next  {Environment.NewLine}",
            destination.ToString());
        Assert.Empty(other.ToString());
    }

    [Theory]
    [InlineData("before\u001b[31mRED\u001b[0mafter", "beforeREDafter")]
    [InlineData("before\u001b]52;c;SECRET\u001b\\after", "beforeafter")]
    [InlineData("before\u001b]52;c;\r\nSECRET\aafter", "beforeafter")]
    [InlineData("before\u009d52;c;SECRET\u009cafter", "beforeafter")]
    [InlineData("before\u001bPSECRET\u001b\\after", "beforeafter")]
    [InlineData("before\u001b(0after", "beforeafter")]
    [InlineData("before\u001b]8;;https://example.invalid\a链接\u001b]8;;\aafter", "before链接after")]
    [InlineData("👩🏽‍💻 e\u0301 中文 🇨🇦", "👩🏽‍💻 e\u0301 中文 🇨🇦")]
    [InlineData("  \ue000first\r\n    next\ue000  \r\n", "  \ue000first\n    next\ue000  \n")]
    public void Every_write_boundary_preserves_text_without_leaking_escape_payloads(string text, string expected)
    {
        for (var split = 0; split <= text.Length; split++)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var root = new RootCommand();
            root.SetAction(result =>
            {
                var writer = result.InvocationConfiguration.Error;
                writer.Write(text.AsSpan(0, split));
                writer.Flush();
                writer.Write(text.ToCharArray(), split, text.Length - split);
                return 0;
            });

            Assert.Equal(0, CommandLineDiagnostics.Invoke(root.Parse([]), output, error));
            Assert.Equal(expected.Replace("\n", Environment.NewLine, StringComparison.Ordinal), error.ToString());
            Assert.Empty(output.ToString());
        }
    }

    [Theory]
    [InlineData("before\ue000\u001b", "before\ue000")]
    [InlineData("before\ue000\u001b[", "before\ue000")]
    [InlineData("before\ue000\u001b]unterminated", "before\ue000")]
    [InlineData("before\ue000\u009dunterminated", "before\ue000")]
    [InlineData("before\ue000\u001b]unterminated\r\n", "before\ue000\n")]
    [InlineData("before\ue000\u001b(", "before\ue000")]
    [InlineData("  before  \u001b]unterminated\r\n", "  before  \n")]
    [InlineData("\u001b]unterminated\r\n", "\n")]
    [InlineData("before\r\n\u001b]unterminated\r\n", "before\n")]
    [InlineData("before\u001b[\r\n", "before\n")]
    public void Unfinished_controls_preserve_valid_text_and_the_final_line_break(string text, string expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var root = new RootCommand();
        root.SetAction(result =>
        {
            foreach (var character in text)
            {
                result.InvocationConfiguration.Error.Write(character);
            }

            return 0;
        });

        Assert.Equal(0, CommandLineDiagnostics.Invoke(root.Parse([]), output, error));
        Assert.Equal(expected.Replace("\n", Environment.NewLine, StringComparison.Ordinal), error.ToString());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Diagnostic_capture_preserves_the_callers_line_endings_and_leaves_writers_open(string newLine)
    {
        using var output = new StringWriter() { NewLine = newLine };
        using var error = new StringWriter() { NewLine = newLine };
        var root = new RootCommand();
        root.SetAction(result =>
        {
            result.InvocationConfiguration.Output.WriteLine("  output");
            result.InvocationConfiguration.Output.WriteLine();
            result.InvocationConfiguration.Error.WriteLine("  error");
            return 0;
        });

        Assert.Equal(0, CommandLineDiagnostics.Invoke(root.Parse([]), output, error));
        output.Write("still open");
        error.Write("still open");

        Assert.Equal($"  output{newLine}{newLine}still open", output.ToString());
        Assert.Equal($"  error{newLine}still open", error.ToString());
    }

    [Fact]
    public void Default_framework_exception_diagnostics_are_sanitized_too()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var root = new RootCommand();
        root.SetAction((Func<ParseResult, int>)(_ =>
            throw new InvalidOperationException("first\u001b]52;c;SECRET\a\nsecond")));

        var exitCode = CommandLineDiagnostics.Invoke(root.Parse([]), output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains($"first{Environment.NewLine}second", error.ToString());
        Assert.DoesNotContain('\u001b', error.ToString());
        Assert.DoesNotContain('\a', error.ToString());
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Fact]
    public void Diagnostic_capture_does_not_change_canonical_values_JSON_or_global_console_streams()
    {
        const string Value = "original\u001b[31mvalue\u001b[0m";
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var report = new StringWriter();
        var consoleOutput = Console.Out;
        var consoleError = Console.Error;
        var argument = new Argument<string>("value");
        var root = new RootCommand { argument };
        root.SetAction(result =>
        {
            Assert.Same(consoleOutput, Console.Out);
            Assert.Same(consoleError, Console.Error);
            Assert.Equal(Value, result.GetValue(argument));
            new OutputWriter(report).WriteJson(new { Value = result.GetValue(argument) });
            return 0;
        });

        Assert.Equal(0, CommandLineDiagnostics.Invoke(root.Parse([Value]), output, error));

        using var json = JsonDocument.Parse(report.ToString());
        Assert.Equal(Value, json.RootElement.GetProperty("value").GetString());
        Assert.Empty(output.ToString());
        Assert.Empty(error.ToString());
    }
}
