using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using VdsServiceUpdater.Compose;
using VdsServiceUpdater.Deploy;
using VdsServiceUpdater.Docker;
using VdsServiceUpdater.Images;
using VdsServiceUpdater.Options;

namespace VdsServiceUpdater.Tests;

/// <summary>Имитация docker compose: `up` читает image из файла на диске, как настоящий compose.</summary>
internal sealed class FakeCompose
{
    private static readonly Regex ImageRx = new(@"image:\s*(\S+)");

    public string Running = "ghcr.io/org/app:1.0";
    public Func<string, string> StatusFor = _ => "running";
    public Func<string, string?> HealthFor = _ => null;
    public bool InvalidConfig;
    public Action? OnUp;
    public FakeRunner Runner { get; }

    public FakeCompose() => Runner = new FakeRunner(Handle);

    private ProcessResult Handle(IReadOnlyList<string> a)
    {
        if (a[0] == "inspect")
        {
            var status = StatusFor(Running);
            return FakeRunner.Ok(DockerTestData.Inspect(status, HealthFor(Running), 0, status == "exited" ? 1 : 0, Running));
        }
        if (a[0] != "compose") return FakeRunner.Ok();
        if (a.Contains("config")) return InvalidConfig ? FakeRunner.Err("services.app Additional property is not allowed") : FakeRunner.Ok();
        if (a.Contains("pull")) return FakeRunner.Ok();
        if (a.Contains("up"))
        {
            OnUp?.Invoke();
            Running = ImageRx.Match(File.ReadAllText(a[2])).Groups[1].Value.Trim('"');
            return FakeRunner.Ok();
        }
        if (a.Contains("ps")) return FakeRunner.Ok("abc123def456\n");
        return FakeRunner.Ok();
    }
}

internal sealed class Harness : IDisposable
{
    public const string DefaultYaml = "services:\n  app:\n    image: ghcr.io/org/app:1.0\n  db:\n    image: postgres:16\n";

    public string Dir { get; } = Directory.CreateTempSubdirectory("vdsu-orch-").FullName;
    public string ComposeFile => Path.Combine(Dir, "compose.yml");
    public string BackupDir => Path.Combine(Dir, "backups");
    public UpdaterOptions Options { get; } = new();
    public FakeCompose Docker { get; } = new();
    public TargetLocks Locks { get; } = new();
    public DeployOrchestrator Orchestrator { get; }

    public Harness(string yaml = DefaultYaml, bool rollback = true, string[]? protectedPatterns = null, bool withMissingTarget = false, int timeoutSeconds = 5)
    {
        File.WriteAllText(ComposeFile, yaml);
        Options.Deploy.WaitTimeoutSeconds = timeoutSeconds;
        Options.Deploy.StabilitySeconds = 0;
        Options.Deploy.RollbackOnFailure = rollback;
        Options.Deploy.BackupDirectory = BackupDir;
        Options.Targets.Add(new TargetOptions
        {
            Name = "main", Type = TargetType.Compose, File = ComposeFile, StackName = "main",
            Protected = (protectedPatterns ?? []).ToList()
        });
        if (withMissingTarget)
            Options.Targets.Add(new TargetOptions { Name = "gone", Type = TargetType.Compose, File = Path.Combine(Dir, "missing.yml") });

        var opts = Microsoft.Extensions.Options.Options.Create(Options);
        var deployer = new ServiceDeployer(new DockerCli(Docker.Runner), opts, NullLogger<ServiceDeployer>.Instance)
        { PollInterval = TimeSpan.FromMilliseconds(5) };
        Orchestrator = new DeployOrchestrator(new ComposeFileStore(opts), deployer, Locks, opts, NullLogger<DeployOrchestrator>.Instance);
    }

    public Task<DeployOutcome> Deploy(string image, bool dryRun = false)
    {
        Assert.True(ImageReference.TryParse(image, out var r, out _));
        return Orchestrator.DeployAsync(r!, dryRun, CancellationToken.None);
    }

    public string Yaml => File.ReadAllText(ComposeFile);
    public void Dispose() => Directory.Delete(Dir, recursive: true);
}

public class DeployOrchestratorTests
{
    [Fact]
    public async Task Successful_deploy_updates_file_applies_and_keeps_backup()
    {
        using var h = new Harness();
        var o = await h.Deploy("ghcr.io/org/app:2.0");

        Assert.Equal(200, o.StatusCode);
        Assert.Equal("updated", o.Body.Status);
        var r = Assert.Single(o.Body.Results);
        Assert.Equal(("app", "updated", "ghcr.io/org/app:1.0", "ghcr.io/org/app:2.0"), (r.Service, r.Status, r.PreviousImage, r.NewImage));
        Assert.Contains("image: ghcr.io/org/app:2.0", h.Yaml);
        Assert.Contains("image: postgres:16", h.Yaml);
        Assert.Single(Directory.GetFiles(Path.Combine(h.BackupDir, "main")));
        Assert.Equal("ghcr.io/org/app:2.0", h.Docker.Running);
    }

    [Fact]
    public async Task Same_tag_is_unchanged_and_docker_is_not_touched()
    {
        using var h = new Harness();
        var o = await h.Deploy("ghcr.io/org/app:1.0");

        Assert.Equal((200, "unchanged"), (o.StatusCode, o.Body.Status));
        Assert.Empty(h.Docker.Runner.Calls);
        Assert.Equal(Harness.DefaultYaml, h.Yaml);
    }

    [Fact]
    public async Task Unknown_image_is_404()
    {
        using var h = new Harness();
        var o = await h.Deploy("ghcr.io/org/other:1");
        Assert.Equal((404, "service_not_found"), (o.StatusCode, o.Body.Code));
    }

    [Fact]
    public async Task Only_protected_match_is_403_and_nothing_changes()
    {
        using var h = new Harness("services:\n  app-db:\n    image: ghcr.io/org/app:1.0\n", protectedPatterns: ["*-db"]);
        var o = await h.Deploy("ghcr.io/org/app:2.0");

        Assert.Equal((403, "service_protected"), (o.StatusCode, o.Body.Code));
        Assert.Empty(h.Docker.Runner.Calls);
        Assert.Contains("app:1.0", h.Yaml);
    }

    [Fact]
    public async Task Variable_tag_is_422()
    {
        using var h = new Harness("services:\n  app:\n    image: ghcr.io/org/app:${TAG}\n");
        var o = await h.Deploy("ghcr.io/org/app:2.0");
        Assert.Equal((422, "image_uses_variable"), (o.StatusCode, o.Body.Code));
    }

    [Fact]
    public async Task Unsupported_layout_is_422_and_file_untouched()
    {
        const string yaml = "services:\n  app: {image: \"ghcr.io/org/app:1.0\"}\n";
        using var h = new Harness(yaml);
        var o = await h.Deploy("ghcr.io/org/app:2.0");

        Assert.Equal((422, "unsupported_format"), (o.StatusCode, o.Body.Code));
        Assert.Equal(yaml, h.Yaml);
    }

    [Fact]
    public async Task Mixed_match_updates_allowed_and_skips_protected()
    {
        const string yaml = "services:\n  app:\n    image: ghcr.io/org/app:1.0\n  app-worker:\n    image: ghcr.io/org/app:1.0\n";
        using var h = new Harness(yaml, protectedPatterns: ["app-worker"]);
        var o = await h.Deploy("ghcr.io/org/app:2.0");

        Assert.Equal((200, "updated"), (o.StatusCode, o.Body.Status));
        Assert.Equal(["updated", "skipped"], o.Body.Results.Select(r => r.Status).ToArray());
        Assert.Equal("protected", o.Body.Results[1].Code);
        Assert.Contains("app-worker:\n    image: ghcr.io/org/app:1.0", h.Yaml);
    }

    [Fact]
    public async Task Dry_run_changes_nothing()
    {
        using var h = new Harness();
        var o = await h.Deploy("ghcr.io/org/app:2.0", dryRun: true);

        Assert.Equal((200, "dry_run"), (o.StatusCode, o.Body.Status));
        Assert.Equal("planned", Assert.Single(o.Body.Results).Status);
        Assert.Empty(h.Docker.Runner.Calls);
        Assert.Equal(Harness.DefaultYaml, h.Yaml);
        Assert.False(Directory.Exists(h.BackupDir));
    }

    [Fact]
    public async Task Busy_target_is_409()
    {
        using var h = new Harness();
        using var held = h.Locks.TryAcquire(h.ComposeFile);
        Assert.NotNull(held);

        var o = await h.Deploy("ghcr.io/org/app:2.0");
        Assert.Equal((409, "deploy_in_progress"), (o.StatusCode, o.Body.Code));
        Assert.Equal(Harness.DefaultYaml, h.Yaml);
    }

    [Fact]
    public async Task Lock_is_released_after_deploy()
    {
        using var h = new Harness();
        await h.Deploy("ghcr.io/org/app:2.0");
        using var again = h.Locks.TryAcquire(h.ComposeFile);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Unreadable_unrelated_target_only_gives_warning()
    {
        using var h = new Harness(withMissingTarget: true);
        var o = await h.Deploy("ghcr.io/org/app:2.0");

        Assert.Equal((200, "updated"), (o.StatusCode, o.Body.Status));
        Assert.Contains("gone", Assert.Single(o.Body.Warnings!));
    }

    // ---------- failures and rollback ----------

    [Fact]
    public async Task Failed_start_rolls_back_file_and_container()
    {
        using var h = new Harness();
        h.Docker.StatusFor = img => img.EndsWith(":2.0") ? "exited" : "running";
        var o = await h.Deploy("ghcr.io/org/app:2.0");

        Assert.Equal((500, "deploy_failed"), (o.StatusCode, o.Body.Code));
        Assert.Equal("rolled_back", Assert.Single(o.Body.Results).Status);
        Assert.Equal(Harness.DefaultYaml, h.Yaml);
        Assert.Equal("ghcr.io/org/app:1.0", h.Docker.Running);
    }

    [Fact]
    public async Task Failed_start_without_rollback_keeps_new_file()
    {
        using var h = new Harness(rollback: false);
        h.Docker.StatusFor = img => img.EndsWith(":2.0") ? "exited" : "running";
        var o = await h.Deploy("ghcr.io/org/app:2.0");

        Assert.Equal(500, o.StatusCode);
        Assert.Equal("failed", Assert.Single(o.Body.Results).Status);
        Assert.Contains("app:2.0", h.Yaml);
    }

    [Fact]
    public async Task Failed_rollback_is_reported_and_file_is_still_restored()
    {
        using var h = new Harness();
        h.Docker.StatusFor = _ => "exited";
        var o = await h.Deploy("ghcr.io/org/app:2.0");

        Assert.Equal((500, "rollback_failed"), (o.StatusCode, o.Body.Code));
        Assert.Equal("rollback_failed", Assert.Single(o.Body.Results).Status);
        Assert.Equal(Harness.DefaultYaml, h.Yaml);
    }

    [Fact]
    public async Task Invalid_compose_config_restores_file_and_never_starts_containers()
    {
        using var h = new Harness();
        h.Docker.InvalidConfig = true;
        var o = await h.Deploy("ghcr.io/org/app:2.0");

        Assert.Equal(500, o.StatusCode);
        Assert.Equal("compose_config_invalid", Assert.Single(o.Body.Results).Code);
        Assert.Equal(Harness.DefaultYaml, h.Yaml);
        Assert.DoesNotContain(h.Docker.Runner.Calls, c => c.Contains(" up "));
    }

    [Fact]
    public async Task Readiness_timeout_is_504_and_rolled_back()
    {
        using var h = new Harness(timeoutSeconds: 1);
        h.Docker.HealthFor = img => img.EndsWith(":2.0") ? "starting" : "healthy";
        var o = await h.Deploy("ghcr.io/org/app:2.0");

        Assert.Equal((504, "timeout"), (o.StatusCode, o.Body.Code));
        Assert.Equal("rolled_back", Assert.Single(o.Body.Results).Status);
        Assert.Equal(Harness.DefaultYaml, h.Yaml);
    }

    [Fact]
    public async Task Cancellation_restores_file_and_releases_lock()
    {
        using var h = new Harness();
        h.Docker.OnUp = () => throw new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Deploy("ghcr.io/org/app:2.0"));

        Assert.Equal(Harness.DefaultYaml, h.Yaml);
        using var again = h.Locks.TryAcquire(h.ComposeFile);
        Assert.NotNull(again);
    }
}
