using System.Collections.Generic;
using SodCoop.Core;
using SodCoop.Net;
using UnityEngine;

namespace SodCoop.Puppet;

/// <summary>
/// Client side of NPC sync (host-authoritative citizens). Citizens the host streams are taken off
/// their local AI and driven from the host's poses; a citizen nobody has sent for
/// <see cref="ReleaseAfter"/> seconds gets its own AI back (it left the client's area).
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

    static readonly HashSet<System.IntPtr> _koPtrs = new();

    /// <summary>Driven and not knocked out: its AI component must stay off (see Patches/AiTickPatch, KeepAiOff).
    /// While knocked out the game's own KO update may run.</summary>
    public static bool IsDrivenAwake(Actor a) => a != null && _drivenPtrs.Contains(a.Pointer) && !_koPtrs.Contains(a.Pointer);
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
                if (_parked.Remove(c.HumanId, out var parked))
                {
                    // Back in range: carry on from the state it was left in, so "no longer knocked out / cuffed"
                    // is seen as a change (ApplyStates) and its AI setting from before the host took it is kept.
                    e.AiWasEnabled = parked.AiWasEnabled;
                    e.KnockedOut = parked.KnockedOut;
                    e.PostureSeen = parked.PostureSeen;
                    if (e.KnockedOut) _koPtrs.Add(cit.Pointer);
                }
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
            if (ko) _koPtrs.Add(e.Citizen.Pointer); else _koPtrs.Remove(e.Citizen.Pointer);
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
            if (e.KnockedOut) continue;
            // Safety net for Patches/AiTickPatch.KeepAiOff: an enabled AI component runs the citizen's own
            // FrequentUpdate (path following, facing, neck, attacks) on top of the host's poses.
            if (e.Citizen.ai != null && e.Citizen.ai.enabled) e.Citizen.ai.enabled = false;
            if (e.Source.TryGetPose(now, out var pose)) e.Driver.Apply(pose);
        }
        if (stale != null) foreach (var id in stale) Release(id, InClientSession);
    }

    // SYNC_GAPS G15: a citizen that leaves the host's range knocked out or cuffed used to be woken / handed to its
    // local AI (it stood up, or walked off in cuffs) although it still lies or stands there on the host. Those are
    // parked instead: left exactly as the host had them, AI off, until the host sends them again or the session ends.
    static readonly Dictionary<int, Entry> _parked = new();

    public static int Parked => _parked.Count;

    static bool InClientSession => NetSession.Current != null && NetSession.Current.Role == NetSession.SessionRole.Client;

    /// <summary>A released citizen must not keep chasing or watching a player on its own AI: the host decided that.</summary>
    static void ClearPursuit(Citizen c, int id)
    {
        try
        {
            var ai = c.ai;
            if (ai == null) return;
            if (!ai.persuit && ai.persuitTarget == null && ai.reactionState == NewAIController.ReactionState.none) return;
            ai.persuit = false;
            ai.persuitTarget = null;
            if (ai.reactionState != NewAIController.ReactionState.none) ai.SetReactionState(NewAIController.ReactionState.none);
        }
        catch (System.Exception ex) { Plugin.Event($"NPC clearing the pursuit of {id} failed: {ex.Message}"); }
    }

    static void Release(int id, bool keepState = false)
    {
        if (!_driven.Remove(id, out var e)) return;
        if (keepState && e.Citizen != null && e.Citizen.ai != null && !e.Citizen.isDead && !PlayerRegistry.IsRemoteBody(e.Citizen)
            && (e.KnockedOut || e.Arms == (int)CitizenAnimationController.ArmsBoolSate.armsCuffed))
        {
            // Still counts as driven (_drivenPtrs/_koPtrs stay): nothing local may wake it, move it or let it hit us.
            _parked[id] = e;
            e.Citizen.ai.enabled = false;
            CitizenEventSync.Released(id);
            Plugin.Event($"NPC citizen {id} left the host's range {(e.KnockedOut ? "knocked out" : "cuffed")}: kept that way");
            return;
        }
        if (e.KnockedOut && e.Citizen != null && e.Citizen.ai != null)
        {
            e.Citizen.ai.enabled = true;
            e.Citizen.ai.SetKO(false, e.Citizen.transform.position, e.Citizen.transform.forward, true, 0f, false, 1f);
            e.Citizen.animationController?.SetRagdoll(false, false);
        }
        if (e.Citizen != null) { _drivenPtrs.Remove(e.Citizen.Pointer); _koPtrs.Remove(e.Citizen.Pointer); }
        CitizenEventSync.Released(id);
        // A citizen that became a player's body keeps its AI off (RemotePlayerBody owns it now).
        if (e.Citizen != null && e.Citizen.ai != null && !PlayerRegistry.IsRemoteBody(e.Citizen) && !e.Citizen.isDead) e.Citizen.ai.enabled = e.AiWasEnabled;
        if (e.Citizen != null && e.Citizen.ai != null && e.Citizen.ai.enabled && !e.Citizen.isDead)
        {
            // Re-plan from where the host left the citizen, instead of resuming a stale local goal (seen: U-turns at the edge).
            // Its own weapon again first (CitizenEventSync set the host's preset for the attack animations).
            try { e.Citizen.ai.UpdateCurrentWeapon(); } catch (System.Exception ex) { Plugin.Event($"NPC weapon restore for {id} failed: {ex.Message}"); }
            ClearPursuit(e.Citizen, id);
            try { e.Citizen.ai.AITick(true, true); }
            catch (System.Exception ex) { Plugin.Event($"NPC re-plan for {id} failed: {ex.Message}"); }
        }
    }

    /// <summary>Stop driving a citizen without touching its AI (e.g. it's about to die by a network event).</summary>
    public static void Forget(int humanId)
    {
        if (_driven.Remove(humanId, out var e) && e.Citizen != null) { _drivenPtrs.Remove(e.Citizen.Pointer); _koPtrs.Remove(e.Citizen.Pointer); }
        if (_parked.Remove(humanId, out var pk) && pk.Citizen != null) { _drivenPtrs.Remove(pk.Citizen.Pointer); _koPtrs.Remove(pk.Citizen.Pointer); }
        CitizenEventSync.Released(humanId);
    }

    public static void ReleaseAll()
    {
        // Parked citizens too: nobody will say otherwise any more, so they get the old release (woken, own AI).
        foreach (var kv in new List<KeyValuePair<int, Entry>>(_parked))
            if (!_driven.ContainsKey(kv.Key)) _driven[kv.Key] = kv.Value;
        _parked.Clear();
        foreach (var id in new List<int>(_driven.Keys)) Release(id);
        _koPtrs.Clear();
        CitizenEventSync.ClearClient();
        _hasSeq = false;
    }
}
