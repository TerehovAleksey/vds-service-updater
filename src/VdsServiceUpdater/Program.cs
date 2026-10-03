using System.Net;
using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Context;
using VdsServiceUpdater.Api;
using VdsServiceUpdater.Images;
using VdsServiceUpdater.Options;
using VdsServiceUpdater.Security;

// Не CreateBootstrapLogger: «замороженный» логгер ломает повторный запуск хоста (WebApplicationFactory в тестах).
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Дополнительный файл конфигурации (монтируется в контейнер), env-переменные: Updater__Auth__Token и т.д.
    // Приоритет (по возрастанию): appsettings.json < /config/updater.json < переменные окружения.
    builder.Configuration.AddJsonFile("/config/updater.json", optional: true, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();

    builder.Host.UseSerilog((ctx, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext());

    builder.Services.AddSingleton<IValidateOptions<UpdaterOptions>, UpdaterOptionsValidator>();
    builder.Services.AddOptions<UpdaterOptions>()
        .Bind(builder.Configuration.GetSection(UpdaterOptions.SectionName))
        .ValidateOnStart(); // неверный конфиг = приложение не стартует
    builder.Services.AddSingleton<ImageRequestValidator>();
    builder.Services.AddSingleton<VdsServiceUpdater.Compose.ComposeFileStore>();
    builder.Services.AddSingleton<VdsServiceUpdater.Docker.IProcessRunner, VdsServiceUpdater.Docker.ProcessRunner>();
    builder.Services.AddSingleton<VdsServiceUpdater.Docker.DockerCli>();
    builder.Services.AddSingleton<VdsServiceUpdater.Docker.ServiceDeployer>();
    builder.Services.AddSingleton<VdsServiceUpdater.Deploy.TargetLocks>();
    builder.Services.AddSingleton<VdsServiceUpdater.Deploy.DeployOrchestrator>();

    // Лимит тела для потоковых запросов без Content-Length (заявленный размер проверяет middleware ниже).
    builder.WebHost.ConfigureKestrel((ctx, k) =>
    {
        var sec = ctx.Configuration.GetSection($"{UpdaterOptions.SectionName}:Security").Get<SecurityOptions>() ?? new SecurityOptions();
        k.AddServerHeader = false;
        k.Limits.MaxRequestBodySize = sec.MaxBodyBytes;
    });

    // Настройки middleware берутся из IOptions лениво (так их можно переопределять в интеграционных тестах).
    builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<UpdaterOptions>>((o, up) =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = 1;
        foreach (var p in up.Value.Proxy.KnownProxies) o.KnownProxies.Add(IPAddress.Parse(p));
        foreach (var n in up.Value.Proxy.KnownNetworks) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(n));
    });

    builder.Services.AddRateLimiter(_ => { });
    builder.Services.AddOptions<RateLimiterOptions>().Configure<IOptions<UpdaterOptions>>((o, up) =>
    {
        var perMinute = up.Value.Security.RequestsPerMinute;
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/api"))
                return RateLimitPartition.GetNoLimiter("non-api");
            return RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        });
        o.OnRejected = async (c, ct) =>
        {
            if (c.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                c.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
            await c.HttpContext.Response.WriteAsJsonAsync(
                ApiError.Of("rate_limited", "Слишком много запросов, повторите позже."), ct);
        };
    });

    var app = builder.Build();
    var opts = app.Services.GetRequiredService<IOptions<UpdaterOptions>>().Value; // триггерит валидацию конфигурации

    app.UseForwardedHeaders();
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["X-Request-Id"] = ctx.TraceIdentifier;
        using (LogContext.PushProperty("RequestId", ctx.TraceIdentifier))
            await next();
    });
    app.UseSerilogRequestLogging();
    app.UseRateLimiter();                       // до проверки токена: ограничивает и подбор токена
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.ContentLength > opts.Security.MaxBodyBytes)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await ctx.Response.WriteAsJsonAsync(ApiError.Of("payload_too_large", "Тело запроса слишком большое."));
            return;
        }
        await next();
    });
    app.UseMiddleware<TokenAuthMiddleware>();

    if (string.IsNullOrEmpty(opts.Auth.Token))
        app.Logger.LogWarning("Updater:Auth:Token не задан: эндпоинт деплоя НЕ защищён. Не выставляйте сервис наружу без токена.");
    app.Logger.LogInformation("Целей: {Targets}, разрешённые префиксы образов: {Prefixes}",
        opts.Targets.Select(t => $"{t.Name}({t.Type})"), opts.Deploy.AllowedImagePrefixes);

    var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    app.MapGet("/health", () => Results.Ok(new { status = "ok", version }));

    // Деплой работает на токене остановки приложения, а не запроса: обрыв клиента (например, таймаут nginx)
    // не прерывает деплой и откат.
    app.MapPost("/api/v1/deploy", async (DeployRequest req, ImageRequestValidator validator,
        VdsServiceUpdater.Deploy.DeployOrchestrator orchestrator, IHostApplicationLifetime lifetime, ILogger<Program> log) =>
    {
        var check = validator.Validate(req.Image);
        if (check.Image is null)
        {
            log.LogWarning("Образ отклонён: {Code} ({Image})", check.Code, LogSanitizer.Sanitize(req.Image));
            return Results.Json(ApiError.Of(check.Code!, check.Message!), statusCode: check.StatusCode);
        }

        log.LogInformation("Запрос на деплой принят: {Image}, dryRun={DryRun}", check.Image.Full, req.DryRun);
        var outcome = await orchestrator.DeployAsync(check.Image, req.DryRun, lifetime.ApplicationStopping);
        log.LogInformation("Деплой {Image} завершён: HTTP {Status}, {Result} ({Code}), {Duration} мс",
            check.Image.Full, outcome.StatusCode, outcome.Body.Status, outcome.Body.Code, outcome.Body.DurationMs);
        return Results.Json(outcome.Body, statusCode: outcome.StatusCode);
    });

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Приложение не удалось запустить");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
return 0;

public partial class Program;
