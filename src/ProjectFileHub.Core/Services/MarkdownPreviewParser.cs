using System.Text;
using System.Text.RegularExpressions;

namespace ProjectFileHub.Core.Services;

public enum MarkdownPreviewBlockKind
{
    Heading,
    Paragraph,
    BulletListItem,
    NumberedListItem,
    Quote,
    Code,
    HorizontalRule
}

public sealed record MarkdownPreviewBlock(
    MarkdownPreviewBlockKind Kind,
    string Text,
    int Level = 0,
    string? Marker = null,
    string? Language = null,
    bool? IsChecked = null);

/// <summary>
/// Parses the structural subset needed by the local Markdown reading preview.
/// It never resolves links, loads remote content, or executes embedded markup.
/// </summary>
public static partial class MarkdownPreviewParser
{
    public static IReadOnlyList<MarkdownPreviewBlock> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var blocks = new List<MarkdownPreviewBlock>();
        var paragraph = new List<string>();
        var code = new StringBuilder();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var inCodeFence = false;
        var fenceMarker = string.Empty;
        var codeLanguage = string.Empty;

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            blocks.Add(new MarkdownPreviewBlock(
                MarkdownPreviewBlockKind.Paragraph,
                string.Join(' ', paragraph)));
            paragraph.Clear();
        }

        void FlushCode()
        {
            blocks.Add(new MarkdownPreviewBlock(
                MarkdownPreviewBlockKind.Code,
                code.ToString().TrimEnd('\n'),
                Language: string.IsNullOrWhiteSpace(codeLanguage) ? null : codeLanguage));
            code.Clear();
            codeLanguage = string.Empty;
        }

        foreach (var line in lines)
        {
            var trimmedStart = line.TrimStart();
            var fence = FenceRegex().Match(trimmedStart);
            if (fence.Success)
            {
                var currentMarker = fence.Groups[1].Value;
                if (!inCodeFence)
                {
                    FlushParagraph();
                    inCodeFence = true;
                    fenceMarker = currentMarker;
                    codeLanguage = fence.Groups[2].Value.Trim();
                }
                else if (currentMarker[0] == fenceMarker[0])
                {
                    inCodeFence = false;
                    FlushCode();
                    fenceMarker = string.Empty;
                }
                else
                {
                    code.AppendLine(line);
                }

                continue;
            }

            if (inCodeFence)
            {
                code.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            var setext = SetextHeadingRegex().Match(line);
            if (setext.Success && paragraph.Count > 0)
            {
                var title = string.Join(' ', paragraph);
                paragraph.Clear();
                blocks.Add(new MarkdownPreviewBlock(
                    MarkdownPreviewBlockKind.Heading,
                    title,
                    setext.Groups[1].Value[0] == '=' ? 1 : 2));
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                blocks.Add(new MarkdownPreviewBlock(
                    MarkdownPreviewBlockKind.Heading,
                    heading.Groups[2].Value.Trim().TrimEnd('#').TrimEnd(),
                    heading.Groups[1].Value.Length));
                continue;
            }

            if (HorizontalRuleRegex().IsMatch(line))
            {
                FlushParagraph();
                blocks.Add(new MarkdownPreviewBlock(MarkdownPreviewBlockKind.HorizontalRule, string.Empty));
                continue;
            }

            var quote = QuoteRegex().Match(line);
            if (quote.Success)
            {
                FlushParagraph();
                blocks.Add(new MarkdownPreviewBlock(
                    MarkdownPreviewBlockKind.Quote,
                    quote.Groups[1].Value.Trim()));
                continue;
            }

            var bullet = BulletRegex().Match(line);
            if (bullet.Success)
            {
                FlushParagraph();
                var taskMarker = bullet.Groups[2].Value;
                blocks.Add(new MarkdownPreviewBlock(
                    MarkdownPreviewBlockKind.BulletListItem,
                    bullet.Groups[3].Value.Trim(),
                    Marker: bullet.Groups[1].Value,
                    IsChecked: taskMarker.Length == 0
                        ? null
                        : taskMarker.Equals("x", StringComparison.OrdinalIgnoreCase)));
                continue;
            }

            var numbered = NumberedRegex().Match(line);
            if (numbered.Success)
            {
                FlushParagraph();
                blocks.Add(new MarkdownPreviewBlock(
                    MarkdownPreviewBlockKind.NumberedListItem,
                    numbered.Groups[2].Value.Trim(),
                    Marker: numbered.Groups[1].Value));
                continue;
            }

            paragraph.Add(line.Trim());
        }

        FlushParagraph();
        if (inCodeFence || code.Length > 0)
        {
            FlushCode();
        }

        return blocks;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*(=+|-+)\s*$")]
    private static partial Regex SetextHeadingRegex();

    [GeneratedRegex(@"^\s*((?:`{3,})|(?:~{3,}))\s*([^`]*)$")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$")]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(@"^\s*>\s?(.*)$")]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"^\s*([-*+])\s+(?:\[([ xX])\]\s+)?(.+)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^\s*(\d+[.)])\s+(.+)$")]
    private static partial Regex NumberedRegex();
}
