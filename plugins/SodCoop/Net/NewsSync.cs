using System;
using System.IO;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// Newspapers and clean-up, both host-owned.
/// <list type="bullet">
/// <item>Newspaper: the host's <c>NewspaperController.GenerateNewNewspaper</c> picks the articles (murders, random
///   choices); clients skip their own and get the host's <c>NewspaperState</c> (the game's save form, JSON), then
///   refresh the texts. Sent after each generation and to a client that joins.</item>
/// <item>Clean-up: <c>CleanupController.TrashUpdate</c> deletes trash and expired items. On a client it would delete
///   items the host still has; the host's deletes arrive through ItemSync instead.</item>
/// </list>
/// </summary>
public static class NewsSync
{
    public const byte MsgTypeNewspaper = 51;
    const ushort Magic = 0x4D53; // same header as Protocol.Write

    [ThreadStatic] static bool _applying;
    public static int Sent { get; private set; }
    public static int Applied { get; private set; }

    static NetSession Session => NetSession.Current;
    static bool IsHost => Session?.Role == NetSession.SessionRole.Host;
    static bool IsClient => Session?.Role == NetSession.SessionRole.Client;

    static byte[] Write(string json)
    {
        using var ms = new MemoryStream(256);
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic); w.Write(MsgTypeNewspaper);
        var b = Encoding.UTF8.GetBytes(json ?? "");
        w.Write(b.Length); w.Write(b);
        w.Flush();
        return ms.ToArray();
    }

    static string Read(byte[] d)
    {
        using var ms = new MemoryStream(d, 3, d.Length - 3);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        return Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()));
    }

    static string Current()
    {
        var state = NewspaperController.Instance?.currentState;
        return state != null ? JsonUtility.ToJson(state) : null;
    }

    /// <summary>Host: the current paper for one (joining) client.</summary>
    public static void SendTo(Action<byte[]> send)
    {
        if (!IsHost) return;
        var json = Current();
        if (json == null) return;
        send(Write(json));
        Sent++;
    }

    /// <summary>Client: the host's paper.</summary>
    public static void OnPacket(byte[] data)
    {
        if (!IsClient || !World.Ready) return;
        var nc = NewspaperController.Instance;
        if (nc == null) return;
        _applying = true;
        try
        {
            var json = Read(data);
            // FromJsonOverwrite fails through the interop (Il2CppSystem.Object.GetType); build a new state object.
            var state = JsonUtility.FromJson(json, Il2CppType.Of<NewspaperController.NewspaperState>())?.TryCast<NewspaperController.NewspaperState>();
            if (state == null) { Plugin.Event("NEWS paper from host: JsonUtility returned nothing"); return; }
            nc.currentState = state;
            nc.UpdateText(true);
            Applied++;
            Plugin.Event($"NEWS paper from host applied: {state.mainArticle} (murder {state.murderID})");
        }
        catch (Exception e) { Plugin.Event($"NEWS paper from host failed: {e.Message}"); }
        finally { _applying = false; }
    }

    [HarmonyPatch(typeof(NewspaperController), nameof(NewspaperController.GenerateNewNewspaper))]
    static class Generate
    {
        static bool Prefix() => !IsClient || _applying || !World.Ready; // a client takes the host's paper

        static void Postfix()
        {
            if (!IsHost || !World.Ready) return;
            var json = Current();
            if (json == null) return;
            Session.SendWorld(Write(json));
            Sent++;
        }
    }

    [HarmonyPatch(typeof(CleanupController), nameof(CleanupController.TrashUpdate))]
    static class NoClientCleanup
    {
        static bool Prefix() => !IsClient || !World.Ready;
    }
}
