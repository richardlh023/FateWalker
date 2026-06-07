// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using TinyIpc.Messaging;

namespace FateWalker.Controller.Party;

/// <summary>
/// Wraps a <see cref="TinyMessageBus"/> on the channel <c>FateWalker.Party.v1</c>
/// so multiple FFXIV.exe processes on the same machine share a broadcast bus
/// (backed by a memory-mapped file + named mutex). Used by
/// <see cref="PartyCoordinator"/> to exchange host/follower messages without
/// going through Dalamud IPC (which is single-process).
///
/// TinyIpc raises the <c>MessageReceived</c> event on a worker thread; this
/// wrapper drops the message into an in-memory queue and exposes a synchronous
/// <see cref="Drain"/> method the coordinator pumps on the Dalamud framework
/// thread. That keeps all real game/state interaction single-threaded.
///
/// The channel name embeds a version (<c>v1</c>) so a future incompatible
/// envelope change can rotate to <c>v2</c> without confusing old peers.
/// Don't reuse "DalamudBroadcaster" — MBT and other multi-box plugins already
/// listen on it and our envelopes would clash.
/// </summary>
public sealed class PartyBus : IDisposable
{
    public const string Channel = "FateWalker.Party.v1";

    private readonly IPluginLog _log;
    private readonly TinyMessageBus _bus;
    private readonly ConcurrentQueue<PartyEnvelope> _inbox = new();
    private volatile bool _disposed;

    public PartyBus(IPluginLog log)
    {
        _log = log;
        _bus = new TinyMessageBus(Channel);
        _bus.MessageReceived += OnMessage;
    }

    private void OnMessage(object? sender, TinyMessageReceivedEventArgs e)
    {
        if (_disposed) return;
        try
        {
            var env = PartyEnvelope.TryDeserialize(e.Message.ToArray());
            if (env != null) _inbox.Enqueue(env);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "PartyBus.OnMessage: deserialize failed");
        }
    }

    /// <summary>Send an envelope. Best-effort; TinyIpc never throws on a
    /// successful publish but if the underlying MMF is gone it will.</summary>
    public void Publish(PartyEnvelope env)
    {
        if (_disposed) return;
        try { _bus.PublishAsync(BinaryData.FromBytes(env.Serialize())); }
        catch (Exception ex) { _log.Warning(ex, "PartyBus.Publish failed"); }
    }

    /// <summary>Drain queued inbound messages. Caller must be on the Dalamud
    /// framework thread (the coordinator pumps this from its Tick).</summary>
    public int Drain(Action<PartyEnvelope> handler)
    {
        int n = 0;
        while (_inbox.TryDequeue(out var env))
        {
            try { handler(env); }
            catch (Exception ex) { _log.Warning(ex, "PartyBus.Drain handler threw"); }
            n++;
        }
        return n;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _bus.MessageReceived -= OnMessage; } catch { }
        try { _bus.Dispose(); } catch { }
    }
}
