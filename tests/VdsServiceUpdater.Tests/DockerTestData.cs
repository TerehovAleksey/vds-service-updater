using System.Text.Json;
using VdsServiceUpdater.Docker;

namespace VdsServiceUpdater.Tests;

internal static class DockerTestData
{
    public static string Inspect(string status, string? health, int restart, int exit, string image, string id = "abc123def456")
    {
        var state = new Dictionary<string, object?> { ["Status"] = status, ["ExitCode"] = exit };
        if (health is not null) state["Health"] = new { Status = health };
        return JsonSerializer.Serialize(new[] { new { Id = id, RestartCount = restart, State = state, Config = new { Image = image } } });
    }

    public static string Svc(string image, string? state, string? started, string? msg = null)
    {
        var d = new Dictionary<string, object?> { ["Spec"] = new { TaskTemplate = new { ContainerSpec = new { Image = image } } } };
        if (state is not null) d["UpdateStatus"] = new { State = state, StartedAt = started, Message = msg };
        return JsonSerializer.Serialize(d);
    }

    public static ContainerState C(string status, string? health = null, int restarts = 0, string image = "app:2", string id = "c1") =>
        new(id, status, health, restarts, 0, image);
}

internal sealed class FakeRunner(Func<IReadOnlyList<string>, ProcessResult> handler) : IProcessRunner
{
    public List<string> Calls { get; } = [];
    public List<string?> StdIns { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add(string.Join(' ', spec.Args));
            StdIns.Add(spec.StdIn);
        }
        return Task.FromResult(handler(spec.Args));
    }

    public static ProcessResult Ok(string stdout = "") => new(0, stdout, "", false);
    public static ProcessResult Err(string stderr, int code = 1) => new(code, "", stderr, false);
}
