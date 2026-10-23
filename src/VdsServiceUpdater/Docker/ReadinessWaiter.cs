using System.Diagnostics;

namespace VdsServiceUpdater.Docker;

/// <summary>Ожидание готовности после применения. Не знает про docker: состояние приходит через делегаты.</summary>
public static class ReadinessWaiter
{
    /// <summary>
    /// Compose. Успех: все контейнеры сервиса работают на ожидаемом образе и (healthy, либо без healthcheck
    /// стабильно проработали <paramref name="stability"/>). Сразу провал: exited/dead/unhealthy.
    /// Перезапуски (RestartCount растёт) и состояние restarting сбрасывают отсчёт стабильности.
    /// </summary>
    public static async Task<WaitResult> WaitForContainersAsync(
        Func<CancellationToken, Task<IReadOnlyList<ContainerState>>> getStates,
        string expectedImage, TimeSpan timeout, TimeSpan stability, TimeSpan poll, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        TimeSpan? stableFrom = null;
        var restarts = new Dictionary<string, int>();
        var last = "no service containers found";

        while (true)
        {
            var states = await getStates(ct);
            if (states.Count == 0)
            {
                stableFrom = null;
                last = "no service containers found";
            }
            else
            {
                var dead = states.FirstOrDefault(s => s.Status is "exited" or "dead");
                if (dead is not null)
                    return new(false, $"Container {Short(dead.Id)} stopped (state {dead.Status}, exit code {dead.ExitCode}).");
                var unhealthy = states.FirstOrDefault(s => s.Health == "unhealthy");
                if (unhealthy is not null)
                    return new(false, $"Container {Short(unhealthy.Id)} failed its health check (unhealthy).");

                var restarted = false;
                foreach (var s in states)
                {
                    if (restarts.TryGetValue(s.Id, out var prev) && s.RestartCount > prev) restarted = true;
                    restarts[s.Id] = s.RestartCount;
                }

                var wrong = states.FirstOrDefault(s => s.Image != expectedImage);
                if (wrong is not null)
                {
                    stableFrom = null;
                    last = $"container is using image '{wrong.Image}', expected '{expectedImage}'";
                }
                else if (restarted || states.Any(s => s.Status == "restarting"))
                {
                    stableFrom = null;
                    last = "container is restarting (it may be crashing during startup)";
                }
                else if (states.All(s => s.Status == "running" && (s.Health is null or "healthy")))
                {
                    stableFrom ??= clock.Elapsed;
                    var needStability = states.Any(s => s.Health is null);
                    if (!needStability || clock.Elapsed - stableFrom >= stability)
                        return new(true, "Service is running.");
                    last = "waiting for the container to remain stable";
                }
                else
                {
                    stableFrom = null;
                    last = "container is not ready yet: " + string.Join(", ", states.Select(s => $"{s.Status}/{s.Health ?? "-"}"));
                }
            }

            if (clock.Elapsed >= timeout) return new(false, $"Timed out waiting for readiness: {last}.");
            await Task.Delay(poll, ct);
        }
    }

    /// <summary>
    /// Swarm. Ждём финальное состояние UpdateStatus именно нового обновления (StartedAt отличается от
    /// значения до обновления), иначе можно принять за результат старое "completed".
    /// </summary>
    public static async Task<WaitResult> WaitForSwarmUpdateAsync(
        Func<CancellationToken, Task<ServiceInfo?>> inspect,
        string? previousStartedAt, TimeSpan timeout, TimeSpan poll, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var last = "update has not started";

        while (true)
        {
            var info = await inspect(ct);
            if (info is null) return new(false, "Service disappeared from Swarm during the update.");

            if (info.UpdateState is { } state && info.UpdateStartedAt != previousStartedAt)
            {
                switch (state)
                {
                    case "completed":
                        return new(true, "Update completed.");
                    case "paused" or "rollback_completed" or "rollback_paused":
                        return new(false, $"Update failed (state {state}): {info.UpdateMessage}");
                    default:
                        last = $"update state: {state}";
                        break;
                }
            }

            if (clock.Elapsed >= timeout) return new(false, $"Timed out waiting for the update: {last}.");
            await Task.Delay(poll, ct);
        }
    }

    private static string Short(string id) => id.Length > 12 ? id[..12] : id;
}
