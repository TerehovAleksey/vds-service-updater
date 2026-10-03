using System.Text.Json.Serialization;

namespace VdsServiceUpdater.Deploy;

/// <summary>Результат по одному сервису. Status: updated | unchanged | skipped | planned | failed | rolled_back | rollback_failed.</summary>
public sealed record ServiceReport(
    string Target,
    string Service,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviousImage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NewImage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message);

/// <summary>Тело ответа /api/v1/deploy. Status: updated | unchanged | dry_run | failed | error.</summary>
public sealed record DeployResponse(
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message,
    IReadOnlyList<ServiceReport> Results,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings,
    long DurationMs);

public sealed record DeployOutcome(int StatusCode, DeployResponse Body);
