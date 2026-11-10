using System;
using System.Collections.Generic;
using System.IO;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// Dragging a body (a knocked-out or dead citizen's ragdoll): the game pulls the ragdoll with physics on the dragging
/// player's machine only (<c>InteractionController.currentlyDragging</c>, a <c>RigidbodyDragObject</c>), so the body
/// never moved for anyone else. While a player drags, the dragged citizen's limb pose (the game's own
/// <c>CitizenAnimationController.GetLimbSnapshotWorld</c>) goes out ~10 times a second; receivers hold those limbs
/// kinematic at the pose, and on the final pose put them down and let physics take over again. The host applies a
/// client's drag and relays it.
/// </summary>
public static class DragSync
{
    const byte KindPose = 1; // int humanId, byte n, n × (byte anchor, vec3 pos, quat rot)
    const byte KindEnd = 2;  // same, final pose: release the limbs
    const float Interval = 0.1f;

    public static int Sent { get; private set; }
    public static int Applied { get; private set; }

    static int _dragging = -1;
    static float _next;

    static NetSession S => NetSession.Current;

    /// <summary>Per frame, both roles: stream the body this player is dragging.</summary>
    public static void Tick(float now)
    {
        if (S == null || !World.Ready) return;
        ReleaseSilent(now);
        if (!S.ClientWorldJoined) return; // a client still in its own world: its citizens aren't the host's
        Human h = null;
        try
        {
            var d = InteractionController.Instance != null ? InteractionController.Instance.currentlyDragging : null;
            if (d != null && !d.WasCollected && d.dragIsActive && d.ai != null) h = d.ai.human;
        }
        catch { h = null; }

        if (h != null)
        {
            if (_dragging != h.humanID) { _dragging = h.humanID; Plugin.Event($"DRAG started dragging citizen {h.humanID} \"{h.citizenName}\""); }
            if (now < _next) return;
            _next = now + Interval;
            Send(h, KindPose);
        }
        else if (_dragging >= 0)
        {
            int id = _dragging;
            _dragging = -1;
            if (CityData.Instance != null && CityData.Instance.citizenDictionary.TryGetValue(id, out var last) && last != null) Send(last, KindEnd);
            Plugin.Event($"DRAG let go of citizen {id}");
        }
    }

    static void Send(Human h, byte kind)
    {
        try
        {
            var snap = h.animationController != null ? h.animationController.GetLimbSnapshotWorld() : null;
            if (snap == null || snap.Count == 0) return;
            var limbs = new List<(byte a, Vector3 p, Quaternion r)>();
            for (int i = 0; i < snap.Count; i++)
            {
                var s = snap[i];
                if (s?.anchorConfig == null) continue;
                limbs.Add(((byte)s.anchorConfig.anchor, s.worldPos, s.worldRot));
            }
            if (limbs.Count == 0) return;
            S.SendExtra(ExtraWire.Write(ExtraWire.Drag, kind, w =>
            {
                w.Write(h.humanID);
                w.Write((byte)limbs.Count);
                foreach (var (a, p, r) in limbs)
                {
                    w.Write(a);
                    w.Write(p.x); w.Write(p.y); w.Write(p.z);
                    w.Write(r.x); w.Write(r.y); w.Write(r.z); w.Write(r.w);
                }
            }));
            Sent++;
        }
        catch (Exception e) { Plugin.Event($"DRAG send failed: {e.Message}"); }
    }

    public static void OnPacket(NetSession s, PeerId from, PlayerId? fromPlayer, byte[] data)
    {
        var r = ExtraWire.Read(data, out var kind);
        int id = r.ReadInt32();
        int n = r.ReadByte();
        var limbs = new (CitizenOutfitController.CharacterAnchor a, Vector3 p, Quaternion q)[n];
        for (int i = 0; i < n; i++)
        {
            var a = (CitizenOutfitController.CharacterAnchor)r.ReadByte();
            var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            limbs[i] = (a, p, q);
        }
        if (s.Role == NetSession.SessionRole.Host) s.SendExtraExcept(data, from); // pass a client's drag on
        if (id == _dragging) return; // we're dragging it ourselves: ours wins here
        if (CityData.Instance == null || !CityData.Instance.citizenDictionary.TryGetValue(id, out var h) || h == null) return;
        Apply(h, limbs, kind == KindEnd);
    }

    static readonly HashSet<int> _held = new();

    // The dragging player vanished mid-drag (timed out, left) or its final pose never came (no limb snapshot once
    // the citizen woke): the limbs stayed kinematic at the last pose, the body hanging in the air. Poses come 10 times
    // a second, so a held body without one for SilentRelease seconds is let go here.
    const float SilentRelease = 2f;
    static readonly Dictionary<int, (float at, CitizenOutfitController.CharacterAnchor[] anchors)> _heldLast = new();

    static void ReleaseSilent(float now)
    {
        if (_heldLast.Count == 0) return;
        List<int> silent = null;
        foreach (var kv in _heldLast) if (now - kv.Value.at > SilentRelease) (silent ??= new List<int>()).Add(kv.Key);
        if (silent == null) return;
        foreach (var id in silent) Release(id, "no drag pose for a while");
    }

    /// <summary>Give a held body's limbs back to physics.</summary>
    static void Release(int humanId, string why)
    {
        if (!_heldLast.Remove(humanId, out var last)) { _held.Remove(humanId); return; }
        _held.Remove(humanId);
        try
        {
            if (!World.Ready || CityData.Instance == null || !CityData.Instance.citizenDictionary.TryGetValue(humanId, out var h) || h == null) return;
            var oc = h.outfitController;
            if (oc == null) return;
            foreach (var a in last.anchors)
            {
                var t = oc.GetBodyAnchor(a);
                var rb = t != null ? t.GetComponent<Rigidbody>() : null;
                if (rb != null) rb.isKinematic = false;
            }
            Plugin.Event($"DRAG citizen {humanId} released ({why})");
        }
        catch (Exception e) { Plugin.Event($"DRAG release failed: {e.Message}"); }
    }

    static void Apply(Human h, (CitizenOutfitController.CharacterAnchor a, Vector3 p, Quaternion q)[] limbs, bool end)
    {
        try
        {
            var oc = h.outfitController;
            if (oc == null) return;
            if (_held.Add(h.humanID)) Plugin.Event($"DRAG citizen {h.humanID} \"{h.citizenName}\" is being dragged by another player");
            if (end) _heldLast.Remove(h.humanID);
            else
            {
                var anchors = new CitizenOutfitController.CharacterAnchor[limbs.Length];
                for (int i = 0; i < limbs.Length; i++) anchors[i] = limbs[i].a;
                _heldLast[h.humanID] = (UnityEngine.Time.unscaledTime, anchors);
            }
            foreach (var (a, p, q) in limbs)
            {
                var t = oc.GetBodyAnchor(a);
                if (t == null) continue;
                var rb = t.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = !end;
                    rb.position = p;
                    rb.rotation = q;
                    if (end) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                }
                t.SetPositionAndRotation(p, q);
            }
            if (end) { _held.Remove(h.humanID); Plugin.Event($"DRAG citizen {h.humanID} put down"); }
            Applied++;
        }
        catch (Exception e) { Plugin.Event($"DRAG apply failed: {e.Message}"); }
    }

    /// <summary>Session over or host gone: nobody will send the final pose any more, so let every held body go.</summary>
    public static void Reset()
    {
        _dragging = -1;
        foreach (var id in new List<int>(_heldLast.Keys)) Release(id, "session over");
        _heldLast.Clear();
        _held.Clear();
    }

    // The citizens of a replaced world are gone: nothing to release.
    static DragSync() => World.Reloaded += () => { _heldLast.Clear(); _held.Clear(); };
}
