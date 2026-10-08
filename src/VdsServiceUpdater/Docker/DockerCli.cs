using VdsServiceUpdater.Options;

namespace VdsServiceUpdater.Docker;

/// <summary>Тонкая обёртка над `docker` CLI. Все аргументы передаются списком, без shell.</summary>
public sealed class DockerCli(IProcessRunner runner)
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);

    private Task<ProcessResult> Run(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, string? stdin = null) =>
        runner.RunAsync(new ProcessSpec("docker", args, timeout, StdIn: stdin), ct);

    private static List<string> Compose(TargetOptions t, IEnumerable<string> tail)
    {
        var args = new List<string> { "compose", "-f", t.File };
        if (!string.IsNullOrWhiteSpace(t.StackName)) { args.Add("-p"); args.Add(t.StackName); }
        args.AddRange(tail);
        return args;
    }

    // ---- compose ----

    /// <summary>Проверка файла: `docker compose config -q` (вывод не нужен, секреты не печатаются).</summary>
    public Task<ProcessResult> ComposeConfigCheckAsync(TargetOptions t, CancellationToken ct) =>
        Run(Compose(t, ["config", "-q"]), QueryTimeout, ct);

    /// <summary>Рендер конфига с подстановкой переменных; stdout содержит секреты, наружу не отдавать.</summary>
    public Task<ProcessResult> ComposeConfigRenderAsync(TargetOptions t, CancellationToken ct) =>
        Run(Compose(t, ["config"]), QueryTimeout, ct);

    public Task<ProcessResult> ComposePullAsync(TargetOptions t, IEnumerable<string> services, TimeSpan timeout, CancellationToken ct) =>
        Run(Compose(t, ["pull", "--quiet", .. services]), timeout, ct);

    public Task<ProcessResult> ComposeUpAsync(TargetOptions t, IEnumerable<string> services, TimeSpan timeout, CancellationToken ct) =>
        Run(Compose(t, ["up", "-d", "--no-deps", "--no-build", .. services]), timeout, ct);

    /// <summary>Удаляет только указанную ссылку и не форсирует удаление.</summary>
    public Task<ProcessResult> ImageRemoveAsync(string image, CancellationToken ct) =>
        Run(["image", "rm", "--", image], QueryTimeout, ct);

    public async Task<IReadOnlyList<ContainerState>> GetComposeContainersAsync(TargetOptions t, string service, CancellationToken ct)
    {
        var ps = await Run(Compose(t, ["ps", "-a", "-q", service]), QueryTimeout, ct);
        if (!ps.Success) throw new DockerCommandException($"docker compose ps завершился с кодом {ps.ExitCode}: {OutputSanitizer.Tail(ps.StdErr.Trim(), 300)}");

        var ids = ps.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0) return [];

        var inspect = await Run(["inspect", .. ids], QueryTimeout, ct);
        if (!inspect.Success) throw new DockerCommandException($"docker inspect завершился с кодом {inspect.ExitCode}: {OutputSanitizer.Tail(inspect.StdErr.Trim(), 300)}");
        return DockerJson.ParseContainers(inspect.StdOut);
    }

    // ---- swarm ----

    /// <summary>null, если сервиса нет в swarm.</summary>
    public async Task<ServiceInfo?> GetServiceAsync(string name, CancellationToken ct)
    {
        var r = await Run(["service", "inspect", "--format", "{{json .}}", name], QueryTimeout, ct);
        if (r.Success) return DockerJson.ParseService(r.StdOut);
        if (r.StdErr.Contains("no such service", StringComparison.OrdinalIgnoreCase)) return null;
        throw new DockerCommandException($"docker service inspect завершился с кодом {r.ExitCode}: {OutputSanitizer.Tail(r.StdErr.Trim(), 300)}");
    }

    /// <summary>Откат при сбое делает сам swarm (--update-failure-action rollback); --detach: ждём сами через inspect.</summary>
    public Task<ProcessResult> ServiceUpdateImageAsync(string name, string image, TimeSpan timeout, CancellationToken ct) =>
        Run(["service", "update", "--image", image, "--with-registry-auth",
             "--update-failure-action", "rollback", "--detach", name], timeout, ct);

    /// <summary>Экспериментально: применяет весь файл, текст конфига передаётся через stdin.</summary>
    public Task<ProcessResult> StackDeployAsync(string stackName, string composeYaml, TimeSpan timeout, CancellationToken ct) =>
        Run(["stack", "deploy", "--compose-file", "-", "--with-registry-auth", stackName], timeout, ct, stdin: composeYaml);
}
