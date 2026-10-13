using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// What host-driven citizens do besides moving (SYNC_PRIORITIES items 8, 9, 13, 14). Their AI is off on the client,
/// so nothing of this happens there on its own:
/// <list type="bullet">
/// <item><b>Events</b> (msg 37, reliable), hooked on the host: attack / throw / block / abort animation triggers with the
///   weapon, speech (<c>SpeechController.Speak</c>, the outermost overload only) and barks (<c>TriggerBark</c>).</item>
/// <item><b>States</b> (msg 38, reliable, on change, 5 Hz poll): reaction state + which player it's aimed at (drives the
///   game's own "spotted"/investigating indicator via <c>NewAIController.TriggerReactionIndicator</c>), alertness, outfit,
///   combat + weapon, and the current action's held props (<c>AIActionPreset.itemRight/itemLeft</c>).</item>
/// </list>
/// Sent only for citizens within <see cref="SyncRadius"/> of the receiving player's body, which matches the pose sync
/// set (see the NetSession snippet). The client applies them only to citizens it is driving from the host.
/// </summary>
public static class CitizenEventSync
{
    public const byte EventMsg = 37;
    public const byte StatesMsg = 38;
    const ushort Magic = 0x4D53; // same header as Protocol: ushort magic, byte type

    public enum Kind : byte { Attack = 1, Throw = 2, Block = 3, AbortAttack = 4, Speak = 5, SpeakDds = 6, Bark = 7 }

    /// <summary>Set by NetSession: send to one player's peer (host side).</summary>
    public static Action<PlayerId, byte[], bool> SendToPlayer;

    public static int EventsSent { get; private set; }
    public static int EventsApplied { get; private set; }
    public static int StatesSent { get; private set; }
    public static int StatesApplied { get; private set; }

    static bool IsHost => NetSession.Current?.Role == NetSession.SessionRole.Host;

    // ------------------------------------------------------------------ interest radius (also for item 18)

    const float IndoorRadius = 40f, OutdoorRadius = 70f, Hysteresis = 10f;

    /// <summary>
    /// Sync radius around a player's body: wider outdoors (long sight lines made the 40 m edge visible as pop-in), plus
    /// <see cref="Hysteresis"/> for a citizen that is already synced so it doesn't flicker in and out at the edge.
    /// </summary>
    public static float SyncRadius(Human playerBody, bool alreadySynced)
    {
        bool outdoors = playerBody == null || playerBody.currentBuilding == null;
        return (outdoors ? OutdoorRadius : IndoorRadius) + (alreadySynced ? Hysteresis : 0f);
    }

    // ------------------------------------------------------------------ host: events

    static readonly List<(Human body, PlayerId id)> _receivers = new();

    /// <summary>Players (other than us) whose sync set contains <paramref name="c"/>.</summary>
    static List<(Human body, PlayerId id)> ReceiversOf(Human c)
    {
        _receivers.Clear();
        if (c == null || Puppet.Doubles.IsBorrowed(c)) return _receivers;
        var pos = c.transform.position;
        foreach (var p in PlayerRegistry.All)
        {
            if (p.IsLocal) continue;
            var body = p.RemoteBody?.Human;
            if (body == null) continue;
            float r = SyncRadius(body, true);
            if ((body.transform.position - pos).sqrMagnitude <= r * r) _receivers.Add((body, p.Id));
        }
        return _receivers;
    }

    static void SendEvent(Human c, Kind kind, Action<BinaryWriter> body)
    {
        if (!IsHost || SendToPlayer == null || c == null) return;
        var to = ReceiversOf(c);
        if (to.Count == 0) return;
        var data = Write(EventMsg, w => { w.Write(c.humanID); w.Write((byte)kind); body?.Invoke(w); });
        foreach (var (_, id) in to) SendToPlayer(id, data, true);
        EventsSent++;
    }

    static string WeaponOf(Human c) => c?.ai?.currentWeaponPreset != null ? c.ai.currentWeaponPreset.name : "";

    [ThreadStatic] static bool _applying;

    [HarmonyPatch(typeof(CitizenAnimationController), nameof(CitizenAnimationController.AttackTrigger))]
    static class Attack { static void Postfix(CitizenAnimationController __instance) { if (!_applying) SendEvent(__instance.cit, Kind.Attack, w => Str(w, WeaponOf(__instance.cit))); } }

    [HarmonyPatch(typeof(CitizenAnimationController), nameof(CitizenAnimationController.ThrowTrigger))]
    static class Throw { static void Postfix(CitizenAnimationController __instance) { if (!_applying) SendEvent(__instance.cit, Kind.Throw, w => Str(w, WeaponOf(__instance.cit))); } }

    [HarmonyPatch(typeof(CitizenAnimationController), nameof(CitizenAnimationController.AbortAttackTrigger))]
    static class Abort { static void Postfix(CitizenAnimationController __instance) { if (!_applying) SendEvent(__instance.cit, Kind.AbortAttack, null); } }

    [HarmonyPatch(typeof(CitizenAnimationController), nameof(CitizenAnimationController.BlockTrigger))]
    static class Block
    {
        static void Postfix(CitizenAnimationController __instance, float blockDelay, bool perfect)
        {
            if (!_applying) SendEvent(__instance.cit, Kind.Block, w => { w.Write(blockDelay); w.Write(perfect); });
        }
    }

    // Speech: only the outermost hooked call is sent (a bark or a DDS line may speak through the other overloads).
    [ThreadStatic] static int _speechDepth;

    static Human SpeakerOf(SpeechController sc) => sc?.actor != null ? sc.actor.TryCast<Human>() : null;

    [HarmonyPatch(typeof(SpeechController), nameof(SpeechController.TriggerBark))]
    static class Bark
    {
        static void Prefix() => _speechDepth++;
        static void Postfix(SpeechController __instance, SpeechController.Bark newBark)
        {
            if (--_speechDepth == 0 && !_applying) SendEvent(SpeakerOf(__instance), Kind.Bark, w => w.Write((int)newBark));
        }
    }

    [HarmonyPatch]
    static class SpeakEntry
    {
        static MethodBase TargetMethod() => typeof(SpeechController).GetMethods()
            .First(m => m.Name == nameof(SpeechController.Speak) && m.GetParameters().Length > 2 && m.GetParameters()[1].Name == "speechEntryRef");

        static void Prefix() => _speechDepth++;
        static void Postfix(SpeechController __instance, string dictionary, string speechEntryRef, bool useParsing, bool shout, bool interupt, Human speakingAbout)
        {
            if (--_speechDepth != 0 || _applying) return;
            SendEvent(SpeakerOf(__instance), Kind.Speak, w =>
            {
                Str(w, dictionary); Str(w, speechEntryRef); w.Write(useParsing); w.Write(shout); w.Write(interupt);
                w.Write(speakingAbout != null ? speakingAbout.humanID : -1);
            });
        }
    }

    [HarmonyPatch]
    static class SpeakDds
    {
        static MethodBase TargetMethod() => typeof(SpeechController).GetMethods()
            .First(m => m.Name == nameof(SpeechController.Speak) && m.GetParameters().Length > 0 && m.GetParameters()[0].Name == "ddsMessage");

        static void Prefix() => _speechDepth++;
        static void Postfix(SpeechController __instance, string ddsMessage, bool shout, bool interupt, Human speakAbout)
        {
            if (--_speechDepth != 0 || _applying) return;
            SendEvent(SpeakerOf(__instance), Kind.SpeakDds, w =>
            {
                Str(w, ddsMessage); w.Write(shout); w.Write(interupt); w.Write(speakAbout != null ? speakAbout.humanID : -1);
            });
        }
    }

    // ------------------------------------------------------------------ host: states (poll)

    struct State
    {
        public byte Reaction;      // NewAIController.ReactionState
        public sbyte TargetPlayer; // player the reaction/persuit is aimed at, -1 none
        public byte Alertness;     // 0..255 of ai.alertness clamped to 0..1
        public byte Outfit;        // ClothesPreset.OutfitCategory
        public bool Combat;
        public string Weapon;      // MurderWeaponPreset name or ""
        public string Action;      // AIActionPreset name or ""
        public bool Right, Left;   // the action's props are spawned in that hand on the host

        public bool SameAs(in State o) => Reaction == o.Reaction && TargetPlayer == o.TargetPlayer && Math.Abs(Alertness - o.Alertness) < 26 &&
                                          Outfit == o.Outfit && Combat == o.Combat && Weapon == o.Weapon && Action == o.Action && Right == o.Right && Left == o.Left;
    }

    const float StateInterval = 0.2f;
    static float _nextStates;
    static readonly Dictionary<(PlayerId, int), State> _lastSent = new();

    static State Capture(Human c)
    {
        var ai = c.ai;
        var s = new State { TargetPlayer = -1, Weapon = "", Action = "" };
        if (ai != null)
        {
            s.Reaction = (byte)ai.reactionState;
            s.Alertness = (byte)Mathf.RoundToInt(Mathf.Clamp01(ai.alertness) * 255f);
            var target = ai.persuitTarget;
            var owner = target != null ? PlayerRegistry.OwnerOf(target) : null;
            if (owner != null) s.TargetPlayer = (sbyte)owner.Id.Value;
            s.Combat = ai.attackActive || (ai.persuit && ai.currentWeaponPreset != null);
            s.Weapon = s.Combat ? WeaponOf(c) : "";
            var preset = ai.currentAction != null ? ai.currentAction.preset : null;
            s.Right = ai.spawnedRightItem != null;
            s.Left = ai.spawnedLeftItem != null;
            if (preset != null && (s.Right || s.Left)) s.Action = preset.name;
        }
        if (c.outfitController != null) s.Outfit = (byte)c.outfitController.currentOutfit;
        return s;
    }

    /// <summary>Host, per frame: send changed citizen states to each player whose sync set holds them.</summary>
    public static void HostTick(float now)
    {
        if (!IsHost || SendToPlayer == null || now < _nextStates) return;
        _nextStates = now + StateInterval;
        var citizens = CityData.Instance?.citizenDirectory;
        if (citizens == null) return;
        var batch = new List<(int id, State s)>();
        foreach (var p in PlayerRegistry.All)
        {
            if (p.IsLocal) continue;
            var body = p.RemoteBody?.Human;
            if (body == null) continue;
            var center = body.transform.position;
            batch.Clear();
            for (int i = 0; i < citizens.Count; i++)
            {
                var c = citizens[i];
                if (c == null || c.isDead || Puppet.Doubles.IsBorrowed(c)) continue;
                var key = (p.Id, c.humanID);
                bool known = _lastSent.TryGetValue(key, out var last);
                float r = SyncRadius(body, known);
                if ((c.transform.position - center).sqrMagnitude > r * r) { if (known) _lastSent.Remove(key); continue; }
                State s;
                try { s = Capture(c); } catch { continue; }
                if (known && last.SameAs(s)) continue;
                _lastSent[key] = s;
                batch.Add((c.humanID, s));
            }
            for (int start = 0; start < batch.Count; start += 20)
            {
                int n = Math.Min(20, batch.Count - start);
                var data = Write(StatesMsg, w =>
                {
                    w.Write((byte)n);
                    for (int k = start; k < start + n; k++) WriteState(w, batch[k].id, batch[k].s);
                });
                SendToPlayer(p.Id, data, true);
                StatesSent += n;
            }
        }
    }

    /// <summary>A player left: forget what they were sent, so a rejoin gets everything again.</summary>
    public static void Forget(PlayerId id)
    {
        foreach (var k in _lastSent.Keys.Where(k => k.Item1 == id).ToList()) _lastSent.Remove(k);
    }

    // ------------------------------------------------------------------ client

    static readonly Dictionary<int, State> _pending = new();  // latest state per citizen, applied once it's driven
    static readonly Dictionary<int, State> _applied = new();

    public static void OnEvent(byte[] data)
    {
        try
        {
            using var r = Reader(data);
            int id = r.ReadInt32();
            var kind = (Kind)r.ReadByte();
            var c = DrivenCitizen(id);
            if (c == null) return; // only citizens the host is driving here (others run their own AI)
            _applying = true;
            try { ApplyEvent(c, kind, r); }
            finally { _applying = false; }
            EventsApplied++;
        }
        catch (Exception e) { Plugin.Event($"CIT event failed: {e.Message}"); }
    }

    static void ApplyEvent(Citizen c, Kind kind, BinaryReader r)
    {
        var anim = c.animationController;
        switch (kind)
        {
            case Kind.Attack:
            case Kind.Throw:
                string weapon = Str(r);
                if (anim == null) return;
                EnsureCombat(c, true, weapon);
                if (kind == Kind.Attack) anim.AttackTrigger(); else anim.ThrowTrigger();
                break;
            case Kind.Block:
                float delay = r.ReadSingle(); bool perfect = r.ReadBoolean();
                anim?.BlockTrigger(delay, perfect);
                break;
            case Kind.AbortAttack:
                anim?.AbortAttackTrigger();
                break;
            case Kind.Speak:
            {
                string dict = Str(r), entry = Str(r);
                bool parsing = r.ReadBoolean(), shout = r.ReadBoolean(), interupt = r.ReadBoolean();
                var about = HumanById(r.ReadInt32());
                c.speechController?.Speak(dict, entry, parsing, shout, interupt, 0f, false, default, about, false, false, null, null, null, null, null);
                break;
            }
            case Kind.SpeakDds:
            {
                string dds = Str(r);
                bool shout = r.ReadBoolean(), interupt = r.ReadBoolean();
                var about = HumanById(r.ReadInt32());
                c.speechController?.Speak(dds, shout, interupt, about, null, null);
                break;
            }
            case Kind.Bark:
                c.speechController?.TriggerBark((SpeechController.Bark)r.ReadInt32());
                break;
        }
    }

    public static void OnStates(byte[] data)
    {
        try
        {
            using var r = Reader(data);
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                var (id, s) = ReadState(r);
                _pending[id] = s;
            }
            ApplyPending();
        }
        catch (Exception e) { Plugin.Event($"CIT states failed: {e.Message}"); }
    }

    /// <summary>Client, per frame (cheap): apply states whose citizen has become driven since they arrived.</summary>
    public static void ClientTick(float now)
    {
        if (_pending.Count > 0 && Time.frameCount % 10 == 0) ApplyPending();
    }

    static readonly List<int> _done = new();

    static void ApplyPending()
    {
        _done.Clear();
        foreach (var kv in _pending)
        {
            var c = DrivenCitizen(kv.Key);
            if (c == null) continue;
            _applying = true;
            try { ApplyState(c, kv.Value); StatesApplied++; }
            catch (Exception e) { Plugin.Event($"CIT state for {kv.Key} failed: {e.Message}"); }
            finally { _applying = false; }
            _applied[kv.Key] = kv.Value;
            _done.Add(kv.Key);
        }
        foreach (var id in _done) _pending.Remove(id);
    }

    static void ApplyState(Citizen c, State s)
    {
        bool had = _applied.TryGetValue(c.humanID, out var old);
        var ai = c.ai;

        // Outfit (coat outdoors, night clothes in bed, work uniform…)
        if (c.outfitController != null && c.outfitController.currentOutfit != (ClothesPreset.OutfitCategory)s.Outfit)
            c.outfitController.SetCurrentOutfit((ClothesPreset.OutfitCategory)s.Outfit, false, false, true);

        // Reaction toward a player: the game's own indicator over the citizen's head ("!" / "?" / spotted).
        if (ai != null && (!had || old.Reaction != s.Reaction || old.TargetPlayer != s.TargetPlayer))
        {
            ai.reactionState = (NewAIController.ReactionState)s.Reaction;
            Actor target = null;
            if (s.TargetPlayer >= 0 && PlayerRegistry.TryGet(new PlayerId((byte)s.TargetPlayer), out var tp)) target = tp.Body;
            ai.persuitTarget = target;
            ai.persuit = ai.reactionState == NewAIController.ReactionState.persuing && target != null;
            ai.alertness = s.Alertness / 255f;
            ai.TriggerReactionIndicator();
        }
        else if (ai != null) ai.alertness = s.Alertness / 255f;

        // Hands: weapon in combat, else the current action's props.
        if (!had || old.Combat != s.Combat || old.Weapon != s.Weapon || old.Action != s.Action || old.Right != s.Right || old.Left != s.Left)
        {
            if (s.Combat) EnsureCombat(c, true, s.Weapon);
            else
            {
                if (had && old.Combat) EnsureCombat(c, false, "");
                SetProps(c, s.Action, s.Right, s.Left);
            }
        }
    }

    // ------------------------------------------------------------------ hands

    static readonly Dictionary<string, MurderWeaponPreset> _weapons = new();
    static readonly Dictionary<string, AIActionPreset> _actions = new();

    static T Preset<T>(Dictionary<string, T> cache, string name) where T : ScriptableObject
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (cache.TryGetValue(name, out var p)) return p;
        T found = null;
        try { if (Toolbox.Instance != null && Toolbox.Instance.LoadDataFromResources<T>(name, out var o)) found = o; }
        catch (Exception e) { Plugin.Event($"CIT preset {name} lookup failed: {e.Message}"); }
        cache[name] = found;
        return found;
    }

    static void EnsureCombat(Citizen c, bool on, string weaponName)
    {
        var anim = c.animationController;
        anim?.SetInCombat(on);
        if (!on) { ClearHands(c); return; }
        var w = Preset(_weapons, weaponName);
        if (w != null && w.itemRightOverride != null) Spawn(c, true, w.itemRightOverride, w.itemRightLocalPos, w.itemRightLocalEuler);
        if (w != null && w.itemLeftOverride != null) Spawn(c, false, w.itemLeftOverride, w.itemLeftLocalPos, w.itemLeftLocalEuler);
    }

    static void SetProps(Citizen c, string actionName, bool right, bool left)
    {
        ClearHands(c);
        var a = Preset(_actions, actionName);
        if (a == null) return; // consumables / inventory items aren't described by the preset: hands stay empty
        if (right && a.itemRight != null) Spawn(c, true, a.itemRight, a.itemRightLocalPos, a.itemRightLocalEuler);
        if (left && a.itemLeft != null) Spawn(c, false, a.itemLeft, a.itemLeftLocalPos, a.itemLeftLocalEuler);
    }

    static void ClearHands(Citizen c)
    {
        var ai = c.ai;
        if (ai == null) return;
        if (ai.spawnedRightItem != null) ai.DespawnRightItem();
        if (ai.spawnedLeftItem != null) ai.DespawnLeftItem();
    }

    /// <summary>Spawn a prop into a hand and register it with the AI, so its own Despawn*Item cleans up after release.</summary>
    static void Spawn(Citizen c, bool right, GameObject prefab, Vector3 localPos, Vector3 localEuler)
    {
        var anchor = c.outfitController?.GetBodyAnchor(right ? CitizenOutfitController.CharacterAnchor.HandRight : CitizenOutfitController.CharacterAnchor.HandLeft);
        if (anchor == null || c.ai == null) return;
        if (right && c.ai.spawnedRightItem != null) c.ai.DespawnRightItem();
        if (!right && c.ai.spawnedLeftItem != null) c.ai.DespawnLeftItem();
        var go = UnityEngine.Object.Instantiate(prefab, anchor);
        go.transform.localPosition = localPos;
        go.transform.localEulerAngles = localEuler;
        if (right) c.ai.spawnedRightItem = go; else c.ai.spawnedLeftItem = go;
    }

    // ------------------------------------------------------------------ helpers

    static Citizen DrivenCitizen(int id)
    {
        var dict = CityData.Instance?.citizenDictionary;
        if (dict == null || !dict.TryGetValue(id, out var h) || h == null) return null;
        var c = h.TryCast<Citizen>();
        return c != null && Puppet.NpcPuppets.IsDriven(c) ? c : null;
    }

    static Human HumanById(int id)
    {
        if (id < 0) return null;
        var dict = CityData.Instance?.citizenDictionary;
        return dict != null && dict.TryGetValue(id, out var h) ? h : null;
    }

    static void WriteState(BinaryWriter w, int id, State s)
    {
        w.Write(id); w.Write(s.Reaction); w.Write(s.TargetPlayer); w.Write(s.Alertness); w.Write(s.Outfit);
        w.Write((byte)((s.Combat ? 1 : 0) | (s.Right ? 2 : 0) | (s.Left ? 4 : 0)));
        Str(w, s.Weapon); Str(w, s.Action);
    }

    static (int, State) ReadState(BinaryReader r)
    {
        int id = r.ReadInt32();
        var s = new State { Reaction = r.ReadByte(), TargetPlayer = r.ReadSByte(), Alertness = r.ReadByte(), Outfit = r.ReadByte() };
        byte f = r.ReadByte();
        s.Combat = (f & 1) != 0; s.Right = (f & 2) != 0; s.Left = (f & 4) != 0;
        s.Weapon = Str(r); s.Action = Str(r);
        return (id, s);
    }

    static byte[] Write(byte type, Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream(64);
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic);
        w.Write(type);
        body(w);
        w.Flush();
        return ms.ToArray();
    }

    static BinaryReader Reader(byte[] data) => new(new MemoryStream(data, 3, data.Length - 3), Encoding.UTF8);

    static void Str(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s ?? "");
        if (b.Length > 255) Array.Resize(ref b, 255);
        w.Write((byte)b.Length);
        w.Write(b);
    }

    static string Str(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));
}
