using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using VdsServiceUpdater.Docker;

namespace VdsServiceUpdater.Tests;

/// <summary>Реальные процессы; на Windows тесты пропускаются (нужны /bin/sh и /bin/echo).</summary>
public class ProcessRunnerTests
{
    private static readonly ProcessRunner Runner = new(NullLogger<ProcessRunner>.Instance);
    private static bool Skip => OperatingSystem.IsWindows();
    private static ProcessSpec Sh(string script, TimeSpan? timeout = null, string? stdin = null) =>
        new("/bin/sh", ["-c", script], timeout ?? TimeSpan.FromSeconds(10), StdIn: stdin);

    [Fact]
    public async Task Captures_output_and_exit_code()
    {
        if (Skip) return;
        var r = await Runner.RunAsync(Sh("echo out; echo err >&2; exit 3"), CancellationToken.None);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("out", r.StdOut);
        Assert.Contains("err", r.StdErr);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Timeout_kills_process()
    {
        if (Skip) return;
        var started = DateTime.UtcNow;
        var r = await Runner.RunAsync(Sh("sleep 10", TimeSpan.FromMilliseconds(300)), CancellationToken.None);
        Assert.True(r.TimedOut);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task Cancellation_throws()
    {
        if (Skip) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner.RunAsync(Sh("sleep 10"), cts.Token));
    }

    [Fact]
    public async Task Passes_stdin()
    {
        if (Skip) return;
        var r = await Runner.RunAsync(Sh("cat", stdin: "hello"), CancellationToken.None);
        Assert.Contains("hello", r.StdOut);
    }

    [Fact]
    public async Task Arguments_are_not_interpreted_by_a_shell()
    {
        if (Skip) return;
        var r = await Runner.RunAsync(new ProcessSpec("/bin/echo", ["$(whoami)", "&&", "x;y"], TimeSpan.FromSeconds(5)), CancellationToken.None);
        Assert.Equal("$(whoami) && x;y", r.StdOut.Trim());
    }

    [Fact]
    public async Task Missing_binary_gives_127()
    {
        var r = await Runner.RunAsync(new ProcessSpec("/nonexistent/binary-xyz", [], TimeSpan.FromSeconds(5)), CancellationToken.None);
        Assert.Equal(127, r.ExitCode);
    }

    [Fact]
    public async Task Output_is_truncated_to_tail()
    {
        if (Skip) return;
        var r = await Runner.RunAsync(Sh("head -c 200000 /dev/zero | tr '\\0' 'a'"), CancellationToken.None);
        Assert.True(r.StdOut.Length <= ProcessRunner.MaxCapturedChars + 2);
    }
}
