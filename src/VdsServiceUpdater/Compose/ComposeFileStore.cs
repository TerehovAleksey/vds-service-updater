using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using VdsServiceUpdater.Options;

namespace VdsServiceUpdater.Compose;

public sealed record FileSnapshot(string Text, bool HasBom, string Sha256);

/// <summary>Чтение, бэкап и атомарная запись YAML. Блокировки по файлам добавятся в оркестраторе (этап 5).</summary>
public sealed class ComposeFileStore(IOptions<UpdaterOptions> options)
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static long _counter;

    public FileSnapshot Read(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { throw new ComposeFileException("file_not_found", $"Файл не найден: {path}"); }

        var hasBom = bytes.AsSpan().StartsWith(Bom);
        var text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        return new FileSnapshot(text, hasBom, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    /// <summary>Копирует текущий файл в BackupDirectory/{target}/ и оставляет последние BackupsToKeep копий.</summary>
    public string Backup(string targetName, string path)
    {
        var deploy = options.Value.Deploy;
        var dir = Path.Combine(deploy.BackupDirectory, targetName);
        Directory.CreateDirectory(dir);

        var name = $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Interlocked.Increment(ref _counter):D6}_{Path.GetFileName(path)}";
        var backupPath = Path.Combine(dir, name);
        File.Copy(path, backupPath, overwrite: false);

        foreach (var old in Directory.GetFiles(dir).OrderByDescending(f => f, StringComparer.Ordinal).Skip(deploy.BackupsToKeep))
            File.Delete(old);

        return backupPath;
    }

    /// <summary>Записывает текст, только если файл не менялся с момента чтения (защита от гонки с ручной правкой).</summary>
    public void WriteAtomic(string path, string text, bool hasBom, string expectedSha256)
    {
        var current = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(current, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new ComposeFileException("file_changed", $"Файл {path} изменился во время деплоя, запись отменена.");

        var body = new UTF8Encoding(false).GetBytes(text);
        WriteBytesAtomic(path, hasBom ? [.. Bom, .. body] : body);
    }

    public void Restore(string backupPath, string path) =>
        WriteBytesAtomic(path, File.ReadAllBytes(backupPath));

    private static void WriteBytesAtomic(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows() && File.Exists(path))
                File.SetUnixFileMode(tmp, File.GetUnixFileMode(path)); // сохраняем права исходного файла
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }
}
