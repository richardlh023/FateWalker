// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FateWalker.Controller.Party;

/// <summary>
/// Wire-format JSON envelope for messages travelling on the cross-client
/// TinyIpc bus (channel name <c>FateWalker.Party.v1</c>). All fields are
/// version-stable; new optional fields can be added without bumping <see cref="V"/>
/// because System.Text.Json tolerates unknown properties on the read side.
///
/// Kinds (see <see cref="PartyKind"/>):
///   FATE_ASSIGN  Host says "everyone go to FATE X in territory T; here's your
///                slot index". Also published periodically as a HEARTBEAT-ish
///                refresh so a Follower that joined late or missed the original
///                message converges within one beat.
///   FATE_CLEAR   Host says "FATE done / cancelled, stand by".
///   HEARTBEAT    Host pings without a FATE assignment so Followers know it's
///                still alive (between FATEs, while traveling, etc.).
///   ROLE_CLAIM   "I am Host" — used to break ties in Auto mode.
/// </summary>
public enum PartyKind
{
    FATE_ASSIGN = 1,
    FATE_CLEAR = 2,
    HEARTBEAT = 3,
    ROLE_CLAIM = 4,
}

public sealed class PartyEnvelope
{
    [JsonPropertyName("v")]         public int V { get; set; } = 1;
    [JsonPropertyName("kind")]      public PartyKind Kind { get; set; }
    /// <summary>Sender's content-id. Used for tie-break and replay-drop.</summary>
    [JsonPropertyName("fromCid")]   public ulong FromCid { get; set; }
    /// <summary>UTC unix-ms when the message was sent.</summary>
    [JsonPropertyName("ts")]        public long  Ts { get; set; }
    /// <summary>Target FATE row id (0 for HEARTBEAT / ROLE_CLAIM / CLEAR).</summary>
    [JsonPropertyName("fateId")]    public uint  FateId { get; set; }
    /// <summary>Target territory id (so Followers in a different zone ignore the message).</summary>
    [JsonPropertyName("territory")] public uint  Territory { get; set; }
    /// <summary>Stable index assignments (cid → 0-based slot). Host fills this from
    /// <see cref="PartyFormation.AssignSlots"/>; Followers look up their own cid.</summary>
    [JsonPropertyName("assigns")]   public List<Assignment> Assigns { get; set; } = new();

    public sealed class Assignment
    {
        [JsonPropertyName("cid")]   public ulong Cid { get; set; }
        [JsonPropertyName("idx")]   public int   Idx { get; set; }
    }

    // STJ options used for both serialize and deserialize. JsonStringEnumConverter
    // lets the wire form use "FATE_ASSIGN" / "HEARTBEAT" instead of magic ints,
    // which makes a log dump readable when debugging.
    private static readonly JsonSerializerOptions _opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() },
    };

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, _opts);

    public static PartyEnvelope? TryDeserialize(byte[] data)
    {
        try { return JsonSerializer.Deserialize<PartyEnvelope>(data, _opts); }
        catch { return null; }
    }

    public static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
