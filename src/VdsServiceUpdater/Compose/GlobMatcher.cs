using System.Text.RegularExpressions;

namespace VdsServiceUpdater.Compose;

/// <summary>Glob без учёта регистра: '*' = любая последовательность, '?' = один символ.</summary>
public static class GlobMatcher
{
    public static bool IsMatch(string pattern, string input)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}
