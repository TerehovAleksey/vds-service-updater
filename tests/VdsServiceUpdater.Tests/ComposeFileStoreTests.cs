using Xunit;
using VdsServiceUpdater.Compose;
using VdsServiceUpdater.Options;

namespace VdsServiceUpdater.Tests;

public sealed class ComposeFileStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vdsu-").FullName;
    private string File1 => Path.Combine(_dir, "compose.yml");

    private ComposeFileStore Store(int keep = 3)
    {
        var o = new UpdaterOptions();
        o.Deploy.BackupDirectory = Path.Combine(_dir, "backups");
        o.Deploy.BackupsToKeep = keep;
        return new ComposeFileStore(Microsoft.Extensions.Options.Options.Create(o));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Write_replaces_content_and_leaves_no_temp_files()
    {
        File.WriteAllText(File1, "old");
        var store = Store();
        var snap = store.Read(File1);
        store.WriteAtomic(File1, "new", snap.HasBom, snap.Sha256);
        Assert.Equal("new", File.ReadAllText(File1));
        Assert.Equal([File1], Directory.GetFiles(_dir));
    }

    [Fact]
    public void Bom_is_preserved()
    {
        File.WriteAllBytes(File1, [0xEF, 0xBB, 0xBF, (byte)'a']);
        var store = Store();
        var snap = store.Read(File1);
        Assert.True(snap.HasBom);
        Assert.Equal("a", snap.Text);
        store.WriteAtomic(File1, "b", snap.HasBom, snap.Sha256);
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'b'], File.ReadAllBytes(File1));
    }

    [Fact]
    public void Write_fails_if_file_changed_since_read()
    {
        File.WriteAllText(File1, "old");
        var store = Store();
        var snap = store.Read(File1);
        File.WriteAllText(File1, "edited by human");
        var ex = Assert.Throws<ComposeFileException>(() => store.WriteAtomic(File1, "new", false, snap.Sha256));
        Assert.Equal("file_changed", ex.Code);
        Assert.Equal("edited by human", File.ReadAllText(File1));
    }

    [Fact]
    public void Backup_then_restore_returns_original()
    {
        File.WriteAllText(File1, "original");
        var store = Store();
        var backup = store.Backup("main", File1);
        File.WriteAllText(File1, "broken");
        store.Restore(backup, File1);
        Assert.Equal("original", File.ReadAllText(File1));
    }

    [Fact]
    public void Backups_are_pruned_to_limit_keeping_newest()
    {
        File.WriteAllText(File1, "x");
        var store = Store(keep: 3);
        string last = "";
        for (var i = 0; i < 5; i++) last = store.Backup("main", File1);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_dir, "backups", "main")).Length);
        Assert.True(File.Exists(last));
    }

    [Fact]
    public void Missing_file_has_code() =>
        Assert.Equal("file_not_found", Assert.Throws<ComposeFileException>(() => Store().Read(File1)).Code);
}
