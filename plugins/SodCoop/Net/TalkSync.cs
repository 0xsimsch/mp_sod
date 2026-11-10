using System;
using System.Collections.Generic;
using HarmonyLib;
using SodCoop.Core;

namespace SodCoop.Net;

/// <summary>
/// B4: one conversation per citizen. A client asks the host before its dialog opens
/// (<c>NewAIController.TalkTo</c> is the entry point); the host grants first come first served, locks the
/// citizen to that player's body (<c>Actor.SetInteracting</c>, so the host's AI stops for it) and unlocks on
/// <c>TalkEnd</c> (the client's <c>OnReturnFromTalkTo</c>) or when the player drops. The host's own player
/// can't start a conversation with a citizen a client is talking to.
/// </summary>
public static class TalkSync
{
    // host: citizen humanID → player talking to it
    static readonly Dictionary<int, PlayerId> _locks = new();
    // client: granted citizens, and requests waiting for an answer (with the conversation type)
    static readonly HashSet<int> _granted = new();
    static readonly Dictionary<int, InteractionController.ConversationType> _pending = new();

    public static int Granted { get; private set; }
    public static int Denied { get; private set; }

    static NetSession S => NetSession.Current;

    [HarmonyPatch(typeof(NewAIController), nameof(NewAIController.TalkTo))]
    static class Ask
    {
        static bool Prefix(NewAIController __instance, InteractionController.ConversationType convoType)
        {
            var s = S;
            var h = __instance.human;
            if (s == null || h == null || !World.Ready) return true;
            // A player's body is a borrowed citizen; "talking" to it would start a citizen conversation with your partner.
            var partner = PlayerRegistry.RemoteOwnerOf(h);
            if (partner != null)
            {
                Notify.Show($"That's {partner.DisplayName}, your partner", InterfaceControls.Icon.agent);
                return false;
            }
            if (s.Role == NetSession.SessionRole.Host)
            {
                if (!_locks.TryGetValue(h.humanID, out var who)) return true;
                Plugin.Event($"TALK host blocked: citizen {h.humanID} is talking to {who}");
                return false;
            }
            if (_granted.Contains(h.humanID)) return true;
            _pending[h.humanID] = convoType;
            s.SendTalk(Protocol.TalkRequest(h.humanID));
            return false;
        }
    }

    [HarmonyPatch(typeof(NewAIController), nameof(NewAIController.OnReturnFromTalkTo))]
    static class End
    {
        static void Postfix(NewAIController __instance)
        {
            var s = S;
            var h = __instance.human;
            if (s == null || h == null || s.Role != NetSession.SessionRole.Client || !_granted.Remove(h.humanID)) return;
            s.SendTalk(Protocol.TalkEnd(h.humanID));
        }
    }

    /// <summary>Host: a client asks.</summary>
    public static bool HostDecide(PlayerId asker, int humanId)
    {
        Human h = null;
        if (CityData.Instance?.citizenDictionary == null || !CityData.Instance.citizenDictionary.TryGetValue(humanId, out h) || h == null) return false;
        bool busy = (_locks.TryGetValue(humanId, out var owner) && owner != asker) || h.isDead || h.inConversation
                    || (h.interactingWith != null && !_locks.ContainsKey(humanId));
        if (busy) { Denied++; Plugin.Event($"TALK {asker} → citizen {humanId}: busy"); return false; }
        _locks[humanId] = asker;
        _lockedAt[humanId] = UnityEngine.Time.unscaledTime;
        if (PlayerRegistry.TryGet(asker, out var p) && p.Body?.interactable != null) h.SetInteracting(p.Body.interactable);
        Granted++;
        Plugin.Event($"TALK {asker} → citizen {humanId}: granted (citizen held for them)");
        return true;
    }

    /// <summary>Host: conversation over (or the player left).</summary>
    public static void HostRelease(PlayerId who, int humanId)
    {
        if (!_locks.TryGetValue(humanId, out var owner) || owner != who) return;
        _locks.Remove(humanId);
        _lockedAt.Remove(humanId);
        if (CityData.Instance.citizenDictionary.TryGetValue(humanId, out var h) && h != null) h.SetInteracting(null);
        Plugin.Event($"TALK {who} finished with citizen {humanId}");
    }

    // host: when each lock was granted
    static readonly Dictionary<int, float> _lockedAt = new();
    static float _nextLockCheck;
    const float LockCheckEvery = 2f;
    /// <summary>A lock this old whose player has walked off is dropped; conversations happen face to face.</summary>
    const float LockAwayAfter = 120f, LockAwayDistance = 10f;
    /// <summary>No conversation lasts this long.</summary>
    const float LockMaxAge = 600f;

    /// <summary>
    /// Host, per frame: a lock is only released by the client's TalkEnd (its OnReturnFromTalkTo). If the conversation
    /// never opened or never returned there (citizen knocked out meanwhile, …), the citizen stayed held
    /// (SetInteracting) and the host's player locked out until that client left. Drop locks that can't be live:
    /// the citizen is dead, the player's body is gone, or the lock is old and the player is far away.
    /// </summary>
    public static void HostTick(float now)
    {
        if (_locks.Count == 0 || now < _nextLockCheck) return;
        _nextLockCheck = now + LockCheckEvery;
        try
        {
            var dict = CityData.Instance?.citizenDictionary;
            if (dict == null || !World.Ready) return;
            foreach (var id in new List<int>(_locks.Keys))
            {
                var who = _locks[id];
                float age = _lockedAt.TryGetValue(id, out var at) ? now - at : LockMaxAge + 1f;
                string why = null;
                if (!dict.TryGetValue(id, out var h) || h == null) why = "the citizen is gone";
                else if (h.isDead) why = "the citizen is dead";
                else if (!PlayerRegistry.TryGet(who, out var p) || p.Body == null) why = "the player has no body here";
                else if (age > LockMaxAge) why = $"held for {age:F0} s";
                else if (age > LockAwayAfter && UnityEngine.Vector3.Distance(p.Body.transform.position, h.transform.position) > LockAwayDistance)
                    why = $"held for {age:F0} s and the player is away";
                if (why == null) continue;
                Plugin.Event($"TALK lock of {who} on citizen {id} dropped: {why}");
                HostRelease(who, id);
            }
        }
        catch (Exception e) { Plugin.Event($"TALK lock check failed: {e.Message}"); }
    }

    public static void HostReleaseAll(PlayerId who)
    {
        foreach (var id in new List<int>(_locks.Keys)) HostRelease(who, id);
    }

    /// <summary>Client: the host answered.</summary>
    public static void ClientReply(int humanId, bool granted)
    {
        if (!_pending.Remove(humanId, out var convoType)) return;
        if (!granted)
        {
            Denied++;
            Plugin.Event($"TALK citizen {humanId} is busy talking to someone else");
            return;
        }
        Granted++;
        _granted.Add(humanId);
        if (CityData.Instance.citizenDictionary.TryGetValue(humanId, out var h) && h?.ai != null) h.ai.TalkTo(convoType);
    }

    public static void Reset()
    {
        _locks.Clear();
        _lockedAt.Clear();
        _granted.Clear();
        _pending.Clear();
    }
}
