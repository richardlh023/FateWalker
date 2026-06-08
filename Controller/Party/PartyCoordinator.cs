// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace FateWalker.Controller.Party;

/// <summary>
/// Heart of multi-client party coordination. Owns:
///   • the bus (PartyBus) — cross-process message exchange
///   • the role state — Off / Host / Follower / Auto-then-elected
///   • the current FATE assignment that came in from a Host (for Followers)
///   • the host-side heartbeat that re-publishes the assignment so a late
///     Follower converges within one beat
///   • host-election arithmetic for Auto mode (lowest ContentID wins)
///
/// All public methods are called from the Dalamud framework thread by
/// FateController. The bus's inbound queue is drained at the start of each
/// <see cref="Tick"/> so callers see a consistent state for the rest of the
/// frame.
///
/// Anti-detection notes baked into the design:
///   • Slot ordering is by ContentID ascending → deterministic, no negotiation
///     races, no flapping when members ping in/out.
///   • Formation seed angle is per-FATE → the group doesn't always cluster on
///     the same compass bearing across FATEs (visual tell).
///   • Heartbeat skews ±10 % per host to avoid two Hosts emitting on the
///     same millisecond and clobbering each other on the bus (TinyIpc serialises
///     publishes but the visible-on-bus interval still betrays robotic regularity).
/// </summary>
public sealed class PartyCoordinator : IDisposable
{
    /// <summary>Effective role this frame after Auto-mode election runs.</summary>
    public enum EffectiveRole { Off, Host, Follower }

    private readonly Configuration _cfg;
    private readonly IPluginLog _log;
    private readonly Action<string> _logAction;
    private readonly PartyBus _bus;

    private EffectiveRole _role = EffectiveRole.Off;
    private DateTime _lastHostHeartbeatSent = DateTime.MinValue;
    private DateTime _lastHostMessageHeard  = DateTime.MinValue;
    private ulong    _currentHostCid;

    // Follower's last-known assignment from the Host.
    public uint   AssignedFateId   { get; private set; }
    public uint   AssignedTerritory{ get; private set; }
    public int    MySlotIdx        { get; private set; } = -1;
    public int    PartyCount       { get; private set; }
    /// <summary>Cached member CIDs from the Host's last FATE_ASSIGN. Used to size
    /// the formation when this client is a Follower (we trust the Host's view).</summary>
    public IReadOnlyList<ulong> AssignedMembers { get; private set; } = Array.Empty<ulong>();

    // Outgoing publish helpers — Host fills these from FateController state.
    public ulong  MyCid             { get; set; }
    public uint   MyFateId          { get; set; }   // Host: the FATE I just picked
    public uint   MyTerritory       { get; set; }
    public IReadOnlyList<ulong> CurrentPartyCids { get; set; } = Array.Empty<ulong>();

    public EffectiveRole Role => _role;
    public ulong HostCid => _currentHostCid;
    public string StatusLine => _role switch
    {
        EffectiveRole.Off      => "Party: Off",
        // Host doesn't receive its own broadcasts, so PartyCount is always 0 on
        // the Host side — read straight off the live party list instead.
        EffectiveRole.Host     => $"Party: Host · party {CurrentPartyCids.Count} · last beat {(DateTime.UtcNow - _lastHostHeartbeatSent).TotalSeconds:F1}s ago",
        EffectiveRole.Follower => $"Party: Follower · host cid={_currentHostCid} · fate {AssignedFateId} slot {MySlotIdx}",
        _ => "Party: ?",
    };

    public PartyCoordinator(Configuration cfg, IPluginLog log, Action<string> logAction)
    {
        _cfg = cfg;
        _log = log;
        _logAction = logAction;
        _bus = new PartyBus(log);
    }

    /// <summary>Compute the effective role for this frame, drain inbound messages,
    /// and emit heartbeat / role-claim as appropriate. Returns the effective role.
    /// Safe to call every framework tick.</summary>
    public EffectiveRole Tick()
    {
        var prev = _role;
        _role = ComputeEffectiveRole();

        if (_role == EffectiveRole.Off)
        {
            // Drain anyway so the queue doesn't grow unbounded if the user
            // disables Party mid-session.
            _bus.Drain(_ => { });
            return _role;
        }

        // 1. Process anything inbound first so the rest of this tick acts on it.
        _bus.Drain(HandleEnvelope);

        // 1b. Compute own slot deterministically from the local/authoritative
        // member list. Without this the Host's MySlotIdx stays at -1 forever
        // (Host doesn't receive its own broadcasts — HandleEnvelope drops them
        // via the loopback filter), so both Host and Follower fall into the
        // -1 branch of ChooseLandingOffset and stack on the FATE centre. With
        // it, every client knows its own slot whether or not the bus delivers
        // a message — same sorted-cid algorithm both ends use, no negotiation.
        var slotSource = AssignedMembers.Count > 0 ? AssignedMembers : CurrentPartyCids;
        MySlotIdx = PartyFormation.AssignSlot(MyCid, slotSource);
        if (PartyCount == 0) PartyCount = slotSource.Count;

        // 2. If we're Host, publish heartbeat / assignment.
        if (_role == EffectiveRole.Host && DueForHeartbeat())
            PublishHostBeat();

        // 3. Follower self-promotion if the Host has gone silent.
        if (_role == EffectiveRole.Follower && _cfg.PartyMode == PartyMode.Auto)
        {
            var silentSec = (DateTime.UtcNow - _lastHostMessageHeard).TotalSeconds;
            // 4× heartbeat = generous; we don't want flapping every time a single
            // beat is dropped.
            if (silentSec > _cfg.PartyHeartbeatSec * 4 && IsLowestCidInParty())
            {
                _logAction($"PartyMode: Auto re-election — host silent {silentSec:F0}s, promoting self");
                _role = EffectiveRole.Host;
                _currentHostCid = MyCid;
                _lastHostHeartbeatSent = DateTime.MinValue;
            }
        }

        if (prev != _role)
            _logAction($"PartyMode: role {prev} → {_role}");

        return _role;
    }

    /// <summary>Host calls this when it COMMITS to a FATE so the assignment goes
    /// out immediately instead of waiting up to one heartbeat.</summary>
    public void PublishFateAssign(uint fateId, uint territory)
    {
        if (_role != EffectiveRole.Host) return;
        MyFateId    = fateId;
        MyTerritory = territory;
        PublishHostBeat(force: true);
    }

    public void PublishFateClear()
    {
        if (_role != EffectiveRole.Host) return;
        MyFateId = 0;
        _bus.Publish(new PartyEnvelope
        {
            Kind      = PartyKind.FATE_CLEAR,
            FromCid   = MyCid,
            Ts        = PartyEnvelope.NowMs,
            Territory = MyTerritory,
        });
        _lastHostHeartbeatSent = DateTime.UtcNow;
    }

    public void Dispose() => _bus.Dispose();

    // ── internals ────────────────────────────────────────────────────────────

    private EffectiveRole ComputeEffectiveRole() => _cfg.PartyMode switch
    {
        PartyMode.Off      => EffectiveRole.Off,
        PartyMode.Host     => EffectiveRole.Host,
        PartyMode.Follower => EffectiveRole.Follower,
        PartyMode.Auto     => IsLowestCidInParty() ? EffectiveRole.Host : EffectiveRole.Follower,
        _ => EffectiveRole.Off,
    };

    private bool IsLowestCidInParty()
    {
        if (MyCid == 0) return false;
        if (CurrentPartyCids.Count == 0) return true;   // solo party = me
        return CurrentPartyCids.Where(c => c != 0).All(c => MyCid <= c);
    }

    private bool DueForHeartbeat()
    {
        var beat = _cfg.PartyHeartbeatSec;
        if (beat < 0.5f) beat = 0.5f;
        // ±10 % jitter so two Hosts (transient during election overlap) don't
        // emit on the same tick rhythm.
        var skewed = beat * (0.9f + 0.2f * (float)Random.Shared.NextDouble());
        return (DateTime.UtcNow - _lastHostHeartbeatSent).TotalSeconds >= skewed;
    }

    private void PublishHostBeat(bool force = false)
    {
        if (!force && !DueForHeartbeat()) return;
        _currentHostCid = MyCid;

        var assigns = PartyFormation.AssignSlots(CurrentPartyCids)
            .Select(t => new PartyEnvelope.Assignment { Cid = t.Cid, Idx = t.Idx })
            .ToList();

        var kind = MyFateId == 0 ? PartyKind.HEARTBEAT : PartyKind.FATE_ASSIGN;
        _bus.Publish(new PartyEnvelope
        {
            Kind      = kind,
            FromCid   = MyCid,
            Ts        = PartyEnvelope.NowMs,
            FateId    = MyFateId,
            Territory = MyTerritory,
            Assigns   = assigns,
        });
        _lastHostHeartbeatSent = DateTime.UtcNow;
    }

    private void HandleEnvelope(PartyEnvelope env)
    {
        // Drop my own loopback (TinyIpc broadcasts to all bus subscribers
        // including the sender).
        if (env.FromCid == MyCid) return;
        // Replay-drop very old messages.
        if (PartyEnvelope.NowMs - env.Ts > 10_000) return;

        switch (env.Kind)
        {
            case PartyKind.FATE_ASSIGN:
            case PartyKind.HEARTBEAT:
                AcceptHostMessage(env);
                break;
            case PartyKind.FATE_CLEAR:
                AcceptHostMessage(env);
                AssignedFateId = 0;
                MySlotIdx = -1;
                break;
            case PartyKind.ROLE_CLAIM:
                AcceptHostMessage(env);
                break;
        }
    }

    private void AcceptHostMessage(PartyEnvelope env)
    {
        // Tie-break: lowest CID wins if we hear from two Hosts.
        if (_currentHostCid != 0 && env.FromCid > _currentHostCid) return;

        _currentHostCid = env.FromCid;
        _lastHostMessageHeard = DateTime.UtcNow;

        if (env.Kind == PartyKind.FATE_ASSIGN)
        {
            AssignedFateId    = env.FateId;
            AssignedTerritory = env.Territory;
            var slot = env.Assigns.FirstOrDefault(a => a.Cid == MyCid);
            MySlotIdx = slot != null ? slot.Idx : -1;
            PartyCount = env.Assigns.Count;
            AssignedMembers = env.Assigns.Select(a => a.Cid).ToList();
        }
        else if (env.Kind == PartyKind.HEARTBEAT)
        {
            PartyCount = env.Assigns.Count;
            AssignedMembers = env.Assigns.Select(a => a.Cid).ToList();
        }
    }
}
