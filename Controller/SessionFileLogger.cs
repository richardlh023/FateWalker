using System;
using System.IO;
using Dalamud.Plugin;

namespace FateWalker.Controller;

/// <summary>
/// Per-session log file. One file per /fwalk start, named
/// <c>session_yyyyMMdd_HHmmss.log</c> under the plugin's config directory.
///
/// Writes are buffered (no AutoFlush) and committed to disk at most once
/// every <see cref="FlushIntervalSeconds"/> seconds. The previous design
/// flushed on every line — which meant every <c>LogAction</c> call did a
/// synchronous disk-sync on the framework thread. With ~5–10 log lines per
/// second during active farming this added up to a noticeable game stutter.
/// Tradeoff: a hard crash now loses up to ~2 s of log tail. Acceptable for
/// offline review.
///
/// All Append calls happen on the framework thread (LogAction → here), so
/// no internal lock is needed.
/// </summary>
public sealed class SessionFileLogger : IDisposable
{
    private const double FlushIntervalSeconds = 2.0;

    private readonly string _logDir;
    private StreamWriter? _writer;
    private string? _currentFile;
    private DateTime _lastFlushAt = DateTime.MinValue;

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
        _writer = new StreamWriter(_currentFile, append: true) { AutoFlush = false };
        _writer.WriteLine($"# FateWalker session start — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _lastFlushAt = DateTime.UtcNow;
    }

    public void End()
    {
        if (_writer == null) return;
        try
        {
            _writer.WriteLine($"# FateWalker session end — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _writer.Flush();
            _writer.Dispose();
        }
        catch { /* best-effort */ }
        _writer = null;
        _currentFile = null;
    }

    /// <summary>
    /// Append one already-formatted line. Writes go to the in-memory buffer
    /// and only hit disk when the flush interval has elapsed.
    /// </summary>
    public void Append(string line)
    {
        if (_writer == null) return;
        try
        {
            _writer.WriteLine(line);
            if ((DateTime.UtcNow - _lastFlushAt).TotalSeconds >= FlushIntervalSeconds)
            {
                _writer.Flush();
                _lastFlushAt = DateTime.UtcNow;
            }
        }
        catch { /* disk full / permission — swallow, in-game log keeps going */ }
    }

    public void Dispose() => End();
}
