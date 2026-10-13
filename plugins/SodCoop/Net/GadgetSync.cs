using System;
using System.IO;
using System.Text;
using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// Things a player does to citizens with tools: citizens are the host's, so a client's effect has to happen there
/// (seen: a citizen cuffed or stunned on a client kept walking on the host). The result then comes back to clients
/// through the existing citizen sync (KO, arms state "cuffed", poses).
/// <list type="bullet">
/// <item>Handcuffs / restraining: <c>NewAIController.SetRestrained</c> (called by <c>Player.OnHandcuff</c>) next to our
///   player is sent to the host, which restrains its citizen.</item>
/// <item>Flash bombs, incapacitators, grenades: <c>Toolbox.ExplodeGrenade</c> on a client is sent to the host, which
///   explodes its copy of the same (spawn-synced) gadget, so its citizens get blinded/KO'd. The client keeps its own
///   local explosion for the visuals and its own player's effects; the host's citizen states overwrite the client's.</item>
/// </list>
/// Door wedges are WorldSync's; the codebreaker's result (unlocking) arrives through the door/lock syncs.
/// </summary>
public static class GadgetSync
{
    public const byte MsgTypeGadget = 49;
    const ushort Magic = 0x4D53;
    const float Reach = 4f;

    enum Kind : byte { Restrain = 1, Explode = 2 }

    [ThreadStatic] static bool _applying;
    public static int Sent { get; private set; }
    public static int Applied { get; private set; }

    static NetSession Session => NetSession.Current;
    static bool IsHost => Session?.Role == NetSession.SessionRole.Host;
    static bool IsClient => Session?.Role == NetSession.SessionRole.Client;

    static byte[] Write(Kind kind, Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream(32);
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic); w.Write(MsgTypeGadget); w.Write((byte)kind);
        body(w);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Host: a client's gadget effect. <paramref name="from"/> is the sending player.</summary>
    public static void OnPacket(PlayerId from, byte[] data)
    {
        if (!IsHost || !World.Ready || data.Length < 4) return;
        using var ms = new MemoryStream(data, 3, data.Length - 3);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        var kind = (Kind)r.ReadByte();
        _applying = true;
        try
        {
            switch (kind)
            {
                case Kind.Restrain:
                {
                    int humanId = r.ReadInt32(); bool val = r.ReadBoolean(); float duration = r.ReadSingle();
                    var dict = CityData.Instance?.citizenDictionary;
                    if (dict == null || !dict.TryGetValue(humanId, out var h) || h?.ai == null || h.isDead) return;
                    if (h.ai.restrained == val) return;
                    h.ai.SetRestrained(val, duration);
                    Applied++;
                    Plugin.Event($"GADGET {from} {(val ? "cuffed" : "released")} citizen {humanId}");
                    break;
                }
                case Kind.Explode:
                {
                    int id = r.ReadInt32();
                    var it = WorldSync.FindInteractable(id);
                    if (it == null || it.rem) return; // already went off here
                    Toolbox.Instance?.ExplodeGrenade(it);
                    Applied++;
                    Plugin.Event($"GADGET {from}'s gadget #{id} went off");
                    break;
                }
            }
        }
        catch (Exception e) { Plugin.Event($"GADGET {kind} from {from} failed: {e.Message}"); }
        finally { _applying = false; }
    }

    static bool NearLocalPlayer(Vector3 pos) =>
        Player.Instance != null && Vector3.Distance(Player.Instance.transform.position, pos) < Reach;

    [HarmonyPatch(typeof(NewAIController), nameof(NewAIController.SetRestrained))]
    static class Restrain
    {
        static void Postfix(NewAIController __instance, bool val, float duration)
        {
            if (!IsClient || _applying || !World.Ready) return;
            var h = __instance.human;
            if (h == null || Puppet.Doubles.IsBorrowed(h) || !NearLocalPlayer(h.transform.position)) return;
            Session?.SendWorld(Write(Kind.Restrain, w => { w.Write(h.humanID); w.Write(val); w.Write(duration); }));
            Sent++;
            Plugin.Event($"GADGET {(val ? "cuffed" : "released")} citizen {h.humanID}; telling the host");
        }
    }

    [HarmonyPatch(typeof(Toolbox), nameof(Toolbox.ExplodeGrenade))]
    static class Explode
    {
        static void Prefix(Interactable grenade)
        {
            if (!IsClient || _applying || !World.Ready || grenade == null || grenade.rem) return;
            int id = grenade.id;
            Session?.SendWorld(Write(Kind.Explode, w => w.Write(id)));
            Sent++;
            Plugin.Event($"GADGET #{id} went off here; telling the host");
        }
    }
}
