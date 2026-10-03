using System.Diagnostics;
using Microsoft.Extensions.Options;
using VdsServiceUpdater.Compose;
using VdsServiceUpdater.Docker;
using VdsServiceUpdater.Images;
using VdsServiceUpdater.Options;

namespace VdsServiceUpdater.Deploy;

/// <summary>
/// Полный сценарий деплоя. Образ уже проверен ImageRequestValidator.
/// 1) план без блокировок (какие цели и сервисы затронуты); 2) блокировка файлов (занято = 409);
/// 3) под блокировкой: перечитать, изменить YAML, бэкап, запись, `compose config`, применение, ожидание;
/// 4) при неудаче: восстановить файл и повторно применить прежние образы.
/// Токен отмены должен быть токеном остановки приложения, а не запроса: обрыв клиента деплой не прерывает.
/// </summary>
public sealed class DeployOrchestrator(
    ComposeFileStore store, ServiceDeployer deployer, TargetLocks locks,
    IOptions<UpdaterOptions> options, ILogger<DeployOrchestrator> logger)
{
    public async Task<DeployOutcome> DeployAsync(ImageReference image, bool dryRun, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var cfg = options.Value.Deploy;
        var warnings = new List<string>();
        var reports = new List<ServiceReport>();
        var toExecute = new List<(TargetOptions Target, EditResult Edit)>();
        var anyMatch = false;

        // ---- 1. план (без блокировок) ----
        foreach (var t in options.Value.Targets)
        {
            FileSnapshot snap;
            try
            {
                snap = store.Read(t.File);
                ComposeFileScanner.Scan(snap.Text);
            }
            catch (ComposeFileException ex)
            {
                // Нечитаемая цель не должна ломать деплой в остальные.
                logger.LogWarning("Цель {Target} пропущена: {Reason}", t.Name, ex.Message);
                warnings.Add($"Цель '{t.Name}' пропущена: {ex.Message}");
                continue;
            }

            EditResult edit;
            try { edit = ComposeImageEditor.Apply(snap.Text, image, t.Protected); }
            catch (ComposeFileException ex)
            {
                anyMatch = true;
                reports.Add(new ServiceReport(t.Name, "*", "failed", ex.Code, null, null, ex.Message));
                continue;
            }

            if (edit.Changes.Count == 0) continue;
            anyMatch = true;
            if (dryRun || !edit.HasUpdates) reports.AddRange(ToReports(t, edit, planned: dryRun));
            else toExecute.Add((t, edit));
        }

        if (!anyMatch)
            return Build(404, "error", "service_not_found",
                "Не найдено сервисов с этим образом в настроенных целях.", reports, warnings, clock);

        // ---- 2. блокировки ----
        var held = new List<IDisposable>();
        try
        {
            foreach (var (t, _) in toExecute)
            {
                var l = locks.TryAcquire(t.File);
                if (l is null)
                    return Build(409, "error", "deploy_in_progress",
                        $"В цель '{t.Name}' уже идёт другой деплой.", [], warnings, clock);
                held.Add(l);
            }

            // ---- 3. выполнение ----
            var budget = TimeSpan.FromSeconds(cfg.WaitTimeoutSeconds);
            TimeSpan Remaining() => budget - clock.Elapsed;
            foreach (var (t, _) in toExecute)
                reports.AddRange(await ExecuteTargetAsync(t, image, Remaining, ct));
        }
        finally
        {
            foreach (var h in held) h.Dispose();
        }

        var (http, status, code, message) = Classify(reports, dryRun);
        return Build(http, status, code, message, reports, warnings, clock);
    }

    private async Task<List<ServiceReport>> ExecuteTargetAsync(TargetOptions t, ImageReference image, Func<TimeSpan> remaining, CancellationToken ct)
    {
        var cfg = options.Value.Deploy;
        string? backup = null;
        List<ServiceReport> reports = [];

        try
        {
            var snap = store.Read(t.File); // под блокировкой: актуальное содержимое
            var edit = ComposeImageEditor.Apply(snap.Text, image, t.Protected);
            reports = ToReports(t, edit, planned: false);

            var updated = edit.Changes.Where(c => c.Outcome == ServiceOutcome.Updated).ToList();
            if (updated.Count == 0) return reports; // изменилось между планом и блокировкой

            var newImages = updated.ToDictionary(c => c.Service, c => c.NewImage);
            var oldImages = updated.ToDictionary(c => c.Service, c => c.PreviousImage);

            backup = store.Backup(t.Name, t.File);
            store.WriteAtomic(t.File, edit.Content, snap.HasBom, snap.Sha256);
            logger.LogInformation("Файл {File} обновлён, бэкап: {Backup}", t.File, backup);

            var (valid, validationMessage) = await deployer.ValidateFileAsync(t, ct);
            if (!valid)
            {
                store.Restore(backup, t.File);
                FailAll(reports, updated, "compose_config_invalid", validationMessage, "failed");
                return reports;
            }

            if (remaining() <= TimeSpan.Zero)
            {
                store.Restore(backup, t.File);
                FailAll(reports, updated, "timeout", "Истекло время, отведённое на деплой.", "failed");
                return reports;
            }

            var results = await deployer.ApplyAsync(t, newImages, remaining(), ct);
            if (results.All(r => r.Success)) return reports;

            foreach (var r in results.Where(r => !r.Success))
                Mark(reports, r.Service, x => x with { Status = "failed", Code = IsTimeout(r.Message) ? "timeout" : "apply_failed", Message = r.Message });

            if (!cfg.RollbackOnFailure) return reports;

            // ---- 4. откат: файл + прежние образы ----
            logger.LogWarning("Деплой в {Target} не удался, откат к прежним образам", t.Name);
            store.Restore(backup, t.File);
            var rollback = (await deployer.ApplyAsync(t, oldImages, TimeSpan.FromSeconds(cfg.WaitTimeoutSeconds), ct))
                .ToDictionary(r => r.Service);

            foreach (var c in updated)
            {
                var ok = rollback.TryGetValue(c.Service, out var rr) && rr.Success;
                if (!ok) logger.LogError("Откат сервиса {Target}/{Service} НЕ удался: {Message}", t.Name, c.Service, rr?.Message);
                Mark(reports, c.Service, x => x with
                {
                    Status = ok ? "rolled_back" : "rollback_failed",
                    Message = ok ? x.Message ?? "Откатён вместе с остальными сервисами цели." : $"Откат не удался: {rr?.Message}"
                });
            }
            return reports;
        }
        catch (OperationCanceledException)
        {
            TryRestore(backup, t.File); // приложение останавливается: хотя бы не оставляем файл изменённым
            throw;
        }
        catch (ComposeFileException ex)
        {
            TryRestore(backup, t.File);
            return [new ServiceReport(t.Name, "*", "failed", ex.Code, null, null, ex.Message)];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Непредвиденная ошибка при деплое в {Target}", t.Name);
            TryRestore(backup, t.File);
            return [new ServiceReport(t.Name, "*", "failed", "internal_error", null, null, ex.Message)];
        }
    }

    // ---------------- helpers ----------------

    private void TryRestore(string? backup, string file)
    {
        if (backup is null) return;
        try { store.Restore(backup, file); }
        catch (Exception ex) { logger.LogError(ex, "Не удалось восстановить {File} из {Backup}. Восстановите вручную.", file, backup); }
    }

    private static List<ServiceReport> ToReports(TargetOptions t, EditResult edit, bool planned) =>
        edit.Changes.Select(c => c.Outcome switch
        {
            ServiceOutcome.Updated => new ServiceReport(t.Name, c.Service, planned ? "planned" : "updated", null, c.PreviousImage, c.NewImage, null),
            ServiceOutcome.Unchanged => new ServiceReport(t.Name, c.Service, "unchanged", null, c.PreviousImage, c.NewImage, null),
            ServiceOutcome.SkippedProtected => new ServiceReport(t.Name, c.Service, "skipped", "protected", c.PreviousImage, null, "Сервис защищён от обновления."),
            ServiceOutcome.SkippedVariable => new ServiceReport(t.Name, c.Service, "skipped", "image_uses_variable", c.PreviousImage, null, "image: использует переменную, автоматическое обновление невозможно."),
            _ => new ServiceReport(t.Name, c.Service, "skipped", "pinned_by_digest", c.PreviousImage, null, "Образ закреплён по digest.")
        }).ToList();

    private static void FailAll(List<ServiceReport> reports, IEnumerable<ServiceChange> updated, string code, string message, string status)
    {
        foreach (var c in updated)
            Mark(reports, c.Service, x => x with { Status = status, Code = code, Message = message });
    }

    private static void Mark(List<ServiceReport> reports, string service, Func<ServiceReport, ServiceReport> change)
    {
        var i = reports.FindIndex(r => r.Service == service);
        if (i >= 0) reports[i] = change(reports[i]);
    }

    private static bool IsTimeout(string message) =>
        message.Contains("таймаут", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("истекло время", StringComparison.OrdinalIgnoreCase);

    private static (int Http, string Status, string? Code, string? Message) Classify(IReadOnlyList<ServiceReport> reports, bool dryRun)
    {
        var failed = reports.Where(r => r.Status is "failed" or "rolled_back" or "rollback_failed").ToList();
        if (failed.Count > 0)
        {
            var message = failed.Select(r => r.Message).FirstOrDefault(m => !string.IsNullOrEmpty(m));
            if (failed.Any(r => r.Status == "rollback_failed"))
                return (500, "failed", "rollback_failed", "Деплой не удался, а откат завершился ошибкой: требуется ручное вмешательство. " + message);
            if (failed.All(r => r.Code == "unsupported_format"))
                return (422, "failed", "unsupported_format", message);
            if (failed.Any(r => r.Code == "timeout"))
                return (504, "failed", "timeout", message);
            return (500, "failed", "deploy_failed", message);
        }

        if (!reports.Any(r => r.Status is "updated" or "unchanged" or "planned"))
        {
            if (reports.Any(r => r.Code == "image_uses_variable"))
                return (422, "error", "image_uses_variable", "image: использует переменную окружения, автоматическое обновление невозможно.");
            if (reports.Any(r => r.Code == "pinned_by_digest"))
                return (422, "error", "pinned_by_digest", "Образ закреплён по digest, автоматическое обновление невозможно.");
            return (403, "error", "service_protected", "Все подходящие сервисы защищены от обновления.");
        }

        var status = dryRun ? "dry_run" : reports.Any(r => r.Status == "updated") ? "updated" : "unchanged";
        return (200, status, null, null);
    }

    private static DeployOutcome Build(int http, string status, string? code, string? message,
        IReadOnlyList<ServiceReport> reports, List<string> warnings, Stopwatch clock) =>
        new(http, new DeployResponse(status, code, message, reports, warnings.Count > 0 ? warnings : null, clock.ElapsedMilliseconds));
}
