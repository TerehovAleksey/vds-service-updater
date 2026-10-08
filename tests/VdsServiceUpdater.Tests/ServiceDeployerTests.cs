using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using VdsServiceUpdater.Docker;
using VdsServiceUpdater.Options;
using static VdsServiceUpdater.Tests.DockerTestData;

namespace VdsServiceUpdater.Tests;

public class ServiceDeployerTests
{
    private const string File = "/opt/stacks/main/compose.yml";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static ServiceDeployer Create(FakeRunner runner)
    {
        var o = new UpdaterOptions();
        o.Deploy.StabilitySeconds = 0;
        return new ServiceDeployer(new DockerCli(runner), Microsoft.Extensions.Options.Options.Create(o), NullLogger<ServiceDeployer>.Instance)
        { PollInterval = TimeSpan.FromMilliseconds(5) };
    }

    private static TargetOptions Compose() => new() { Name = "main", Type = TargetType.Compose, File = File, StackName = "main" };
    private static TargetOptions Stack(StackApplyMode mode = StackApplyMode.ServiceUpdate) =>
        new() { Name = "prod", Type = TargetType.Stack, File = File, StackName = "prod", ApplyMode = mode };
    private static Dictionary<string, string> Img(string image = "ghcr.io/org/app:2") => new() { ["app"] = image };

    // ---------- compose ----------

    [Fact]
    public async Task Compose_success_runs_pull_then_up_then_waits()
    {
        var runner = new FakeRunner(a => string.Join(' ', a) switch
        {
            var s when s.Contains(" ps ") => FakeRunner.Ok("abc123def456\n"),
            var s when s.StartsWith("inspect") => FakeRunner.Ok(Inspect("running", "healthy", 0, 0, "ghcr.io/org/app:2")),
            _ => FakeRunner.Ok()
        });
        var results = await Create(runner).ApplyAsync(Compose(), Img(), Timeout, CancellationToken.None);

        Assert.True(Assert.Single(results).Success);
        Assert.Equal($"compose -f {File} -p main pull --quiet app", runner.Calls[0]);
        Assert.Equal($"compose -f {File} -p main up -d --no-deps --no-build app", runner.Calls[1]);
    }

    [Fact]
    public async Task Compose_pull_failure_stops_before_up_and_reports_stderr()
    {
        var runner = new FakeRunner(a => a.Contains("pull") ? FakeRunner.Err("denied: requested access to the resource is denied") : FakeRunner.Ok());
        var results = await Create(runner).ApplyAsync(Compose(), Img(), Timeout, CancellationToken.None);

        var r = Assert.Single(results);
        Assert.False(r.Success);
        Assert.Contains("denied", r.Message);
        Assert.DoesNotContain(runner.Calls, c => c.Contains(" up "));
    }

    [Fact]
    public async Task Compose_crashed_container_is_failure()
    {
        var runner = new FakeRunner(a => string.Join(' ', a) switch
        {
            var s when s.Contains(" ps ") => FakeRunner.Ok("abc123def456\n"),
            var s when s.StartsWith("inspect") => FakeRunner.Ok(Inspect("exited", null, 0, 1, "ghcr.io/org/app:2")),
            _ => FakeRunner.Ok()
        });
        var r = Assert.Single(await Create(runner).ApplyAsync(Compose(), Img(), Timeout, CancellationToken.None));
        Assert.False(r.Success);
        Assert.Contains("остановился", r.Message);
    }

    [Fact]
    public async Task Compose_never_ready_times_out()
    {
        var runner = new FakeRunner(a => string.Join(' ', a) switch
        {
            var s when s.Contains(" ps ") => FakeRunner.Ok("abc123def456\n"),
            var s when s.StartsWith("inspect") => FakeRunner.Ok(Inspect("running", "starting", 0, 0, "ghcr.io/org/app:2")),
            _ => FakeRunner.Ok()
        });
        var r = Assert.Single(await Create(runner).ApplyAsync(Compose(), Img(), TimeSpan.FromMilliseconds(200), CancellationToken.None));
        Assert.False(r.Success);
        Assert.Contains("Таймаут", r.Message);
    }

    [Fact]
    public async Task Validate_uses_quiet_config_check()
    {
        var runner = new FakeRunner(_ => FakeRunner.Ok());
        var (ok, _) = await Create(runner).ValidateFileAsync(Compose(), CancellationToken.None);
        Assert.True(ok);
        Assert.Equal($"compose -f {File} -p main config -q", Assert.Single(runner.Calls));
    }

    [Fact]
    public async Task Validate_reports_invalid_file()
    {
        var runner = new FakeRunner(_ => FakeRunner.Err("services.app.ports must be a list"));
        var (ok, message) = await Create(runner).ValidateFileAsync(Compose(), CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("ports", message);
    }

    [Fact]
    public async Task Image_cleanup_uses_exact_reference_without_force()
    {
        var runner = new FakeRunner(_ => FakeRunner.Ok());

        var result = await Create(runner).RemoveImageAsync("ghcr.io/org/app:1", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Equal("image rm -- ghcr.io/org/app:1", Assert.Single(runner.Calls));
    }

    // ---------- swarm: service update ----------

    private static FakeRunner SwarmRunner(Func<bool, string> inspectJson, Func<IReadOnlyList<string>, ProcessResult?>? extra = null)
    {
        var updated = false;
        return new FakeRunner(a =>
        {
            var custom = extra?.Invoke(a);
            if (custom is not null) return custom;
            if (a[0] == "service" && a[1] == "inspect") return FakeRunner.Ok(inspectJson(updated));
            if (a[0] == "service" && a[1] == "update") { updated = true; return FakeRunner.Ok("id"); }
            return FakeRunner.Ok();
        });
    }

    [Fact]
    public async Task Swarm_success()
    {
        var runner = SwarmRunner(updated => updated
            ? Svc("ghcr.io/org/app:2@sha256:new", "completed", "T2")
            : Svc("ghcr.io/org/app:1@sha256:old", "completed", "T1"));
        var r = Assert.Single(await Create(runner).ApplyAsync(Stack(), Img(), Timeout, CancellationToken.None));

        Assert.True(r.Success);
        Assert.Contains("service update --image ghcr.io/org/app:2 --with-registry-auth --update-failure-action rollback --detach prod_app", runner.Calls);
    }

    [Fact]
    public async Task Swarm_rolled_back_is_failure()
    {
        var runner = SwarmRunner(updated => updated
            ? Svc("ghcr.io/org/app:1@sha256:old", "rollback_completed", "T2", "update rolled back")
            : Svc("ghcr.io/org/app:1@sha256:old", "completed", "T1"));
        var r = Assert.Single(await Create(runner).ApplyAsync(Stack(), Img(), Timeout, CancellationToken.None));

        Assert.False(r.Success);
        Assert.Contains("rollback_completed", r.Message);
    }

    [Fact]
    public async Task Swarm_already_deployed_does_not_call_update()
    {
        var runner = SwarmRunner(_ => Svc("ghcr.io/org/app:2@sha256:x", "completed", "T1"));
        var r = Assert.Single(await Create(runner).ApplyAsync(Stack(), Img(), Timeout, CancellationToken.None));

        Assert.True(r.Success);
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("service update"));
    }

    [Fact]
    public async Task Swarm_missing_service_is_failure()
    {
        var runner = SwarmRunner(_ => "", a => a[0] == "service" ? FakeRunner.Err("Status: Error: no such service: prod_app, Code: 1") : null);
        var r = Assert.Single(await Create(runner).ApplyAsync(Stack(), Img(), Timeout, CancellationToken.None));

        Assert.False(r.Success);
        Assert.Contains("не найден", r.Message);
    }

    [Fact]
    public async Task Swarm_update_command_failure_is_reported()
    {
        var runner = SwarmRunner(_ => Svc("ghcr.io/org/app:1@sha256:old", "completed", "T1"),
            a => a[0] == "service" && a[1] == "update" ? FakeRunner.Err("unauthorized: authentication required") : null);
        var r = Assert.Single(await Create(runner).ApplyAsync(Stack(), Img(), Timeout, CancellationToken.None));

        Assert.False(r.Success);
        Assert.Contains("unauthorized", r.Message);
    }

    // ---------- swarm: stack deploy ----------

    [Fact]
    public async Task StackDeploy_pipes_rendered_config_to_stdin()
    {
        var updated = false;
        var runner = new FakeRunner(a =>
        {
            if (a[0] == "compose") return FakeRunner.Ok("services:\n  app:\n    image: ghcr.io/org/app:2\n");
            if (a[0] == "stack") { updated = true; return FakeRunner.Ok(); }
            if (a[0] == "service" && a[1] == "inspect")
                return FakeRunner.Ok(updated ? Svc("ghcr.io/org/app:2@sha256:n", "completed", "T2") : Svc("ghcr.io/org/app:1@sha256:o", "completed", "T1"));
            return FakeRunner.Ok();
        });
        var r = Assert.Single(await Create(runner).ApplyAsync(Stack(StackApplyMode.StackDeploy), Img(), Timeout, CancellationToken.None));

        Assert.True(r.Success);
        var i = runner.Calls.IndexOf("stack deploy --compose-file - --with-registry-auth prod");
        Assert.True(i >= 0);
        Assert.Contains("image: ghcr.io/org/app:2", runner.StdIns[i]);
    }
}
