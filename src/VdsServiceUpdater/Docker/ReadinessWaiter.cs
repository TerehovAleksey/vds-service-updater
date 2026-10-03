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
        var last = "контейнеры сервиса не найдены";

        while (true)
        {
            var states = await getStates(ct);
            if (states.Count == 0)
            {
                stableFrom = null;
                last = "контейнеры сервиса не найдены";
            }
            else
            {
                var dead = states.FirstOrDefault(s => s.Status is "exited" or "dead");
                if (dead is not null)
                    return new(false, $"Контейнер {Short(dead.Id)} остановился (состояние {dead.Status}, код выхода {dead.ExitCode}).");
                var unhealthy = states.FirstOrDefault(s => s.Health == "unhealthy");
                if (unhealthy is not null)
                    return new(false, $"Контейнер {Short(unhealthy.Id)} не прошёл healthcheck (unhealthy).");

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
                    last = $"контейнер использует образ '{wrong.Image}', ожидался '{expectedImage}'";
                }
                else if (restarted || states.Any(s => s.Status == "restarting"))
                {
                    stableFrom = null;
                    last = "контейнер перезапускается (возможно, падает при старте)";
                }
                else if (states.All(s => s.Status == "running" && (s.Health is null or "healthy")))
                {
                    stableFrom ??= clock.Elapsed;
                    var needStability = states.Any(s => s.Health is null);
                    if (!needStability || clock.Elapsed - stableFrom >= stability)
                        return new(true, "Сервис запущен.");
                    last = "ждём стабильной работы контейнера";
                }
                else
                {
                    stableFrom = null;
                    last = "контейнер ещё не готов: " + string.Join(", ", states.Select(s => $"{s.Status}/{s.Health ?? "-"}"));
                }
            }

            if (clock.Elapsed >= timeout) return new(false, $"Таймаут ожидания готовности: {last}.");
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
        var last = "обновление ещё не началось";

        while (true)
        {
            var info = await inspect(ct);
            if (info is null) return new(false, "Сервис исчез из swarm во время обновления.");

            if (info.UpdateState is { } state && info.UpdateStartedAt != previousStartedAt)
            {
                switch (state)
                {
                    case "completed":
                        return new(true, "Обновление завершено.");
                    case "paused" or "rollback_completed" or "rollback_paused":
                        return new(false, $"Обновление не удалось (состояние {state}): {info.UpdateMessage}");
                    default:
                        last = $"состояние обновления: {state}";
                        break;
                }
            }

            if (clock.Elapsed >= timeout) return new(false, $"Таймаут ожидания обновления: {last}.");
            await Task.Delay(poll, ct);
        }
    }

    private static string Short(string id) => id.Length > 12 ? id[..12] : id;
}
