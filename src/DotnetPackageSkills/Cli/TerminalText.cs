using System.Globalization;
using System.Text;

namespace DotnetPackageSkills.Cli;

/// <summary>Plain terminal text, measured and split at grapheme and display-cell boundaries.</summary>
internal static class TerminalText
{
    public static string Sanitize(string? text, bool multiline = false, bool trim = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var clean = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length;)
        {
            var character = text[index];
            if (character is '\x1b' or '\x9b' or '\x9d' or '\x90' or '\x98' or '\x9e' or '\x9f')
            {
                index = SkipEscape(text, index);
                continue;
            }

            if (character is '\r' or '\n' or '\u2028' or '\u2029')
            {
                clean.Append(multiline ? '\n' : ' ');
                index += character == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
                continue;
            }

            if (character == '\t')
            {
                clean.Append(' ');
                index++;
                continue;
            }

            var status = Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                consumed = 1;
            }

            index += consumed;
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Control ||
                category == UnicodeCategory.Format && rune.Value is not (0x200c or 0x200d or >= 0xe0020 and <= 0xe007f))
            {
                continue;
            }

            clean.Append(Rune.IsWhiteSpace(rune) ? " " : rune.ToString());
        }

        var result = clean.ToString();
        return trim ? result.Trim() : result;
    }

    public static int Width(string text) => Elements(text).Sum(element => CellWidth(element));

    public static IEnumerable<string> Elements(string text)
    {
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            yield return elements.GetTextElement();
        }
    }

    public static int CellWidth(string element)
    {
        var width = 0;
        var emojiPresentation = false;
        foreach (var rune in element.EnumerateRunes())
        {
            emojiPresentation |= rune.Value is 0xfe0f or 0x20e3;
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or
                UnicodeCategory.Format or UnicodeCategory.Control)
            {
                continue;
            }

            width = Math.Max(width, IsWide(rune.Value) ? 2 : 1);
        }

        return width > 0 && emojiPresentation ? 2 : width;
    }

    public static string PadRight(string text, int width) =>
        text + new string(' ', Math.Max(0, width - Width(text)));

    public static string Clip(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (Width(text) <= width)
        {
            return text;
        }

        var suffix = new string('.', Math.Min(3, width));
        var clipped = new StringBuilder();
        var available = width - suffix.Length;
        foreach (var element in Elements(text))
        {
            var cells = CellWidth(element);
            if (cells > available)
            {
                break;
            }

            clipped.Append(element);
            available -= cells;
        }

        return clipped.Append(suffix).ToString();
    }

    public static IReadOnlyList<string> Wrap(string text, int width) => Wrap(text, width, width);

    public static IReadOnlyList<string> Wrap(string text, int firstLineWidth, int continuationWidth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(firstLineWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(continuationWidth, 1);
        var width = firstLineWidth;
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var line = new StringBuilder();
            var cells = 0;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var wordWidth = Width(word);
                if (cells > 0 && cells + 1 + wordWidth <= width)
                {
                    line.Append(' ').Append(word);
                    cells += 1 + wordWidth;
                    continue;
                }

                if (cells > 0)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                    cells = 0;
                    width = continuationWidth;
                }

                foreach (var element in Elements(word))
                {
                    var elementWidth = CellWidth(element);
                    if (cells > 0 && cells + elementWidth > width)
                    {
                        lines.Add(line.ToString());
                        line.Clear();
                        cells = 0;
                        width = continuationWidth;
                    }

                    if (elementWidth > width)
                    {
                        throw new ArgumentException("The column is narrower than a single display grapheme.", nameof(width));
                    }

                    line.Append(element);
                    cells += elementWidth;
                }
            }

            lines.Add(line.ToString());
            width = continuationWidth;
        }

        return lines;
    }

    private static int SkipEscape(string text, int index)
    {
        var kind = text[index++];
        if (kind == '\x1b')
        {
            if (index == text.Length)
            {
                return index;
            }

            kind = text[index++];
        }

        if (kind is '[' or '\x9b')
        {
            while (index < text.Length)
            {
                if (text[index++] is >= '\x40' and <= '\x7e')
                {
                    break;
                }
            }
        }
        else if (kind is ']' or 'P' or 'X' or '^' or '_' or '\x9d' or '\x90' or '\x98' or '\x9e' or '\x9f')
        {
            while (index < text.Length)
            {
                if (text[index++] is '\a' or '\x9c')
                {
                    break;
                }

                if (text[index - 1] == '\x1b' && index < text.Length && text[index] == '\\')
                {
                    return index + 1;
                }
            }
        }
        else if (kind is >= '\x20' and <= '\x2f')
        {
            while (index < text.Length && text[index] is >= '\x20' and <= '\x2f')
            {
                index++;
            }

            if (index < text.Length && text[index] is >= '\x30' and <= '\x7e')
            {
                index++;
            }
        }

        return index;
    }

    private static bool IsWide(int value) =>
        value != 0x303f && value is >= 0x1100 and <= 0x115f or
            0x231a or 0x231b or 0x2329 or 0x232a or
            >= 0x23e9 and <= 0x23ec or 0x23f0 or 0x23f3 or 0x25fd or 0x25fe or
            0x2614 or 0x2615 or >= 0x2648 and <= 0x2653 or 0x267f or 0x2693 or
            0x26a1 or 0x26aa or 0x26ab or 0x26bd or 0x26be or 0x26c4 or 0x26c5 or
            0x26ce or 0x26d4 or 0x26ea or 0x26f2 or 0x26f3 or 0x26f5 or 0x26fa or
            0x26fd or 0x2705 or 0x270a or 0x270b or 0x2728 or 0x274c or 0x274e or
            >= 0x2753 and <= 0x2755 or 0x2757 or >= 0x2795 and <= 0x2797 or
            0x27b0 or 0x27bf or 0x2b1b or 0x2b1c or 0x2b50 or 0x2b55 or
            >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3 or
            >= 0xf900 and <= 0xfaff or >= 0xfe10 and <= 0xfe19 or
            >= 0xfe30 and <= 0xfe6f or >= 0xff00 and <= 0xff60 or
            >= 0xffe0 and <= 0xffe6 or >= 0x16fe0 and <= 0x18dff or
            >= 0x1aff0 and <= 0x1b2ff or 0x1f004 or 0x1f0cf or 0x1f18e or
            >= 0x1f191 and <= 0x1f19a or >= 0x1f1e6 and <= 0x1f1ff or
            >= 0x1f200 and <= 0x1f251 or >= 0x1f300 and <= 0x1faff or
            >= 0x20000 and <= 0x3fffd;
}
