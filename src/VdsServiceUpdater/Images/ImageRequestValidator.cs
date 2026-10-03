using Microsoft.Extensions.Options;
using VdsServiceUpdater.Options;

namespace VdsServiceUpdater.Images;

public sealed record ImageValidationResult(ImageReference? Image, int StatusCode = 0, string? Code = null, string? Message = null);

/// <summary>Проверяет образ из запроса: формат, обязательный тег, разрешённые префиксы, запрет latest.</summary>
public sealed class ImageRequestValidator
{
    private readonly string[] _prefixes;
    private readonly bool _allowLatest;

    public ImageRequestValidator(IOptions<UpdaterOptions> options)
    {
        _prefixes = options.Value.Deploy.AllowedImagePrefixes.Select(NormalizePrefix).ToArray();
        _allowLatest = options.Value.Deploy.AllowLatestTag;
    }

    public ImageValidationResult Validate(string? image)
    {
        if (!ImageReference.TryParse(image, out var r, out var error) || r is null)
            return Reject(422, "invalid_image", error ?? "Некорректный образ.");
        if (r.Tag is null)
            return Reject(422, "tag_required", "Укажите тег образа (например app:1.4.2).");
        if (r.Digest is not null)
            return Reject(422, "digest_not_supported", "Образы с @digest не поддерживаются, используйте тег.");
        if (!IsAllowed(r.Repository))
            return Reject(403, "image_not_allowed", "Образ не входит в список разрешённых префиксов.");
        if (!_allowLatest && r.Tag.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return Reject(422, "latest_tag_forbidden", "Тег latest запрещён конфигурацией.");
        return new ImageValidationResult(r);
    }

    private bool IsAllowed(string repository) =>
        _prefixes.Any(p => repository == p || repository.StartsWith(p + "/", StringComparison.Ordinal));

    /// <summary>"myorg/" → "docker.io/myorg"; "ghcr.io/Org/" → "ghcr.io/org"; граница всегда по символу '/'.</summary>
    internal static string NormalizePrefix(string prefix)
    {
        var p = prefix.Trim().ToLowerInvariant().Trim('/');
        var slash = p.IndexOf('/');
        var first = slash < 0 ? p : p[..slash];
        if (!ImageReference.LooksLikeRegistry(first))
            return $"{ImageReference.DockerHub}/{p}";
        var rest = slash < 0 ? "" : p[slash..];
        return ImageReference.NormalizeRegistry(first) + rest;
    }

    private static ImageValidationResult Reject(int status, string code, string message) => new(null, status, code, message);
}
