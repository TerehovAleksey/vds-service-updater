using System.Text.RegularExpressions;

namespace VdsServiceUpdater.Docker;

/// <summary>Подготовка вывода docker для логов и ответа: маскировка секретов и хвост ограниченной длины.</summary>
public static class OutputSanitizer
{
    private static readonly Regex SecretPattern = new(
        @"\b(password|passwd|token|secret|authorization)\b\s*[:=]\s*\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string Mask(string text, IEnumerable<string?> secrets)
    {
        foreach (var s in secrets)
            if (!string.IsNullOrEmpty(s)) text = text.Replace(s, "***", StringComparison.Ordinal);
        return SecretPattern.Replace(text, "$1=***");
    }

    public static string Tail(string text, int max) =>
        text.Length <= max ? text : "…" + text[^max..];

    public static string Prepare(string text, IEnumerable<string?> secrets, int max = 1500) =>
        Tail(Mask(text, secrets).Trim(), max);
}
