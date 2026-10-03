using System.Text.Json;

namespace VdsServiceUpdater.Docker;

public sealed record ContainerState(string Id, string Status, string? Health, int RestartCount, int ExitCode, string? Image);

/// <summary>Образ swarm-сервиса (после обновления вида "name:tag@sha256:...") и состояние последнего обновления.</summary>
public sealed record ServiceInfo(string? Image, string? UpdateState, string? UpdateStartedAt, string? UpdateMessage);

public sealed record WaitResult(bool Success, string Message);

public sealed class DockerCommandException(string message) : Exception(message);

public static class DockerJson
{
    /// <summary>Разбор вывода `docker inspect id...` (массив).</summary>
    public static IReadOnlyList<ContainerState> ParseContainers(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        var list = new List<ContainerState>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var state = el.GetProperty("State");
            list.Add(new ContainerState(
                Str(el, "Id") ?? "",
                Str(state, "Status") ?? "unknown",
                state.TryGetProperty("Health", out var h) && h.ValueKind == JsonValueKind.Object ? Str(h, "Status") : null,
                el.TryGetProperty("RestartCount", out var rc) && rc.TryGetInt32(out var restarts) ? restarts : 0,
                state.TryGetProperty("ExitCode", out var ec) && ec.TryGetInt32(out var exit) ? exit : 0,
                el.TryGetProperty("Config", out var cfg) ? Str(cfg, "Image") : null));
        }
        return list;
    }

    /// <summary>Разбор вывода `docker service inspect --format '{{json .}}'`.</summary>
    public static ServiceInfo ParseService(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? image = null;
        if (root.TryGetProperty("Spec", out var spec) &&
            spec.TryGetProperty("TaskTemplate", out var tt) &&
            tt.TryGetProperty("ContainerSpec", out var cs))
            image = Str(cs, "Image");

        string? state = null, started = null, message = null;
        if (root.TryGetProperty("UpdateStatus", out var us) && us.ValueKind == JsonValueKind.Object)
        {
            state = Str(us, "State");
            started = Str(us, "StartedAt");
            message = Str(us, "Message");
        }
        return new ServiceInfo(image, state, started, message);
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
