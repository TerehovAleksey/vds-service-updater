using System.Diagnostics;
using Microsoft.Extensions.Options;
using VdsServiceUpdater.Options;

namespace VdsServiceUpdater.Docker;

public sealed record ServiceApplyResult(string Service, bool Success, string Message);

/// <summary>
/// Применяет уже изменённый YAML: compose (pull + up --no-deps) или swarm (service update / stack deploy)
/// и ждёт готовности. Откат (восстановить файл и вызвать ApplyAsync со старыми образами) делает оркестратор.
/// </summary>
public sealed class ServiceDeployer(DockerCli docker, IOptions<UpdaterOptions> options, ILogger<ServiceDeployer> logger)
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<(bool Ok, string Message)> ValidateFileAsync(TargetOptions target, CancellationToken ct)
    {
        var r = await docker.ComposeConfigCheckAsync(target, ct);
        return r.Success ? (true, "ok") : (false, Describe("File failed `docker compose config` validation", r));
    }

    public async Task<(bool Success, string? Error)> RemoveImageAsync(string image, CancellationToken ct)
    {
        var result = await docker.ImageRemoveAsync(image, ct);
        return result.Success
            ? (true, null)
            : (false, Describe("docker image rm failed", result));
    }

    /// <param name="images">сервис → полное значение image (как записано в файле), которое должно работать.</param>
    public async Task<IReadOnlyList<ServiceApplyResult>> ApplyAsync(
        TargetOptions target, IReadOnlyDictionary<string, string> images, TimeSpan timeout, CancellationToken ct)
    {
        if (images.Count == 0) return [];

        var clock = Stopwatch.StartNew();
        TimeSpan Remaining() => timeout - clock.Elapsed;

        logger.LogInformation("Applying {Type} deployment to {Target}: {Services}", target.Type, target.Name, images);
        var results = target.Type == TargetType.Compose
            ? await ApplyComposeAsync(target, images, Remaining, ct)
            : target.ApplyMode == StackApplyMode.StackDeploy
                ? await ApplyStackDeployAsync(target, images, Remaining, ct)
                : await ApplySwarmServicesAsync(target, images, Remaining, ct);

        foreach (var r in results)
            logger.LogInformation("Deployment result for {Target}/{Service}: {Success} ({Message})", target.Name, r.Service, r.Success, r.Message);
        return results;
    }

    // ---------------- compose ----------------

    private async Task<IReadOnlyList<ServiceApplyResult>> ApplyComposeAsync(
        TargetOptions t, IReadOnlyDictionary<string, string> images, Func<TimeSpan> remaining, CancellationToken ct)
    {
        var services = images.Keys.ToArray();

        if (remaining() <= TimeSpan.Zero) return FailAll(services, "The deployment time limit has expired.");
        var pull = await docker.ComposePullAsync(t, services, remaining(), ct);
        if (!pull.Success) return FailAll(services, Describe("docker compose pull failed", pull));

        if (remaining() <= TimeSpan.Zero) return FailAll(services, "The deployment time limit has expired.");
        var up = await docker.ComposeUpAsync(t, services, remaining(), ct);
        if (!up.Success) return FailAll(services, Describe("docker compose up failed", up));

        var stability = TimeSpan.FromSeconds(options.Value.Deploy.StabilitySeconds);
        var tasks = services.Select(async service =>
        {
            try
            {
                var w = await ReadinessWaiter.WaitForContainersAsync(
                    c => docker.GetComposeContainersAsync(t, service, c), images[service],
                    remaining(), stability, PollInterval, ct);
                return new ServiceApplyResult(service, w.Success, w.Message);
            }
            catch (DockerCommandException ex) { return new ServiceApplyResult(service, false, ex.Message); }
        });
        return await Task.WhenAll(tasks);
    }

    // ---------------- swarm ----------------

    private async Task<IReadOnlyList<ServiceApplyResult>> ApplySwarmServicesAsync(
        TargetOptions t, IReadOnlyDictionary<string, string> images, Func<TimeSpan> remaining, CancellationToken ct)
    {
        var tasks = images.Select(async kv =>
        {
            var (service, image) = (kv.Key, kv.Value);
            var name = $"{t.StackName}_{service}";
            try
            {
                var before = await docker.GetServiceAsync(name, ct);
                if (before is null) return Missing(service, name);
                if (AlreadyDeployed(before, image)) return new ServiceApplyResult(service, true, "Service is already running with this image.");

                var upd = await docker.ServiceUpdateImageAsync(name, image, remaining(), ct);
                if (!upd.Success) return new ServiceApplyResult(service, false, Describe("docker service update failed", upd));

                return await WaitSwarmAsync(service, name, before, remaining, ct);
            }
            catch (DockerCommandException ex) { return new ServiceApplyResult(service, false, ex.Message); }
        });
        return await Task.WhenAll(tasks);
    }

    /// <summary>Экспериментальный режим: `docker compose config | docker stack deploy -c -`.</summary>
    private async Task<IReadOnlyList<ServiceApplyResult>> ApplyStackDeployAsync(
        TargetOptions t, IReadOnlyDictionary<string, string> images, Func<TimeSpan> remaining, CancellationToken ct)
    {
        var services = images.Keys.ToArray();
        var before = new Dictionary<string, ServiceInfo>();
        try
        {
            foreach (var s in services)
            {
                var info = await docker.GetServiceAsync($"{t.StackName}_{s}", ct);
                if (info is null) return FailAll(services, Missing(s, $"{t.StackName}_{s}").Message);
                before[s] = info;
            }
        }
        catch (DockerCommandException ex) { return FailAll(services, ex.Message); }

        var render = await docker.ComposeConfigRenderAsync(t, ct);
        if (!render.Success) return FailAll(services, Describe("docker compose config failed", render));

        var deploy = await docker.StackDeployAsync(t.StackName!, render.StdOut, remaining(), ct);
        if (!deploy.Success) return FailAll(services, Describe("docker stack deploy failed", deploy));

        var tasks = services.Select(async s =>
        {
            try
            {
                if (AlreadyDeployed(before[s], images[s])) return new ServiceApplyResult(s, true, "Service is already running with this image.");
                return await WaitSwarmAsync(s, $"{t.StackName}_{s}", before[s], remaining, ct);
            }
            catch (DockerCommandException ex) { return new ServiceApplyResult(s, false, ex.Message); }
        });
        return await Task.WhenAll(tasks);
    }

    private async Task<ServiceApplyResult> WaitSwarmAsync(string service, string name, ServiceInfo before, Func<TimeSpan> remaining, CancellationToken ct)
    {
        var w = await ReadinessWaiter.WaitForSwarmUpdateAsync(
            c => docker.GetServiceAsync(name, c), before.UpdateStartedAt, remaining(), PollInterval, ct);
        return new ServiceApplyResult(service, w.Success, w.Message);
    }

    // ---------------- helpers ----------------

    /// <summary>Swarm хранит образ как "name:tag@sha256:..."; сравниваем без digest.</summary>
    private static bool AlreadyDeployed(ServiceInfo info, string image) =>
        (info.Image ?? "").Split('@')[0] == image && info.UpdateState is not ("updating" or "rollback_started");

    private static ServiceApplyResult Missing(string service, string name) =>
        new(service, false, $"Service '{name}' was not found in Swarm (is the stack deployed?).");

    private static IReadOnlyList<ServiceApplyResult> FailAll(IEnumerable<string> services, string message) =>
        services.Select(s => new ServiceApplyResult(s, false, message)).ToArray();

    private string Describe(string what, ProcessResult r)
    {
        var reason = r.TimedOut ? "timed out" : $"exit code {r.ExitCode}";
        var output = r.StdErr.Trim().Length > 0 ? r.StdErr : r.StdOut;
        return $"{what} ({reason}): {OutputSanitizer.Prepare(output, [options.Value.Auth.Token])}";
    }
}
