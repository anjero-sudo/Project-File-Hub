using System.Text;
using TinyPinyin;

namespace ProjectFileHub.Core.Services;

public static class FileNameSearch
{
    public static bool Matches(string name, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        query = query.Trim();
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        // Initials only: keep ASCII words/numbers intact in mixed filenames.
        // Do not expand a Cartesian product of polyphonic pronunciations.
        if (!query.Any(char.IsAsciiLetter)) return false;
        var initials = new StringBuilder(name.Length);
        var hasChinese = false;
        foreach (var character in name)
        {
            if (PinyinHelper.IsChinese(character))
            {
                initials.Append(PinyinHelper.GetPinyin(character)[0]);
                hasChinese = true;
            }
            else initials.Append(character);
        }
        return hasChinese && initials.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
