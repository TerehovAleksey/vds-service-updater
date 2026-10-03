using Microsoft.Extensions.Options;

namespace VdsServiceUpdater.Options;

/// <summary>Fail-fast проверка конфигурации на старте приложения.</summary>
public sealed class UpdaterOptionsValidator : IValidateOptions<UpdaterOptions>
{
    public ValidateOptionsResult Validate(string? name, UpdaterOptions o)
    {
        var e = new List<string>();

        if (o.Deploy.AllowedImagePrefixes.Count == 0 || o.Deploy.AllowedImagePrefixes.Any(string.IsNullOrWhiteSpace))
            e.Add("Updater:Deploy:AllowedImagePrefixes: нужен хотя бы один непустой префикс (например 'ghcr.io/myorg/').");

        if (o.Deploy.WaitTimeoutSeconds is < 10 or > 3600)
            e.Add("Updater:Deploy:WaitTimeoutSeconds: допустимо 10..3600.");
        if (o.Deploy.StabilitySeconds is < 0 or > 300)
            e.Add("Updater:Deploy:StabilitySeconds: допустимо 0..300.");
        if (o.Deploy.BackupsToKeep < 1)
            e.Add("Updater:Deploy:BackupsToKeep: минимум 1.");
        if (!Path.IsPathRooted(o.Deploy.BackupDirectory))
            e.Add("Updater:Deploy:BackupDirectory: нужен абсолютный путь.");

        if (!string.IsNullOrEmpty(o.Auth.Token) && (o.Auth.Token.Length < 16 || o.Auth.Token != o.Auth.Token.Trim()))
            e.Add("Updater:Auth:Token: минимум 16 символов, без пробелов по краям (или оставьте пустым, чтобы отключить защиту).");

        if (o.Security.RequestsPerMinute is < 1 or > 6000)
            e.Add("Updater:Security:RequestsPerMinute: допустимо 1..6000.");
        if (o.Security.MaxBodyBytes is < 256 or > 65536)
            e.Add("Updater:Security:MaxBodyBytes: допустимо 256..65536.");

        foreach (var n in o.Proxy.KnownNetworks)
            if (!System.Net.IPNetwork.TryParse(n, out _))
                e.Add($"Updater:Proxy:KnownNetworks: '{n}' не является подсетью в формате CIDR.");

        foreach (var p in o.Proxy.KnownProxies)
            if (!System.Net.IPAddress.TryParse(p, out _))
                e.Add($"Updater:Proxy:KnownProxies: '{p}' не является IP-адресом.");

        if (o.Targets.Count == 0)
            e.Add("Updater:Targets: нужна хотя бы одна цель.");

        var dupNames = o.Targets.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
        foreach (var g in dupNames) e.Add($"Updater:Targets: имя '{g.Key}' повторяется.");

        var dupFiles = o.Targets.GroupBy(t => t.File).Where(g => g.Count() > 1);
        foreach (var g in dupFiles) e.Add($"Updater:Targets: файл '{g.Key}' указан в нескольких целях.");

        for (var i = 0; i < o.Targets.Count; i++)
        {
            var t = o.Targets[i];
            var p = $"Updater:Targets[{i}] ({(string.IsNullOrWhiteSpace(t.Name) ? "?" : t.Name)})";
            if (string.IsNullOrWhiteSpace(t.Name)) e.Add($"{p}: не задано Name.");
            else if (!System.Text.RegularExpressions.Regex.IsMatch(t.Name, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$"))
                e.Add($"{p}: Name допускает только латиницу, цифры, '.', '_', '-' (до 64 символов), он используется в путях бэкапов.");
            if (!Path.IsPathRooted(t.File)) e.Add($"{p}: File должен быть абсолютным путём.");
            if (t.Type == TargetType.Stack && string.IsNullOrWhiteSpace(t.StackName))
                e.Add($"{p}: для type=Stack обязателен StackName.");
            if (t.Protected.Any(string.IsNullOrWhiteSpace))
                e.Add($"{p}: Protected содержит пустую маску.");
        }

        return e.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(e);
    }
}
