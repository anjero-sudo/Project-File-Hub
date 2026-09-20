using System.Text;

namespace ProjectFileHub.Core.Services;

internal static class MarkdownTableParser
{
    public static bool TryRead(string[] lines, int start, out MarkdownTable? table, out int end)
    {
        table = null;
        end = start;
        if (start + 1 >= lines.Length || !lines[start].Contains('|')) return false;
        var headers = SplitRow(lines[start]);
        var dividers = SplitRow(lines[start + 1]);
        if (headers.Count is 0 or > 128 || dividers.Count != headers.Count) return false;
        var alignments = new List<string>();
        foreach (var divider in dividers)
        {
            var dashes = divider.Trim(':');
            if (dashes.Length == 0 || dashes.Any(c => c != '-')) return false;
            alignments.Add(divider.StartsWith(':') && divider.EndsWith(':') ? "center"
                : divider.EndsWith(':') ? "right" : "left");
        }

        var rows = new List<IReadOnlyList<string>>();
        end = start + 1;
        while (end + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[end + 1])
            && lines[end + 1].Contains('|') && !lines[end + 1].TrimStart().StartsWith("```", StringComparison.Ordinal)
            && !lines[end + 1].TrimStart().StartsWith("~~~", StringComparison.Ordinal))
        {
            var cells = SplitRow(lines[++end]);
            rows.Add(Enumerable.Range(0, headers.Count).Select(i => i < cells.Count ? cells[i] : string.Empty).ToArray());
        }
        table = new MarkdownTable(headers, alignments, rows);
        return true;
    }

    private static List<string> SplitRow(string line)
    {
        line = line.Trim();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var start = line.StartsWith('|') ? 1 : 0;
        var lastWasSeparator = false;
        for (var i = start; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length && line[i + 1] is '|' or '\\')
            {
                cell.Append(line[++i]);
                lastWasSeparator = false;
            }
            else if (c == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
                lastWasSeparator = true;
            }
            else
            {
                cell.Append(c);
                lastWasSeparator = false;
            }
        }
        if (!lastWasSeparator) cells.Add(cell.ToString().Trim());
        return cells;
    }
}
