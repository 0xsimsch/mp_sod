using System;
using System.Collections.Generic;
using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// B2 fix, part 2: the lifecycle of existing interactables. <c>MoveInteractable</c>, <c>SetInInventory</c>,
/// <c>SetAsNotInventory</c> and <c>Delete</c> are reported and replayed by id. Ids are only meaningful for
/// objects in <c>CityData.savableInteractableDictionary</c> (generation-time objects and synced spawns);
/// others count as misses on the receiver.
/// A player holder is sent as a player id, because each machine represents a player by a different human
/// (the local <c>Player</c> here, a borrowed citizen there).
/// </summary>
public static class ItemSync
{
    [ThreadStatic] static bool _applying;

    sealed class Scope : IDisposable
    {
        readonly bool _prev;
        public Scope() { _prev = _applying; _applying = true; }
        public void Dispose() => _applying = _prev;
    }

    /// <summary>Don't report item events while this is alive (e.g. applying a network spawn).</summary>
    public static IDisposable Suppress() => new Scope();

    public static int Sent { get; private set; }
    public static int Applied { get; private set; }
    public static int Missed { get; private set; }

    /// <summary>
    /// A client still simulates its own citizens, so it must only report what its own player did:
    /// items it holds/held, or changes within <see cref="ClientReach"/> m of it. (Goes away once clients
    /// run a thin world without AI.)
    /// </summary>
    const float ClientReach = 3f;

    public static bool ClientMayReport(Vector3 where, Actor involved = null, bool heldByLocal = false)
    {
        var s = NetSession.Current;
        if (s == null || s.Role != NetSession.SessionRole.Client) return true;
        var me = Player.Instance;
        if (me == null) return false;
        if (heldByLocal || (involved != null && involved.Pointer == me.Pointer)) return true;
        return Vector3.Distance(where, me.transform.position) <= ClientReach;
    }

    static void Report(Interactable it, ItemEventKind kind, Actor holder = null, bool wasHeldByLocal = false,
                       Vector3? pos = null, Vector3? euler = null, int? switchType = null, bool switchValue = false,
                       bool byLocalPlayer = false)
    {
        var s = NetSession.Current;
        if (s == null || _applying || it == null || PlayerRegistry.Local == null || !World.Ready) return;
        if (!byLocalPlayer && !ClientMayReport(it.wPos, holder, wasHeldByLocal)) return;
        var p = pos ?? it.wPos;
        var e = euler ?? it.wEuler;
        var msg = new ItemMsg
        {
            ByPlayer = PlayerRegistry.Local.Id.Value,
            Kind = kind,
            Id = it.id,
            Position = new Vec3(p.x, p.y, p.z),
            Euler = new Vec3(e.x, e.y, e.z),
            Holder = -1,
        };
        if (switchType != null)
        {
            msg.Holder = switchType.Value;
            msg.HolderIsPlayer = switchValue;
            s.SendItem(msg);
            Sent++;
            Plugin.Event($"ITEM switch #{it.id} {it.name} {(InteractablePreset.Switch)switchType.Value}={switchValue} sent");
            return;
        }
        if (holder != null)
        {
            var owner = PlayerRegistry.OwnerOf(holder);
            if (owner != null) { msg.Holder = owner.Id.Value; msg.HolderIsPlayer = true; }
            else if (holder.TryCast<Human>() is { } h) msg.Holder = h.humanID;
        }
        s.SendItem(msg);
        Sent++;
    }

    public static void Apply(ItemMsg m)
    {
        if (!World.Ready) return;
        // Not only saved objects: fixtures such as light switches aren't in savableInteractableDictionary.
        var it = WorldSync.FindInteractable(m.Id);
        if (it == null)
        {
            Missed++;
            if (m.Kind == ItemEventKind.Switch) Plugin.Event($"ITEM switch #{m.Id} from P{m.ByPlayer}: no such object here");
            return;
        }
        _applying = true;
        try
        {
            var pos = new Vector3(m.Position.X, m.Position.Y, m.Position.Z);
            var euler = new Vector3(m.Euler.X, m.Euler.Y, m.Euler.Z);
            switch (m.Kind)
            {
                case ItemEventKind.Move: it.MoveInteractable(pos, euler, true); break;
                case ItemEventKind.IntoInventory:
                    var holder = m.HolderIsPlayer
                        ? (PlayerRegistry.TryGet(new PlayerId((byte)m.Holder), out var p) ? p.Body : null)
                        : (CityData.Instance.citizenDictionary.TryGetValue(m.Holder, out var hh) ? hh : null);
                    if (holder != null) it.SetInInventory(holder);
                    break;
                case ItemEventKind.OutOfInventory:
                    var node = Toolbox.Instance.FindClosestValidNodeToWorldPosition(pos, false, true, false, default, false, 0, false, 200);
                    it.SetAsNotInventory(node);
                    it.MoveInteractable(pos, euler, true);
                    break;
                case ItemEventKind.Delete: it.Delete(); break;
                case ItemEventKind.Throw:
                {
                    var ctrl = it.controller;
                    if (ctrl == null) { it.MoveInteractable(pos, euler, true); break; } // not spawned here: the resting place follows
                    var thrower = PlayerRegistry.TryGet(new PlayerId(m.ByPlayer), out var tp) ? tp.Body : null;
                    tp?.RemoteBody?.OnThrew(m.Id);
                    ctrl.SetPhysics(true, thrower);
                    ctrl.transform.SetPositionAndRotation(pos, Quaternion.Euler(euler));
                    if (ctrl.rb != null)
                    {
                        ctrl.rb.position = pos;
                        ctrl.rb.rotation = Quaternion.Euler(euler);
                        ctrl.rb.velocity = new Vector3(m.Velocity.X, m.Velocity.Y, m.Velocity.Z);
                        ctrl.rb.angularVelocity = new Vector3(m.Spin.X, m.Spin.Y, m.Spin.Z);
                    }
                    break;
                }
                case ItemEventKind.Switch:
                    if (SwitchValue(it, (InteractablePreset.Switch)m.Holder) == m.HolderIsPlayer) break; // already so (the host's confirmation of our own flip)
                    var who = PlayerRegistry.TryGet(new PlayerId(m.ByPlayer), out var by) ? by.Body : null;
                    it.SetSwtichByType((InteractablePreset.Switch)m.Holder, m.HolderIsPlayer, who, true, false);
                    Plugin.Event($"ITEM switch #{m.Id} {it.name} {(InteractablePreset.Switch)m.Holder}={m.HolderIsPlayer} from P{m.ByPlayer} applied");
                    break;
            }
            Applied++;
        }
        catch (Exception e) { Plugin.Event($"ITEM {m.Kind} #{m.Id} failed: {e.Message}"); }
        finally { _applying = false; }
    }

    static bool SwitchValue(Interactable it, InteractablePreset.Switch which) => which switch
    {
        InteractablePreset.Switch.switchState => it.sw0,
        InteractablePreset.Switch.custom1 => it.sw1,
        InteractablePreset.Switch.custom2 => it.sw2,
        InteractablePreset.Switch.custom3 => it.sw3,
        InteractablePreset.Switch.lockState => it.locked,
        _ => !it.sw0,
    };

    /// <summary>
    /// Host, after applying a client's switch flip: the switch's state as it is now on the host, for everyone (the
    /// client that flipped it included). That confirms the flip, and corrects a client whose flip didn't take here.
    /// </summary>
    public static bool HostSwitchState(ItemMsg m, out ItemMsg state)
    {
        state = m;
        var it = WorldSync.FindInteractable(m.Id);
        if (it == null) return false;
        bool now = SwitchValue(it, (InteractablePreset.Switch)m.Holder);
        state.HolderIsPlayer = now;
        if (now != m.HolderIsPlayer) Plugin.Event($"ITEM switch #{m.Id} {it.name}: P{m.ByPlayer} set it to {m.HolderIsPlayer}, the host has {now}; resyncing");
        return true;
    }

    // --- physics: objects a player carried, dropped, threw or pushed. Their resting place is sent once they settle.

    /// <summary>Interactables our player touched (carried, threw, knocked): only those are reported when they come to rest.</summary>
    static readonly HashSet<int> _touched = new();

    static bool IsLocal(Actor a) => a != null && Player.Instance != null && a.Pointer == Player.Instance.Pointer;

    [HarmonyPatch(typeof(InteractableController), nameof(InteractableController.MovablePickUpThis))]
    static class Carry { static void Postfix(InteractableController __instance) { if (__instance.interactable != null) _touched.Add(__instance.interactable.id); } }

    /// <summary>
    /// Our player let go of a carried object (thrown or dropped): send its start and velocity so it flies the same arc
    /// elsewhere. The throw force goes through physics, so the velocity is read on the next frame (<see cref="Tick"/>).
    /// </summary>
    [HarmonyPatch(typeof(InteractableController), nameof(InteractableController.DropThis))]
    static class Dropped
    {
        static void Postfix(InteractableController __instance, bool throwThis)
        {
            if (__instance.interactable == null || _applying) return;
            _touched.Add(__instance.interactable.id);
            _toThrow.Add((__instance, UnityEngine.Time.frameCount, throwThis));
        }
    }

    static readonly List<(InteractableController ctrl, int frame, bool thrown)> _toThrow = new();

    /// <summary>Per frame: send throws captured a frame ago.</summary>
    public static void Tick()
    {
        _localActionDepth = 0; // an action that threw never ran its postfix: never let that leak into later frames
        if (_toThrow.Count == 0) return;
        for (int i = _toThrow.Count - 1; i >= 0; i--)
        {
            var (ctrl, frame, thrown) = _toThrow[i];
            if (UnityEngine.Time.frameCount <= frame) continue;
            _toThrow.RemoveAt(i);
            if (ctrl == null || ctrl.WasCollected || ctrl.interactable == null) continue;
            var t = ctrl.transform;
            var rb = ctrl.rb;
            ReportThrow(ctrl.interactable, t.position, t.eulerAngles, rb != null ? rb.velocity : Vector3.zero, rb != null ? rb.angularVelocity : Vector3.zero, thrown);
        }
    }

    static void ReportThrow(Interactable it, Vector3 pos, Vector3 euler, Vector3 vel, Vector3 spin, bool thrown)
    {
        var s = NetSession.Current;
        if (s == null || PlayerRegistry.Local == null || !World.Ready) return;
        s.SendItem(new ItemMsg
        {
            ByPlayer = PlayerRegistry.Local.Id.Value, Kind = ItemEventKind.Throw, Id = it.id, Holder = -1,
            Position = new Vec3(pos.x, pos.y, pos.z), Euler = new Vec3(euler.x, euler.y, euler.z),
            Velocity = new Vec3(vel.x, vel.y, vel.z), Spin = new Vec3(spin.x, spin.y, spin.z),
        });
        Sent++;
        Plugin.Event($"ITEM {(thrown ? "threw" : "dropped")} #{it.id} at {vel.magnitude:F1} m/s");
    }

    [HarmonyPatch(typeof(InteractableController), nameof(InteractableController.SetPhysics))]
    static class Physics
    {
        static void Postfix(InteractableController __instance, bool val, Actor setThrownBy)
        {
            var it = __instance.interactable;
            if (it == null || _applying) return;
            if (val) { if (IsLocal(setThrownBy)) _touched.Add(it.id); return; } // knocked/thrown by us
            if (!_touched.Remove(it.id)) return; // came to rest: report where, if we moved it
            var t = __instance.transform;
            Report(it, ItemEventKind.Move, null, true, t.position, t.eulerAngles);
        }
    }

    // --- on/off states flipped by our player (doors have DoorSync).

    /// <summary>
    /// Our player is performing an ActionController action (light, TV, hob, music, cabinet, breaker…). The game doesn't
    /// pass the player on to the switch setter (interactor null), so without this a client's own switches were never
    /// sent; the host's only reached clients through WorldSync's "not a player" path.
    /// </summary>
    [ThreadStatic] static int _localActionDepth;

    [HarmonyPatch]
    static class LocalActions
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var m in typeof(ActionController).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            {
                var ps = m.GetParameters();
                // Only switch-type actions. Hooking all ~200 actions (with a finalizer) crashed the game natively at
                // startup (0.9.1, bisected); finalizers are avoided entirely, see Tick for the reset.
                bool switchy = m.Name.StartsWith("TurnOn") || m.Name.StartsWith("TurnOff") || m.Name.StartsWith("Open") || m.Name.StartsWith("Close")
                               || m.Name.StartsWith("Lock") || m.Name.StartsWith("Unlock") || m.Name.StartsWith("Switch") || m.Name.Contains("Toggle");
                if (switchy && ps.Length == 3 && ps[0].ParameterType == typeof(Interactable) && ps[1].ParameterType == typeof(NewNode) && ps[2].ParameterType == typeof(Actor))
                    yield return m;
            }
        }

        static void Prefix(Actor who, out bool __state) { __state = IsLocal(who); if (__state) _localActionDepth++; }
        static void Postfix(bool __state) { if (__state && _localActionDepth > 0) _localActionDepth--; }
    }

    /// <summary>
    /// The player's own interaction (key press on an object). Some objects flip their switch from here without an
    /// ActionController action, again with no interactor.
    /// </summary>
    [HarmonyPatch(typeof(Interactable), nameof(Interactable.OnInteraction), typeof(InteractablePreset.InteractionAction), typeof(Actor), typeof(bool), typeof(float))]
    static class LocalInteraction
    {
        static void Prefix(Actor who, out bool __state) { __state = IsLocal(who); if (__state) _localActionDepth++; }
        static void Postfix(bool __state) { if (__state && _localActionDepth > 0) _localActionDepth--; }
    }

    static void ReportSwitch(Interactable it, InteractablePreset.Switch which, bool val, Actor interactor)
    {
        if (_applying || it == null) return;
        if (!IsLocal(interactor) && !(interactor == null && _localActionDepth > 0)) return;
        if (it.objectRef != null && it.objectRef.TryCast<NewDoor>() != null) return;
        // Ours for certain, so no reach check: a light switch's stored position can be metres from the player's feet,
        // and one switch also flips the room's other lights (seen: a client's switches never reached the host).
        Report(it, ItemEventKind.Switch, interactor, false, null, null, (int)which, val, byLocalPlayer: true);
    }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.SetSwitchState))]
    static class SwitchMain { static void Postfix(Interactable __instance, bool val, Actor interactor) => ReportSwitch(__instance, InteractablePreset.Switch.switchState, val, interactor); }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.SetCustomState1))]
    static class Custom1 { static void Postfix(Interactable __instance, bool val, Actor interactor) => ReportSwitch(__instance, InteractablePreset.Switch.custom1, val, interactor); }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.SetCustomState2))]
    static class Custom2 { static void Postfix(Interactable __instance, bool val, Actor interactor) => ReportSwitch(__instance, InteractablePreset.Switch.custom2, val, interactor); }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.SetCustomState3))]
    static class Custom3 { static void Postfix(Interactable __instance, bool val, Actor interactor) => ReportSwitch(__instance, InteractablePreset.Switch.custom3, val, interactor); }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.SetLockedState))]
    static class Locked { static void Postfix(Interactable __instance, bool val, Actor interactor) => ReportSwitch(__instance, InteractablePreset.Switch.lockState, val, interactor); }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.MoveInteractable))]
    static class Move { static void Postfix(Interactable __instance) => Report(__instance, ItemEventKind.Move); }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.SetInInventory))]
    static class Into { static void Postfix(Interactable __instance, Human newActor) => Report(__instance, ItemEventKind.IntoInventory, newActor); }

    static bool HeldByLocal(Interactable it) => it.inInventory != null && Player.Instance != null && it.inInventory.Pointer == Player.Instance.Pointer;

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.SetAsNotInventory))]
    static class OutOf
    {
        static void Prefix(Interactable __instance, out bool __state) => __state = HeldByLocal(__instance);
        static void Postfix(Interactable __instance, bool __state) => Report(__instance, ItemEventKind.OutOfInventory, null, __state);
    }

    /// <summary>
    /// Every "take" action of our player (keys, money, lockpicks, sync disks, consumables, prints, gadgets…): several
    /// remove the object without going through Delete/SetInInventory (SafeDelete) or keep it in the world, so the
    /// other machines kept it and it could be taken twice. Afterwards, if the object is gone here, say so.
    /// </summary>
    [HarmonyPatch]
    static class TakeActions
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var m in typeof(ActionController).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (!(m.Name.StartsWith("Take") || m.Name == nameof(ActionController.PickUp))) continue;
                var ps = m.GetParameters();
                if (ps.Length == 3 && ps[0].ParameterType == typeof(Interactable) && ps[2].ParameterType == typeof(Actor)) yield return m;
            }
        }

        static void Prefix(Interactable what, out bool __state) => __state = what != null && !what.rem;

        static void Postfix(Interactable what, Actor who, bool __state, System.Reflection.MethodBase __originalMethod)
        {
            if (!__state || what == null || !IsLocal(who) || _applying) return;
            if (what.rem && what.inInventory == null)
            {
                Report(what, ItemEventKind.Delete, null, true);
                Plugin.Event($"ITEM took #{what.id} ({__originalMethod.Name}): removed for everyone");
            }
        }
    }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.SafeDelete))]
    static class SafeDel { static void Prefix(Interactable __instance) => Report(__instance, ItemEventKind.Delete, null, HeldByLocal(__instance)); }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.Delete))]
    static class Del { static void Prefix(Interactable __instance) => Report(__instance, ItemEventKind.Delete, null, HeldByLocal(__instance)); }
}
