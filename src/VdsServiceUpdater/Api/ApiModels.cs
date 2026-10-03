namespace VdsServiceUpdater.Api;

public sealed record DeployRequest(string? Image, bool DryRun = false);

/// <summary>Единый формат ошибок: { "status": "error", "code": "...", "message": "..." }.</summary>
public sealed record ApiError(string Status, string Code, string Message)
{
    public static ApiError Of(string code, string message) => new("error", code, message);
}
