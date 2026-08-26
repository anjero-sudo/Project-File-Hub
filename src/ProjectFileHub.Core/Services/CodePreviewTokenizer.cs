using System.Text.RegularExpressions;

namespace ProjectFileHub.Core.Services;

public enum CodePreviewTokenKind
{
    Plain,
    Comment,
    String,
    Number,
    Keyword
}

public sealed record CodePreviewToken(string Text, CodePreviewTokenKind Kind);

public static class CodePreviewTokenizer
{
    private static readonly Regex TokenRegex = new(
        @"(?<comment>/\*[\s\S]*?\*/|//[^\r\n]*|(?m:^[\t ]*#[^\r\n]*))|" +
        @"(?<string>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|`(?:\\.|[^`\\])*`)|" +
        @"(?<number>\b(?:0[xX][0-9a-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\b)|" +
        @"(?<keyword>\b(?:abstract|and|as|async|await|base|bool|break|byte|case|catch|char|class|const|continue|" +
        @"decimal|default|delegate|do|double|else|enum|event|explicit|export|extends|extern|false|finally|fixed|float|" +
        @"for|foreach|from|function|get|global|go|goto|if|implements|import|in|init|instanceof|int|interface|internal|" +
        @"is|let|lock|long|namespace|native|new|not|null|object|operator|or|out|override|package|params|partial|private|" +
        @"protected|public|readonly|record|ref|required|return|sbyte|sealed|set|short|sizeof|static|string|struct|" +
        @"super|switch|synchronized|this|throw|throws|transient|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|" +
        @"using|var|virtual|void|volatile|when|where|while|with|yield|None|False|True|def|elif|except|lambda|pass|" +
        @"raise|self|nonlocal|del|match|asyncio|fn|impl|mut|pub|trait|type|select|defer|map|chan|range)\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<CodePreviewToken> Tokenize(string source)
    {
        if (source.Length == 0)
        {
            return [];
        }

        var tokens = new List<CodePreviewToken>();
        var position = 0;
        foreach (Match match in TokenRegex.Matches(source))
        {
            if (match.Index > position)
            {
                tokens.Add(new CodePreviewToken(source[position..match.Index], CodePreviewTokenKind.Plain));
            }

            var kind = match.Groups["comment"].Success
                ? CodePreviewTokenKind.Comment
                : match.Groups["string"].Success
                    ? CodePreviewTokenKind.String
                    : match.Groups["number"].Success
                        ? CodePreviewTokenKind.Number
                        : CodePreviewTokenKind.Keyword;
            tokens.Add(new CodePreviewToken(match.Value, kind));
            position = match.Index + match.Length;
        }

        if (position < source.Length)
        {
            tokens.Add(new CodePreviewToken(source[position..], CodePreviewTokenKind.Plain));
        }

        return tokens;
    }
}
