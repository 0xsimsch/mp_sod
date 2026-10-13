using System;
using System.IO;
using System.Text;
using SodCoop.Core;

namespace SodCoop.Net;

/// <summary>
/// Framing and dispatch for the progression/status messages (MsgType 39–42): objectives, case resolution,
/// player status, partner notifications. Same header as <see cref="Protocol"/> (magic "SM" + type byte); each
/// message then starts with a sub-kind byte.
/// </summary>
public static class ExtraWire
{
    const ushort Magic = 0x4D53; // must match Protocol.Magic

    public const byte Objective = 39;   // ObjectiveSync
    public const byte CaseResolve = 40; // ObjectiveSync (answers, resolution)
    public const byte Status = 41;      // StatusSync
    public const byte Notice = 42;      // Notify

    public static byte[] Write(byte type, byte kind, Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream(64);
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic);
        w.Write(type);
        w.Write(kind);
        body(w);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Reader positioned after the header; <paramref name="kind"/> is the sub-kind byte.</summary>
    public static BinaryReader Read(byte[] data, out byte kind)
    {
        var r = new BinaryReader(new MemoryStream(data, 3, data.Length - 3), Encoding.UTF8);
        kind = r.ReadByte();
        return r;
    }

    public static void Str(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s ?? "");
        w.Write((ushort)Math.Min(b.Length, ushort.MaxValue));
        w.Write(b, 0, Math.Min(b.Length, ushort.MaxValue));
    }

    public static string Str(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));

    /// <summary>
    /// From NetSession's packet switch: <c>case (MsgType)39: case (MsgType)40: case (MsgType)41: case (MsgType)42:</c>.
    /// <paramref name="fromPlayer"/>: host = the sending client's id (null if unknown), client = null (it's the host).
    /// </summary>
    public static void OnPacket(NetSession s, PeerId from, PlayerId? fromPlayer, byte type, byte[] data)
    {
        try
        {
            switch (type)
            {
                case Objective:
                case CaseResolve: ObjectiveSync.OnPacket(s, from, fromPlayer, type, data); break;
                case Status: StatusSync.OnPacket(s, from, fromPlayer, data); break;
                case Notice: Notify.OnPacket(s, from, fromPlayer, data); break;
            }
        }
        catch (Exception e) { Plugin.Event($"EXTRA message {type} failed: {e.Message}"); }
    }
}
