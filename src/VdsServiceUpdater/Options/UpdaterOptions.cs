namespace VdsServiceUpdater.Options;

public enum TargetType { Compose, Stack }

/// <summary>Как применять изменения в swarm.</summary>
public enum StackApplyMode
{
    /// <summary>docker service update --image ... (по умолчанию, не зависит от ${VAR})</summary>
    ServiceUpdate,
    /// <summary>docker compose config | docker stack deploy -c - (применяет все правки файла)</summary>
    StackDeploy
}

public sealed class UpdaterOptions
{
    public const string SectionName = "Updater";

    public AuthOptions Auth { get; set; } = new();
    public DeployOptions Deploy { get; set; } = new();
    public ProxyOptions Proxy { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public List<TargetOptions> Targets { get; set; } = new();
}

public sealed class AuthOptions
{
    /// <summary>Пусто = защита выключена (в логе будет предупреждение). Если задан, минимум 16 символов.</summary>
    public string? Token { get; set; }
}

public sealed class DeployOptions
{
    /// <summary>Разрешённые префиксы образов, например "ghcr.io/myorg/". Обязателен (deny by default).</summary>
    public List<string> AllowedImagePrefixes { get; set; } = new();
    public bool AllowLatestTag { get; set; } = false;
    public int WaitTimeoutSeconds { get; set; } = 120;
    /// <summary>Сколько секунд сервис должен пробыть стабильным (compose без healthcheck).</summary>
    public int StabilitySeconds { get; set; } = 10;
    public bool RollbackOnFailure { get; set; } = true;
    /// <summary>Куда складывать бэкапы YAML перед правкой.</summary>
    public string BackupDirectory { get; set; } = "/data/backups";
    public int BackupsToKeep { get; set; } = 20;
}

public sealed class ProxyOptions
{
    /// <summary>IP доверенных прокси (nginx) для X-Forwarded-*.</summary>
    public List<string> KnownProxies { get; set; } = new();
    /// <summary>Подсети доверенных прокси в CIDR, например "172.18.0.0/16".</summary>
    public List<string> KnownNetworks { get; set; } = new();
}

public sealed class SecurityOptions
{
    /// <summary>Лимит запросов к /api с одного IP в минуту.</summary>
    public int RequestsPerMinute { get; set; } = 30;
    /// <summary>Максимальный размер тела запроса.</summary>
    public int MaxBodyBytes { get; set; } = 4096;
}

public sealed class TargetOptions
{
    public string Name { get; set; } = "";
    public TargetType Type { get; set; } = TargetType.Compose;
    /// <summary>Абсолютный путь, совпадающий на хосте и в контейнере апдейтера.</summary>
    public string File { get; set; } = "";
    /// <summary>Имя стека (обязательно для swarm; для compose = имя проекта, необязательно).</summary>
    public string? StackName { get; set; }
    public StackApplyMode ApplyMode { get; set; } = StackApplyMode.ServiceUpdate;
    /// <summary>Glob-маски имён сервисов, которые никогда не обновляются.</summary>
    public List<string> Protected { get; set; } = new();
}
