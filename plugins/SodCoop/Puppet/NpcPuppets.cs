using System.Collections.Generic;
using SodCoop.Core;
using SodCoop.Net;
using UnityEngine;

namespace SodCoop.Puppet;

/// <summary>
/// Client side of NPC sync (host-authoritative citizens). Citizens the host streams are taken off
/// their local AI and driven from the host's poses; a citizen nobody has sent for
/// <see cref="ReleaseAfter"/> seconds gets its own AI back (it left the client's area).
/// Not synced yet: idle/arms/bed animation states, conversations, combat.
/// </summary>
public static class NpcPuppets
{
    const float ReleaseAfter = 3f;

    sealed class Entry
    {
        public Citizen Citizen;
        public BodyDriver Driver;
        public NetworkPoseSource Source;
        public bool AiWasEnabled;
        public float LastSeen;
        public int Idle = -1, Arms = -1;
        public bool KnockedOut;
        public bool PostureSeen;
    }

    static readonly Dictionary<int, Entry> _driven = new();
    static ushort _lastSeq;
    static bool _hasSeq;

    public static int Count => _driven.Count;

    static readonly HashSet<System.IntPtr> _drivenPtrs = new();

    /// <summary>True for a citizen currently driven by the host (its own AI must not tick).</summary>
    public static bool IsDriven(Actor a) => a != null && _drivenPtrs.Contains(a.Pointer);
    public static int Received { get; private set; }

    public static void OnPoses(CitizenPosesMsg m, float now)
    {
        // One update may span several packets with the same seq; only drop strictly older ones.
        if (_hasSeq && m.Seq != _lastSeq && !Protocol.SeqNewer(m.Seq, _lastSeq)) return;
        _lastSeq = m.Seq;
        _hasSeq = true;
        var dict = CityData.Instance?.citizenDictionary;
        if (dict == null) return;

        foreach (var c in m.Citizens)
        {
            Received++;
            if (!_driven.TryGetValue(c.HumanId, out var e))
            {
                if (!dict.TryGetValue(c.HumanId, out var h) || h == null) continue;
                var cit = h.TryCast<Citizen>();
                if (cit == null || Doubles.IsBorrowed(cit) || cit.ai == null) continue; // a player's body (here or elsewhere)
                e = new Entry { Citizen = cit, Driver = new BodyDriver(cit), Source = new NetworkPoseSource(), AiWasEnabled = cit.ai.enabled };
                cit.ai.enabled = false;
                _driven[c.HumanId] = e;
                _drivenPtrs.Add(cit.Pointer);
            }
            e.LastSeen = now;
            ApplyStates(e, c);
            e.Source.Push(new PlayerPose
            {
                Time = m.Time,
                Position = new Vector3(c.Position.X, c.Position.Y, c.Position.Z),
                Yaw = c.Yaw,
                Crouched = (c.Flags & PoseFlags.Crouched) != 0,
                Running = (c.Flags & PoseFlags.Running) != 0,
                Moving = (c.Flags & PoseFlags.Moving) != 0,
            }, now);
        }
    }

    /// <summary>Idle/arms animation states and KO, set only when they change.</summary>
    static void ApplyStates(Entry e, CitizenPose c)
    {
        var anim = e.Citizen.animationController;
        ApplyPosture(e, c, anim);
        if (anim != null)
        {
            if (e.Idle != c.Idle) { anim.SetIdleAnimationState((CitizenAnimationController.IdleAnimationState)c.Idle); e.Idle = c.Idle; }
            if (e.Arms != c.Arms) { anim.SetArmsBoolState((CitizenAnimationController.ArmsBoolSate)c.Arms); e.Arms = c.Arms; }
        }
        bool ko = (c.Flags & PoseFlags.KnockedOut) != 0;
        if (ko != e.KnockedOut && e.Citizen.ai != null)
        {
            e.KnockedOut = ko;
            var t = e.Citizen.transform;
            e.Citizen.ai.enabled = true; // SetKO drives the ragdoll through the controller
            e.Citizen.ai.SetKO(ko, t.position, t.forward, true, 3600f, false, 1f);
            e.Citizen.ai.enabled = false;
            if (!ko && anim != null)
            {
                anim.SetRagdoll(false, false);
                if (anim.mainAnimator != null) anim.mainAnimator.enabled = true;
            }
        }
    }

    /// <summary>
    /// Bed/sofa and left/right variant. Compared with the citizen's own fields (not a cached copy), so a bed state our
    /// local AI left behind before the host took the citizen over is corrected on the first packet. The first time a
    /// citizen is seen the change is instant (no lie-down animation replayed).
    /// </summary>
    static void ApplyPosture(Entry e, CitizenPose c, CitizenAnimationController anim)
    {
        var h = e.Citizen;
        bool inBed = (c.Flags & PoseFlags.InBed) != 0, low = (c.Flags & PoseFlags.LowBed) != 0, right = (c.Flags & PoseFlags.FlipRight) != 0;
        try
        {
            if (anim != null && anim.flipToRightAnimation != right) anim.FlipAnimationToRight(right);
            if (h.isInBed != inBed || (inBed && h.isInLowBed != low))
            {
                h.isInBed = inBed;
                h.isInLowBed = low;
                anim?.SetInBed(inBed, low, right, !e.PostureSeen);
            }
            h.isAsleep = (c.Flags & PoseFlags.Asleep) != 0;
        }
        catch (System.Exception ex) { Plugin.Event($"NPC posture for {h.humanID} failed: {ex.Message}"); }
        e.PostureSeen = true;
    }

    public static void Update(float now)
    {
        if (_driven.Count == 0) return;
        List<int> stale = null;
        foreach (var kv in _driven)
        {
            var e = kv.Value;
            if (e.Citizen == null || PlayerRegistry.IsRemoteBody(e.Citizen)) { (stale ??= new()).Add(kv.Key); continue; }
            if (now - e.LastSeen > ReleaseAfter) { (stale ??= new()).Add(kv.Key); continue; }
            if (!e.KnockedOut && e.Source.TryGetPose(now, out var pose)) e.Driver.Apply(pose);
        }
        if (stale != null) foreach (var id in stale) Release(id);
    }

    static void Release(int id)
    {
        if (!_driven.Remove(id, out var e)) return;
        if (e.KnockedOut && e.Citizen != null && e.Citizen.ai != null)
        {
            e.Citizen.ai.enabled = true;
            e.Citizen.ai.SetKO(false, e.Citizen.transform.position, e.Citizen.transform.forward, true, 0f, false, 1f);
            e.Citizen.animationController?.SetRagdoll(false, false);
        }
        if (e.Citizen != null) _drivenPtrs.Remove(e.Citizen.Pointer);
        // A citizen that became a player's body keeps its AI off (RemotePlayerBody owns it now).
        if (e.Citizen != null && e.Citizen.ai != null && !PlayerRegistry.IsRemoteBody(e.Citizen) && !e.Citizen.isDead) e.Citizen.ai.enabled = e.AiWasEnabled;
        if (e.Citizen != null && e.Citizen.ai != null && e.Citizen.ai.enabled && !e.Citizen.isDead)
        {
            // Re-plan from where the host left the citizen, instead of resuming a stale local goal (seen: U-turns at the edge).
            try { e.Citizen.ai.AITick(true, true); }
            catch (System.Exception ex) { Plugin.Event($"NPC re-plan for {id} failed: {ex.Message}"); }
        }
    }

    /// <summary>Stop driving a citizen without touching its AI (e.g. it's about to die by a network event).</summary>
    public static void Forget(int humanId)
    {
        if (_driven.Remove(humanId, out var e) && e.Citizen != null) _drivenPtrs.Remove(e.Citizen.Pointer);
    }

    public static void ReleaseAll()
    {
        foreach (var id in new List<int>(_driven.Keys)) Release(id);
        _hasSeq = false;
    }
}
