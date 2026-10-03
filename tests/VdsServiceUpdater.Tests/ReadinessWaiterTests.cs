using Xunit;
using VdsServiceUpdater.Docker;

namespace VdsServiceUpdater.Tests;

public class ReadinessWaiterTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);

    private static Func<CancellationToken, Task<IReadOnlyList<ContainerState>>> Seq(params IReadOnlyList<ContainerState>[] steps)
    {
        var i = 0;
        return _ => Task.FromResult(steps[Math.Min(i++, steps.Length - 1)]);
    }

    private static Task<WaitResult> Wait(Func<CancellationToken, Task<IReadOnlyList<ContainerState>>> f, TimeSpan? stability = null, TimeSpan? timeout = null) =>
        ReadinessWaiter.WaitForContainersAsync(f, "app:2", timeout ?? Short, stability ?? TimeSpan.Zero, Poll, CancellationToken.None);

    [Fact]
    public async Task Running_without_healthcheck_is_ready_after_zero_stability() =>
        Assert.True((await Wait(Seq([DockerTestData.C("running")]))).Success);

    [Fact]
    public async Task Waits_for_healthy()
    {
        var r = await Wait(Seq([DockerTestData.C("running", "starting")], [DockerTestData.C("running", "starting")], [DockerTestData.C("running", "healthy")]));
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Exited_fails_immediately()
    {
        var r = await Wait(Seq([DockerTestData.C("exited")]), timeout: TimeSpan.FromSeconds(30));
        Assert.False(r.Success);
        Assert.Contains("остановился", r.Message);
    }

    [Fact]
    public async Task Unhealthy_fails_immediately()
    {
        var r = await Wait(Seq([DockerTestData.C("running", "unhealthy")]), timeout: TimeSpan.FromSeconds(30));
        Assert.False(r.Success);
        Assert.Contains("healthcheck", r.Message);
    }

    [Fact]
    public async Task Old_image_is_not_ready_until_replaced()
    {
        var r = await Wait(Seq([DockerTestData.C("running", image: "app:1")], [DockerTestData.C("running", image: "app:2")]));
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Wrong_image_times_out_with_reason()
    {
        var r = await Wait(Seq([DockerTestData.C("running", image: "app:1")]));
        Assert.False(r.Success);
        Assert.Contains("Таймаут", r.Message);
        Assert.Contains("app:1", r.Message);
    }

    [Fact]
    public async Task No_containers_times_out()
    {
        var r = await Wait(Seq(Array.Empty<ContainerState>()));
        Assert.False(r.Success);
        Assert.Contains("не найдены", r.Message);
    }

    [Fact]
    public async Task Crash_loop_never_becomes_stable()
    {
        var n = 0;
        Func<CancellationToken, Task<IReadOnlyList<ContainerState>>> f =
            _ => Task.FromResult<IReadOnlyList<ContainerState>>([DockerTestData.C("running", restarts: n++)]);
        var r = await Wait(f, stability: TimeSpan.FromMilliseconds(40));
        Assert.False(r.Success);
        Assert.Contains("перезапускается", r.Message);
    }

    // ---- swarm ----

    private static Func<CancellationToken, Task<ServiceInfo?>> SvcSeq(params ServiceInfo?[] steps)
    {
        var i = 0;
        return _ => Task.FromResult(steps[Math.Min(i++, steps.Length - 1)]);
    }

    private static Task<WaitResult> WaitSwarm(Func<CancellationToken, Task<ServiceInfo?>> f, string? prev = "T1") =>
        ReadinessWaiter.WaitForSwarmUpdateAsync(f, prev, Short, Poll, CancellationToken.None);

    [Fact]
    public async Task Swarm_waits_for_new_update_to_complete()
    {
        var r = await WaitSwarm(SvcSeq(
            new ServiceInfo("app:2", "completed", "T1", null),   // старое обновление, игнорируем
            new ServiceInfo("app:2", "updating", "T2", null),
            new ServiceInfo("app:2", "completed", "T2", null)));
        Assert.True(r.Success);
    }

    [Fact]
    public async Task Swarm_old_completed_status_is_not_a_result()
    {
        var r = await WaitSwarm(SvcSeq(new ServiceInfo("app:2", "completed", "T1", null)));
        Assert.False(r.Success);
        Assert.Contains("Таймаут", r.Message);
    }

    [Theory]
    [InlineData("rollback_completed")]
    [InlineData("paused")]
    [InlineData("rollback_paused")]
    public async Task Swarm_failure_states(string state)
    {
        var r = await WaitSwarm(SvcSeq(new ServiceInfo("app:2", state, "T2", "task failed")));
        Assert.False(r.Success);
        Assert.Contains(state, r.Message);
        Assert.Contains("task failed", r.Message);
    }

    [Fact]
    public async Task Swarm_service_disappearing_fails()
    {
        var r = await WaitSwarm(SvcSeq((ServiceInfo?)null));
        Assert.False(r.Success);
    }
}
