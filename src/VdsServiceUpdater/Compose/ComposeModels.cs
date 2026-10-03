using VdsServiceUpdater.Images;

namespace VdsServiceUpdater.Compose;

/// <summary>Сервис с ключом image: в compose/stack-файле.</summary>
/// <param name="Line">Номер строки (с 1) с ключом image:.</param>
/// <param name="Reference">Разобранный образ; null, если значение не удалось разобрать (например "${IMAGE}").</param>
/// <param name="UsesVariable">В значении есть "$": переменная окружения (тег или целиком).</param>
public sealed record ServiceImage(string Service, string RawValue, int Line, ImageReference? Reference, bool UsesVariable);

public enum ServiceOutcome
{
    Updated,
    Unchanged,
    SkippedProtected,
    /// <summary>image: name:${TAG}: автоматически править нельзя.</summary>
    SkippedVariable,
    /// <summary>image: name:tag@sha256:...: закреплён по digest.</summary>
    SkippedPinned
}

public sealed record ServiceChange(string Service, ServiceOutcome Outcome, string PreviousImage, string NewImage, int Line);

public sealed record EditResult(string Content, IReadOnlyList<ServiceChange> Changes)
{
    public bool HasUpdates => Changes.Any(c => c.Outcome == ServiceOutcome.Updated);
}

/// <summary>Ошибка работы с файлом. Code: invalid_yaml, no_services, unsupported_format, verification_failed, file_not_found, file_changed.</summary>
public sealed class ComposeFileException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
