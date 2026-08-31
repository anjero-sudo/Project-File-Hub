using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectFileHub.Core.Services;

public enum MarkdownHtmlTheme
{
    Midnight,
    WarmGraphite,
    Light
}

/// <summary>
/// Produces a self-contained, script-limited Markdown reading surface for WebView2.
/// Source Markdown is always HTML encoded; raw HTML and remote resources are never emitted.
/// </summary>
public static partial class MarkdownHtmlRenderer
{
    public static string Render(string markdown, bool lightTheme = false) =>
        Render(markdown, lightTheme ? MarkdownHtmlTheme.Light : MarkdownHtmlTheme.Midnight);

    public static string Render(
        string markdown,
        MarkdownHtmlTheme theme,
        bool wrapCodeBlocks = false)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var blocks = MarkdownPreviewParser.Parse(markdown);
        var html = new StringBuilder(Math.Max(markdown.Length + 8_192, 16_384));
        html.Append(DocumentStart);
        var bodyClass = theme switch
        {
            MarkdownHtmlTheme.Light => "light",
            MarkdownHtmlTheme.WarmGraphite => "warm-graphite",
            _ => string.Empty
        };
        if (wrapCodeBlocks)
        {
            bodyClass = string.IsNullOrEmpty(bodyClass) ? "wrap-code" : $"{bodyClass} wrap-code";
        }

        html.Append("<body");
        if (!string.IsNullOrEmpty(bodyClass))
        {
            html.Append(" class=\"").Append(bodyClass).Append('"');
        }

        html.Append("><main id=\"document\">");

        var headingIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            switch (block.Kind)
            {
                case MarkdownPreviewBlockKind.Heading:
                    var level = Math.Clamp(block.Level, 1, 6);
                    var headingId = CreateUniqueHeadingId(block.Text, headingIds);
                    html.Append("<h").Append(level).Append(" id=\"")
                        .Append(EncodeAttribute(headingId)).Append("\">")
                        .Append(RenderInline(block.Text)).Append("</h").Append(level).Append('>');
                    break;

                case MarkdownPreviewBlockKind.Paragraph:
                    html.Append("<p>").Append(RenderInline(block.Text)).Append("</p>");
                    break;

                case MarkdownPreviewBlockKind.Quote:
                    html.Append("<blockquote>").Append(RenderInline(block.Text)).Append("</blockquote>");
                    break;

                case MarkdownPreviewBlockKind.Code:
                    AppendCodeBlock(html, block);
                    break;

                case MarkdownPreviewBlockKind.HorizontalRule:
                    html.Append("<hr>");
                    break;

                case MarkdownPreviewBlockKind.BulletListItem:
                    index = AppendList(html, blocks, index, ordered: false);
                    break;

                case MarkdownPreviewBlockKind.NumberedListItem:
                    index = AppendList(html, blocks, index, ordered: true);
                    break;
            }
        }

        html.Append(DocumentEnd);
        return html.ToString();
    }

    private static int AppendList(
        StringBuilder html,
        IReadOnlyList<MarkdownPreviewBlock> blocks,
        int startIndex,
        bool ordered)
    {
        var expectedKind = ordered
            ? MarkdownPreviewBlockKind.NumberedListItem
            : MarkdownPreviewBlockKind.BulletListItem;
        html.Append(ordered ? "<ol>" : "<ul>");

        var index = startIndex;
        while (index < blocks.Count && blocks[index].Kind == expectedKind)
        {
            var block = blocks[index];
            html.Append("<li>");
            if (block.IsChecked is not null)
            {
                html.Append("<span class=\"task-marker\" aria-hidden=\"true\">")
                    .Append(block.IsChecked.Value ? "☑" : "☐")
                    .Append("</span>");
            }

            html.Append(RenderInline(block.Text)).Append("</li>");
            index++;
        }

        html.Append(ordered ? "</ol>" : "</ul>");
        return index - 1;
    }

    private static void AppendCodeBlock(StringBuilder html, MarkdownPreviewBlock block)
    {
        var language = string.IsNullOrWhiteSpace(block.Language) ? "代码" : block.Language.Trim();
        html.Append("<section class=\"code-block\"><header><span>")
            .Append(WebUtility.HtmlEncode(language))
            .Append("</span><button type=\"button\" class=\"copy-code\" aria-label=\"复制整块代码\">复制整块</button></header><pre><code>")
            .Append(RenderCodeBlockText(block.Text))
            .Append("</code></pre></section>");
    }

    private static string RenderCodeBlockText(string text)
    {
        var html = new StringBuilder(text.Length + 64);
        var cursor = 0;
        foreach (Match match in MarkdownLinkRegex().Matches(text))
        {
            html.Append(WebUtility.HtmlEncode(text[cursor..match.Index]));

            var label = match.Groups["linkText"].Value;
            var target = match.Groups["linkTarget"].Value;
            html.Append('[')
                .Append("<a href=\"#\" data-pfh-href=\"")
                .Append(EncodeAttribute(target.Trim()))
                .Append("\">")
                .Append(WebUtility.HtmlEncode(label))
                .Append("</a>](")
                .Append(WebUtility.HtmlEncode(target))
                .Append(')');

            cursor = match.Index + match.Length;
        }

        html.Append(WebUtility.HtmlEncode(text[cursor..]));
        return html.ToString();
    }

    private static string RenderInline(string text)
    {
        var html = new StringBuilder(text.Length + 32);
        var cursor = 0;
        foreach (Match match in InlineMarkdownRegex().Matches(text))
        {
            html.Append(WebUtility.HtmlEncode(text[cursor..match.Index]));
            if (match.Groups["code"].Success)
            {
                html.Append("<code class=\"inline-code\">")
                    .Append(WebUtility.HtmlEncode(match.Groups["codeText"].Value))
                    .Append("</code>");
            }
            else if (match.Groups["link"].Success)
            {
                html.Append("<a href=\"#\" data-pfh-href=\"")
                    .Append(EncodeAttribute(match.Groups["linkTarget"].Value.Trim()))
                    .Append("\">")
                    .Append(WebUtility.HtmlEncode(match.Groups["linkText"].Value))
                    .Append("</a>");
            }
            else if (match.Groups["strong"].Success)
            {
                html.Append("<strong>")
                    .Append(WebUtility.HtmlEncode(match.Groups["strongText"].Value))
                    .Append("</strong>");
            }
            else
            {
                html.Append("<em>")
                    .Append(WebUtility.HtmlEncode(match.Groups["emText"].Value))
                    .Append("</em>");
            }

            cursor = match.Index + match.Length;
        }

        html.Append(WebUtility.HtmlEncode(text[cursor..]));
        return html.ToString();
    }

    private static string CreateUniqueHeadingId(string heading, IDictionary<string, int> headingIds)
    {
        var slug = CreateHeadingId(heading);
        if (!headingIds.TryGetValue(slug, out var count))
        {
            headingIds[slug] = 1;
            return slug;
        }

        count++;
        headingIds[slug] = count;
        return $"{slug}-{count}";
    }

    private static string CreateHeadingId(string heading)
    {
        var result = new StringBuilder(heading.Length);
        var pendingSeparator = false;
        foreach (var character in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && result.Length > 0)
                {
                    result.Append('-');
                }

                result.Append(character);
                pendingSeparator = false;
            }
            else if (char.IsWhiteSpace(character) || character is '-' or '_')
            {
                pendingSeparator = result.Length > 0;
            }
        }

        return result.Length == 0 ? "section" : result.ToString();
    }

    private static string EncodeAttribute(string value) => WebUtility.HtmlEncode(value);

    [GeneratedRegex(
        @"(?<code>`(?<codeText>[^`\r\n]+)`)|(?<link>\[(?<linkText>[^\]\r\n]+)\]\((?<linkTarget>[^)\r\n]+)\))|(?<strong>\*\*(?<strongText>[^*\r\n]+)\*\*)|(?<em>\*(?<emText>[^*\r\n]+)\*)")]
    private static partial Regex InlineMarkdownRegex();

    [GeneratedRegex(@"\[(?<linkText>[^\]\r\n]+)\]\((?<linkTarget>[^)\r\n]+)\)")]
    private static partial Regex MarkdownLinkRegex();

    private const string DocumentStart = """
        <!doctype html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8">
          <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src 'none'; media-src 'none'; connect-src 'none'; frame-src 'none'; object-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <style>
            :root { color-scheme: dark; font-family: "Segoe UI", "Microsoft YaHei UI", sans-serif; }
            * { box-sizing: border-box; }
            html, body { margin: 0; min-height: 100%; background: #0f1419; color: #c7d0da; }
            body { padding: 36px 48px 72px; font-size: 16px; line-height: 1.72; user-select: text; overflow-wrap: anywhere; }
            main { width: min(920px, 100%); margin: 0 auto; }
            h1, h2, h3, h4, h5, h6 { color: #eef5fb; line-height: 1.25; margin: 1.7em 0 .65em; letter-spacing: -.015em; }
            h1 { font-size: 2rem; border-bottom: 1px solid #293541; padding-bottom: .4em; }
            h2 { font-size: 1.55rem; }
            h3 { font-size: 1.25rem; }
            p, ul, ol, blockquote { margin: .8em 0; }
            li { padding-left: .2em; margin: .2em 0; }
            li::marker, a { color: #2fc8ff; }
            a { text-decoration-thickness: 1px; text-underline-offset: 3px; cursor: pointer; }
            strong { color: #f5f8fb; font-weight: 650; }
            blockquote { margin-left: 0; padding: .35em 1em; border-left: 3px solid #20bde8; color: #9fabb7; background: #111a22; }
            hr { border: 0; border-top: 1px solid #2a3540; margin: 2em 0; }
            .inline-code { color: #7de1ff; background: #17212a; border: 1px solid #263745; border-radius: 5px; padding: .08em .32em; font-family: "Cascadia Code", Consolas, monospace; font-size: .92em; }
            .task-marker { color: #25c6f7; display: inline-block; width: 1.45em; margin-left: -1.3em; }
            .code-block { margin: 1.2em 0; border: 1px solid #2a3946; border-radius: 10px; overflow: hidden; background: #182129; }
            .code-block header { min-height: 38px; padding: 6px 8px 6px 14px; display: flex; align-items: center; justify-content: space-between; color: #57d4ff; font-size: 11px; letter-spacing: .07em; text-transform: uppercase; border-bottom: 1px solid #2a3946; background: #151e26; user-select: none; }
            .copy-code { border: 1px solid #385166; border-radius: 6px; color: #d8e4ed; background: #202d38; padding: 5px 9px; font: 12px "Segoe UI", sans-serif; cursor: pointer; }
            .copy-code:hover { border-color: #25c6f7; color: #fff; }
            .copy-code:focus-visible, a:focus-visible { outline: 2px solid #25c6f7; outline-offset: 2px; }
            pre { margin: 0; padding: 18px; overflow: auto; white-space: pre; line-height: 1.6; }
            pre code { color: #d6dde5; font: 13px/1.6 "Cascadia Code", Consolas, monospace; tab-size: 4; }
            body.wrap-code pre { white-space: pre-wrap; overflow-wrap: anywhere; word-break: break-word; }
            body.light { background: #f4f7fb; color: #42546a; }
            body.light h1, body.light h2, body.light h3, body.light h4, body.light h5, body.light h6, body.light strong { color: #102033; }
            body.light h1, body.light hr { border-color: #c8d5e2; }
            body.light a, body.light li::marker, body.light .task-marker { color: #0369a1; }
            body.light blockquote { color: #526278; background: #e8eff6; border-left-color: #0284c7; }
            body.light .inline-code { color: #0369a1; background: #e8eff6; border-color: #c8d5e2; }
            body.light .code-block { border-color: #bdcbd8; background: #17212a; }
            body.warm-graphite { background: #11110f; color: #b9b2a7; }
            body.warm-graphite h1, body.warm-graphite h2, body.warm-graphite h3, body.warm-graphite h4, body.warm-graphite h5, body.warm-graphite h6, body.warm-graphite strong { color: #f2eee6; }
            body.warm-graphite h1, body.warm-graphite hr { border-color: #38352f; }
            body.warm-graphite a, body.warm-graphite li::marker, body.warm-graphite .task-marker { color: #d59a52; }
            body.warm-graphite blockquote { color: #b9b2a7; background: #181816; border-left-color: #d59a52; }
            body.warm-graphite .inline-code { color: #edb56d; background: #22211e; border-color: #38352f; }
            body.warm-graphite .code-block { border-color: #38352f; background: #181816; }
            body.warm-graphite .code-block header { color: #d59a52; border-bottom-color: #38352f; background: #22211e; }
            body.warm-graphite .copy-code { border-color: #504a40; color: #f2eee6; background: #2b2925; }
            body.warm-graphite .copy-code:hover { border-color: #edb56d; color: #fffaf0; }
            body.warm-graphite .copy-code:focus-visible, body.warm-graphite a:focus-visible { outline-color: #edb56d; }
            body.warm-graphite pre code { color: #f2eee6; }
            @media (max-width: 640px) { body { padding: 24px 22px 56px; } }
          </style>
        </head>
        """;

    private const string DocumentEnd = """
        </main>
        <script>
          (() => {
            const send = (message) => {
              if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(message);
            };
            document.addEventListener('click', (event) => {
              const copyButton = event.target.closest('.copy-code');
              if (copyButton) {
                const code = copyButton.closest('.code-block').querySelector('code');
                send({ type: 'copy-code', text: code ? code.textContent : '' });
                copyButton.textContent = '已复制';
                window.setTimeout(() => copyButton.textContent = '复制整块', 1200);
                return;
              }

              const link = event.target.closest('a[data-pfh-href]');
              if (!link) return;
              event.preventDefault();
              const target = link.dataset.pfhHref || '';
              if (target.startsWith('#')) {
                const anchor = decodeURIComponent(target.slice(1)).toLowerCase();
                const element = document.getElementById(anchor);
                if (element) element.scrollIntoView({ behavior: 'smooth', block: 'start' });
                return;
              }
              send({ type: 'open-link', href: target });
            });
            document.addEventListener('dragstart', (event) => {
              if (event.target.closest('a')) event.preventDefault();
            });
          })();
        </script>
        </body></html>
        """;
}
