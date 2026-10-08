using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using VdsServiceUpdater.Docker;

namespace VdsServiceUpdater.Tests;

/// <summary>Весь HTTP-конвейер (middleware, эндпоинт, оркестратор) с подменённым docker.</summary>
internal sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Token = "test-token-1234567890";

    private readonly Dictionary<string, string?> _settings = new();
    public string Dir { get; } = Directory.CreateTempSubdirectory("vdsu-api-").FullName;
    public string ComposeFile => Path.Combine(Dir, "compose.yml");
    public string Yaml => File.ReadAllText(ComposeFile);
    public FakeCompose Docker { get; } = new();

    public ApiFactory(string token = Token, int rateLimit = 1000, int maxBody = 4096, Action<Dictionary<string, string?>>? configure = null)
    {
        File.WriteAllText(ComposeFile, Harness.DefaultYaml);
        _settings["Updater:Auth:Token"] = token;
        _settings["Updater:Security:RequestsPerMinute"] = rateLimit.ToString();
        _settings["Updater:Security:MaxBodyBytes"] = maxBody.ToString();
        _settings["Updater:Deploy:AllowedImagePrefixes:0"] = "ghcr.io/org/";
        _settings["Updater:Deploy:WaitTimeoutSeconds"] = "10";
        _settings["Updater:Deploy:StabilitySeconds"] = "0";
        _settings["Updater:Deploy:BackupDirectory"] = Path.Combine(Dir, "backups");
        _settings["Updater:Targets:0:Name"] = "main";
        _settings["Updater:Targets:0:Type"] = "Compose";
        _settings["Updater:Targets:0:File"] = ComposeFile;
        _settings["Updater:Targets:0:StackName"] = "main";
        _settings["Updater:Targets:0:Protected:0"] = "*-db";
        configure?.Invoke(_settings);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing"); // не подтягиваем appsettings.Development.json
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(_settings));
        builder.ConfigureTestServices(s => s.AddSingleton<IProcessRunner>(Docker.Runner));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
    }
}

public class ApiIntegrationTests
{
    private static string Body(string image) => JsonSerializer.Serialize(new { image });

    private static HttpRequestMessage Post(string json, string? token = ApiFactory.Token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/deploy")
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (token is not null) req.Headers.Add("X-Webhook-Token", token);
        return req;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        await r.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Условие не выполнилось за отведённое время.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    // ---------- здоровье, токен ----------

    [Fact]
    public async Task Health_is_open_and_returns_request_id()
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("ok", (await Json(r)).GetProperty("status").GetString());
        Assert.True(r.Headers.Contains("X-Request-Id"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task Missing_or_wrong_token_is_401_and_docker_untouched(string? token)
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().SendAsync(Post(Body("ghcr.io/org/app:2.0"), token), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("unauthorized", (await Json(r)).GetProperty("code").GetString());
        Assert.Empty(f.Docker.Runner.Calls);
        Assert.Equal(Harness.DefaultYaml, f.Yaml);
    }

    [Fact]
    public async Task Empty_token_in_config_disables_protection()
    {
        using var f = new ApiFactory(token: "");
        var r = await f.CreateClient().SendAsync(Post(Body("ghcr.io/org/app:2.0"), token: null), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    // ---------- деплой ----------

    [Fact]
    public async Task Successful_deploy_over_http()
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().SendAsync(Post(Body("ghcr.io/org/app:2.0")), TestContext.Current.CancellationToken);
        var json = await Json(r);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("updated", json.GetProperty("status").GetString());
        var item = json.GetProperty("results")[0];
        Assert.Equal("app", item.GetProperty("service").GetString());
        Assert.Equal("ghcr.io/org/app:1.0", item.GetProperty("previousImage").GetString());
        Assert.Equal("ghcr.io/org/app:2.0", item.GetProperty("newImage").GetString());
        Assert.Contains("image: ghcr.io/org/app:2.0", f.Yaml);
    }

    [Fact]
    public async Task Cleanup_attempts_are_included_in_successful_api_response_when_enabled()
    {
        using var f = new ApiFactory(configure: settings => settings["Updater:Targets:0:CleanupOldImagesAfterSuccess"] = "true");

        var r = await f.CreateClient().SendAsync(Post(Body("ghcr.io/org/app:2.0")), TestContext.Current.CancellationToken);
        var json = await Json(r);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cleanup = json.GetProperty("cleanup")[0];
        Assert.Equal("main", cleanup.GetProperty("target").GetString());
        Assert.Equal("ghcr.io/org/app:1.0", cleanup.GetProperty("image").GetString());
        Assert.True(cleanup.GetProperty("attempted").GetBoolean());
        Assert.True(cleanup.GetProperty("success").GetBoolean());
    }

    [Theory]
    [InlineData("ghcr.io/other/app:1", 403, "image_not_allowed")]
    [InlineData("ghcr.io/org/app:latest", 422, "latest_tag_forbidden")]
    [InlineData("ghcr.io/org/app", 422, "tag_required")]
    [InlineData("ghcr.io/org/app:1;ls", 422, "invalid_image")]
    [InlineData("", 422, "invalid_image")]
    public async Task Invalid_images_are_rejected_before_touching_anything(string image, int status, string code)
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().SendAsync(Post(Body(image)), TestContext.Current.CancellationToken);

        Assert.Equal(status, (int)r.StatusCode);
        Assert.Equal(code, (await Json(r)).GetProperty("code").GetString());
        Assert.Empty(f.Docker.Runner.Calls);
        Assert.Equal(Harness.DefaultYaml, f.Yaml);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("")]
    public async Task Malformed_body_is_400(string json)
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().SendAsync(Post(json), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Oversized_body_is_413()
    {
        using var f = new ApiFactory(maxBody: 256);
        var r = await f.CreateClient().SendAsync(Post(Body("ghcr.io/org/app:" + new string('1', 1000))), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Empty(f.Docker.Runner.Calls);
    }

    [Fact]
    public async Task Dry_run_over_http_changes_nothing()
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().SendAsync(Post("""{"image":"ghcr.io/org/app:2.0","dryRun":true}"""), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("dry_run", (await Json(r)).GetProperty("status").GetString());
        Assert.Equal(Harness.DefaultYaml, f.Yaml);
        Assert.Empty(f.Docker.Runner.Calls);
    }

    // ---------- лимиты ----------

    [Fact]
    public async Task Rate_limit_returns_429_but_does_not_touch_health()
    {
        using var f = new ApiFactory(rateLimit: 3);
        var client = f.CreateClient();

        for (var i = 0; i < 3; i++)
            Assert.Equal(422, (int)(await client.SendAsync(Post(Body("ghcr.io/org/app")), TestContext.Current.CancellationToken)).StatusCode);

        var limited = await client.SendAsync(Post(Body("ghcr.io/org/app")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Token_guessing_is_rate_limited_too()
    {
        using var f = new ApiFactory(rateLimit: 3);
        var client = f.CreateClient();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post(Body("ghcr.io/org/app:2"), "guess-" + i), TestContext.Current.CancellationToken)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Post(Body("ghcr.io/org/app:2"), "guess-3"), TestContext.Current.CancellationToken)).StatusCode);
    }

    // ---------- конкурентность и обрыв клиента ----------

    [Fact]
    public async Task Client_disconnect_does_not_abort_the_deploy()
    {
        using var f = new ApiFactory();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Docker.OnUp = () => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var call = f.CreateClient().SendAsync(Post(Body("ghcr.io/org/app:2.0")), cts.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "деплой не дошёл до docker compose up");

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        release.Set();

        // клиента уже нет, но деплой должен дойти до проверки готовности
        await WaitUntil(() => { lock (f.Docker.Runner.Calls) return f.Docker.Runner.Calls.Any(c => c.StartsWith("inspect")); });
        Assert.Contains("image: ghcr.io/org/app:2.0", f.Yaml);
        Assert.Equal("ghcr.io/org/app:2.0", f.Docker.Running);
    }

    [Fact]
    public async Task Parallel_deploy_to_same_file_gets_409_then_first_succeeds()
    {
        using var f = new ApiFactory();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Docker.OnUp = () => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var client = f.CreateClient();

        var first = client.SendAsync(Post(Body("ghcr.io/org/app:2.0")), TestContext.Current.CancellationToken);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        var second = await client.SendAsync(Post(Body("ghcr.io/org/app:3.0")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("deploy_in_progress", (await Json(second)).GetProperty("code").GetString());

        release.Set();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Contains("app:2.0", f.Yaml);
    }

    // ---------- конфигурация ----------

    [Fact]
    public void Invalid_configuration_prevents_startup()
    {
        using var f = new ApiFactory(configure: s => s["Updater:Deploy:AllowedImagePrefixes:0"] = "");
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }
}
