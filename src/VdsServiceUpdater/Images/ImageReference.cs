using System.Text.RegularExpressions;

namespace VdsServiceUpdater.Images;

/// <summary>
/// Разобранная ссылка на docker-образ. Реестр и репозиторий нормализованы
/// (например, "nginx" → "docker.io/library/nginx"), чтобы ссылки можно было сравнивать.
/// </summary>
public sealed record ImageReference(string Registry, string Path, string? Tag, string? Digest)
{
    public const string DockerHub = "docker.io";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private const RegexOptions Opts = RegexOptions.CultureInvariant;

    private static readonly Regex TagRegex = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", Opts, RegexTimeout);
    private static readonly Regex DigestRegex = new(@"^sha256:[a-f0-9]{64}$", Opts, RegexTimeout);
    private static readonly Regex RegistryRegex = new(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[0-9]{1,5})?$", Opts, RegexTimeout);
    private static readonly Regex PathPartRegex = new(@"^[a-z0-9]+(?:(?:\.|_|__|-+)[a-z0-9]+)*$", Opts, RegexTimeout);

    /// <summary>Нормализованный репозиторий без тега: "ghcr.io/org/app".</summary>
    public string Repository => $"{Registry}/{Path}";

    /// <summary>Нормализованный образ с тегом или null, если тега нет.</summary>
    public string? Full => Tag is null ? null : $"{Repository}:{Tag}";

    public static bool LooksLikeRegistry(string firstComponent) =>
        firstComponent.Contains('.') || firstComponent.Contains(':') ||
        firstComponent.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeRegistry(string registry)
    {
        var r = registry.ToLowerInvariant();
        return r is "docker.io" or "index.docker.io" or "registry-1.docker.io" ? DockerHub : r;
    }

    /// <summary>Тег необязателен: так же разбираются значения image: из YAML.</summary>
    public static bool TryParse(string? value, out ImageReference? result, out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value)) return Fail("Образ не указан.", out error);
        if (value.Length > 255) return Fail("Образ длиннее 255 символов.", out error);
        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return Fail("Образ содержит пробелы или управляющие символы.", out error);

        var rest = value;

        string? digest = null;
        var at = rest.IndexOf('@');
        if (at >= 0)
        {
            digest = rest[(at + 1)..];
            rest = rest[..at];
            if (!IsMatch(DigestRegex, digest)) return Fail("Некорректный digest (ожидается sha256:<64 hex>).", out error);
        }

        string? tag = null;
        var colon = rest.LastIndexOf(':');
        if (colon > rest.LastIndexOf('/'))
        {
            tag = rest[(colon + 1)..];
            rest = rest[..colon];
            if (!IsMatch(TagRegex, tag)) return Fail("Некорректный тег.", out error);
        }

        if (rest.Length == 0) return Fail("Не указано имя образа.", out error);

        var parts = rest.Split('/');
        string registry;
        string[] pathParts;
        if (parts.Length > 1 && LooksLikeRegistry(parts[0]))
        {
            registry = NormalizeRegistry(parts[0]);
            pathParts = parts[1..];
            if (!IsMatch(RegistryRegex, registry)) return Fail("Некорректный адрес реестра.", out error);
        }
        else
        {
            registry = DockerHub;
            pathParts = parts;
        }

        if (pathParts.Any(p => !IsMatch(PathPartRegex, p)))
            return Fail("Некорректное имя репозитория (допустимы строчные a-z, 0-9, '.', '_', '-').", out error);

        if (registry == DockerHub && pathParts.Length == 1)
            pathParts = ["library", pathParts[0]];

        result = new ImageReference(registry, string.Join('/', pathParts), tag, digest);
        return true;
    }

    private static bool IsMatch(Regex regex, string input)
    {
        try { return regex.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
