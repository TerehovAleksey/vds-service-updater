using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using VdsServiceUpdater.Security;

namespace VdsServiceUpdater.Docker;

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Args, TimeSpan Timeout, string? WorkingDirectory = null, string? StdIn = null);

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => !TimedOut && ExitCode == 0;
}

public interface IProcessRunner
{
    /// <summary>Запускает процесс без shell (аргументы передаются списком). Отмена через ct бросает OperationCanceledException.</summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct);
}

public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    public const int MaxCapturedChars = 64 * 1024;

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = spec.StdIn is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (spec.WorkingDirectory is not null) psi.WorkingDirectory = spec.WorkingDirectory;
        foreach (var a in spec.Args) psi.ArgumentList.Add(a);

        var stdout = new TailBuffer(MaxCapturedChars);
        var stderr = new TailBuffer(MaxCapturedChars);
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        logger.LogInformation("Запуск: {Command}", LogSanitizer.Sanitize($"{spec.FileName} {string.Join(' ', spec.Args)}", 400));

        try { process.Start(); }
        catch (Win32Exception ex)
        {
            return new ProcessResult(127, "", $"Не удалось запустить '{spec.FileName}': {ex.Message}", false);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(spec.Timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : spec.Timeout);
        try
        {
            if (spec.StdIn is not null)
            {
                try { await process.StandardInput.WriteAsync(spec.StdIn.AsMemory(), cts.Token); }
                catch (IOException) { /* процесс завершился, не дочитав stdin */ }
                finally { try { process.StandardInput.Close(); } catch (IOException) { } }
            }
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(grace.Token); } catch (OperationCanceledException) { }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), false);
    }

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    /// <summary>Хранит последние N символов вывода (ошибки обычно в конце).</summary>
    private sealed class TailBuffer(int max)
    {
        private readonly StringBuilder _sb = new();
        private readonly object _lock = new();

        public void AppendLine(string line)
        {
            lock (_lock)
            {
                _sb.AppendLine(line);
                if (_sb.Length > max) _sb.Remove(0, _sb.Length - max);
            }
        }

        public override string ToString() { lock (_lock) return _sb.ToString(); }
    }
}
