namespace VdsServiceUpdater.Security;

public static class LogSanitizer
{
    /// <summary>Убирает управляющие символы (защита от подделки строк лога) и обрезает длину.</summary>
    public static string Sanitize(string? value, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var s = value.Length > maxLength ? value[..maxLength] + "…" : value;
        return string.Create(s.Length, s, (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
                span[i] = char.IsControl(src[i]) ? '?' : src[i];
        });
    }
}
