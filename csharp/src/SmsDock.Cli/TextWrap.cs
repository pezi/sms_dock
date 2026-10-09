using System.Text;
using System.Text.RegularExpressions;

namespace SmsDock.Cli;

/// <summary>Breaks text into lines the way Python's <c>textwrap</c> does with its defaults.</summary>
internal static partial class TextWrap
{
    /// <summary>Returns the lines of a text, each at most <paramref name="width"/> characters long.</summary>
    public static List<string> Wrap(string text, int width)
    {
        var normalized = Whitespace().Replace(text, " ").Trim(' ');
        var chunks = WordSeparator().Split(normalized).Where(chunk => chunk.Length > 0).Reverse().ToList();
        var lines = new List<string>();
        while (chunks.Count > 0)
        {
            var line = new List<string>();
            var length = 0;
            if (chunks[^1].Trim(' ').Length == 0 && lines.Count > 0)
            {
                chunks.RemoveAt(chunks.Count - 1);
            }
            while (chunks.Count > 0 && length + chunks[^1].Length <= width)
            {
                length += chunks[^1].Length;
                line.Add(chunks[^1]);
                chunks.RemoveAt(chunks.Count - 1);
            }
            if (chunks.Count > 0 && chunks[^1].Length > width)
            {
                BreakLongWord(chunks, line, length, width);
            }
            if (line.Count > 0 && line[^1].Trim(' ').Length == 0)
            {
                line.RemoveAt(line.Count - 1);
            }
            if (line.Count > 0)
            {
                lines.Add(string.Concat(line));
            }
        }
        return lines;
    }

    /// <summary>Returns a text as one string with line breaks.</summary>
    public static string Fill(string text, int width) => string.Join("\n", Wrap(text, width));

    private static void BreakLongWord(List<string> chunks, List<string> line, int length, int width)
    {
        var spaceLeft = width < 1 ? 1 : width - length;
        var chunk = chunks[^1];
        var end = spaceLeft;
        if (chunk.Length > spaceLeft && spaceLeft > 0)
        {
            var hyphen = chunk.LastIndexOf('-', spaceLeft - 1);
            if (hyphen > 0 && chunk[..hyphen].Any(character => character != '-'))
            {
                end = hyphen + 1;
            }
        }
        end = Math.Clamp(end, 0, chunk.Length);
        line.Add(chunk[..end]);
        chunks[^1] = chunk[end..];
    }

    [GeneratedRegex(@"[ \t\n\r\f\v]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(
        """
        ([\t\n\x0b\x0c\r ]+|(?<=[\w!"'&.,?])-{2,}(?=\w)|[^\t\n\x0b\x0c\r ]+?(?:-(?:(?<=[^\d\W]{2}-)|(?<=[^\d\W]-[\w!"'&.,?]-))(?=[^\d\W]-?[^\d\W])|(?=[\t\n\x0b\x0c\r ]|\z)|(?<=[\w!"'&.,?])(?=-{2,}\w)))
        """)]
    private static partial Regex WordSeparator();
}
