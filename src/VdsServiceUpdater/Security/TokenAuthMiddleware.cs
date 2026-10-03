using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using VdsServiceUpdater.Api;
using VdsServiceUpdater.Options;

namespace VdsServiceUpdater.Security;

/// <summary>Проверка X-Webhook-Token для /api/*. Если токен не задан в конфиге, пропускает всё.</summary>
public sealed class TokenAuthMiddleware
{
    public const string HeaderName = "X-Webhook-Token";

    private readonly RequestDelegate _next;
    private readonly ILogger<TokenAuthMiddleware> _logger;
    private readonly byte[]? _expectedHash;

    public TokenAuthMiddleware(RequestDelegate next, IOptions<UpdaterOptions> options, ILogger<TokenAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        var token = options.Value.Auth.Token;
        _expectedHash = string.IsNullOrEmpty(token) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_expectedHash is null || !context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        // Сравниваем хеши: фиксированная длина, постоянное время, длина токена не утекает.
        var provided = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers[HeaderName].ToString()));
        if (CryptographicOperations.FixedTimeEquals(provided, _expectedHash))
        {
            await _next(context);
            return;
        }

        _logger.LogWarning("Запрос отклонён: неверный или отсутствующий токен (IP {RemoteIp})", context.Connection.RemoteIpAddress);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(ApiError.Of("unauthorized", "Неверный или отсутствующий X-Webhook-Token."));
    }
}
