using System;
using System.IO;
using Dalamud.Plugin;

namespace FateWalker.Controller;

/// <summary>
/// Per-session log file. One file is created the first time <c>Append</c> is
/// called after <c>Start()</c>, named <c>session_yyyyMMdd_HHmmss.log</c> under
/// the plugin's config directory. Lines are appended directly with a UTC
/// timestamp. The file stream is held open for the whole session and flushed
/// on every write so a crash doesn't lose the tail.
///
/// Intended for offline review of bot runs — quicker than tailing Dalamud's
/// global log and only contains FateWalker's controller log entries.
/// </summary>
public sealed class SessionFileLogger : IDisposable
{
    private readonly string _logDir;
    private StreamWriter? _writer;
    private string? _currentFile;

    public string? CurrentFilePath => _currentFile;

    public SessionFileLogger(IDalamudPluginInterface pi)
    {
        // ConfigDirectory is the per-plugin path Dalamud guarantees.
        _logDir = Path.Combine(pi.ConfigDirectory.FullName, "logs");
        Directory.CreateDirectory(_logDir);
    }

    /// <summary>Open a fresh log file. Closes any previous session.</summary>
    public void BeginSession()
    {
        End();
        var name = $"session_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        _currentFile = Path.Combine(_logDir, name);
        _writer = new StreamWriter(_currentFile, append: true) { AutoFlush = true };
        _writer.WriteLine($"# FateWalker session start — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    public void End()
    {
        if (_writer == null) return;
        try
        {
            _writer.WriteLine($"# FateWalker session end — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _writer.Dispose();
        }
        catch { /* best-effort */ }
        _writer = null;
        _currentFile = null;
    }

    /// <summary>Append one already-formatted line. No-op if no active session.</summary>
    public void Append(string line)
    {
        if (_writer == null) return;
        try { _writer.WriteLine(line); }
        catch { /* disk full / permission — swallow, in-game log keeps going */ }
    }

    public void Dispose() => End();
}
