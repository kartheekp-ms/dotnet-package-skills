using System.CommandLine;

namespace DotnetPackageSkills.Cli;

/// <summary>Sanitizes framework diagnostics without changing arguments or application console output.</summary>
internal static class CommandLineDiagnostics
{
    public static int Invoke(ParseResult result, TextWriter output, TextWriter error)
    {
        // Suggestions use Output, not Error. Buffer both through the whole invocation so
        // split escape sequences, surrogate pairs, and Flush calls cannot bypass sanitizing.
        using var capturedOutput = new StringWriter(output.FormatProvider) { NewLine = output.NewLine };
        using var capturedError = new StringWriter(error.FormatProvider) { NewLine = error.NewLine };

        try
        {
            return result.Invoke(new InvocationConfiguration
            {
                Output = capturedOutput,
                Error = capturedError,
            });
        }
        finally
        {
            Write(error, capturedError.ToString());
            Write(output, capturedOutput.ToString());
        }
    }

    private static void Write(TextWriter writer, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var clean = TerminalText.Sanitize(text, multiline: true, trim: false);

        if (text.EndsWith('\n') && !clean.EndsWith('\n'))
        {
            // An unterminated control string can also consume the framework's final newline.
            clean += "\n";
        }

        writer.Write(clean.Replace("\n", writer.NewLine, StringComparison.Ordinal));
    }
}
