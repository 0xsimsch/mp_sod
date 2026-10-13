using System;
using System.Collections.Generic;
using UnityEngine;

namespace SodCoop.Core;

/// <summary>
/// All players in the session. Code must loop over this instead of assuming
/// "the player" or "the other player".
/// </summary>
public static class PlayerRegistry
{
    static readonly Dictionary<PlayerId, NetPlayer> _players = new();

    // Il2Cpp wrapper objects aren't stable identities; key bodies by native pointer.
    static readonly Dictionary<IntPtr, NetPlayer> _byRemoteBody = new();

    public static int MaxPlayers { get; set; } = 2;

    public static IReadOnlyCollection<NetPlayer> All => _players.Values;
    public static int Count => _players.Count;

    public static NetPlayer Local { get; private set; }

    public static NetPlayer Add(PlayerId id, string name, bool isLocal)
    {
        if (_players.ContainsKey(id)) throw new InvalidOperationException($"{id} already registered");
        if (_players.Count >= MaxPlayers) throw new InvalidOperationException($"session full ({MaxPlayers})");

        var p = new NetPlayer(id, name, isLocal);
        _players.Add(id, p);
        if (isLocal) Local = p;
        return p;
    }

    public static void Remove(PlayerId id)
    {
        if (!_players.Remove(id, out var p)) return;
        if (p.RemoteBody?.Human != null) _byRemoteBody.Remove(p.RemoteBody.Human.Pointer);
        if (ReferenceEquals(Local, p)) Local = null;
    }

    public static bool TryGet(PlayerId id, out NetPlayer player) => _players.TryGetValue(id, out player);

    /// <summary>Client side: the host assigned us an id; move the local player to it.</summary>
    public static void ReassignLocal(PlayerId newId)
    {
        if (Local == null || Local.Id == newId) return;
        if (_players.ContainsKey(newId)) throw new InvalidOperationException($"{newId} already registered");
        var name = Local.DisplayName;
        _players.Remove(Local.Id);
        var p = new NetPlayer(newId, name, isLocal: true);
        _players.Add(newId, p);
        Local = p;
    }

    /// <summary>Lowest free id, or null if the session is full.</summary>
    public static PlayerId? NextFreeId()
    {
        if (_players.Count >= MaxPlayers) return null;
        for (int i = 0; i < 256; i++)
            if (!_players.ContainsKey(new PlayerId((byte)i))) return new PlayerId((byte)i);
        return null;
    }

    internal static void BindRemoteBody(NetPlayer p, Human body) => _byRemoteBody[body.Pointer] = p;
    internal static void UnbindRemoteBody(Human body) => _byRemoteBody.Remove(body.Pointer);

    /// <summary>The remote player that owns this actor's body, or null.</summary>
    public static NetPlayer RemoteOwnerOf(Actor actor)
    {
        if (actor == null) return null;
        return _byRemoteBody.TryGetValue(actor.Pointer, out var p) ? p : null;
    }

    public static bool IsRemoteBody(Actor actor) => actor != null && _byRemoteBody.ContainsKey(actor.Pointer);

    /// <summary>Which player (local or remote) this actor is, or null for ordinary citizens.</summary>
    public static NetPlayer OwnerOf(Actor actor)
    {
        if (actor == null) return null;
        var remote = RemoteOwnerOf(actor);
        if (remote != null) return remote;
        var local = Player.Instance;
        return local != null && actor.Pointer == local.Pointer ? Local : null;
    }

    /// <summary>Nearest living player body to a world position (for tick rate / culling later).</summary>
    public static NetPlayer NearestTo(Vector3 pos, out float distance)
    {
        NetPlayer best = null;
        distance = float.MaxValue;
        foreach (var p in _players.Values)
        {
            if (p.LifeState != PlayerLifeState.Alive) continue;
            var body = p.Body;
            if (body == null) continue;
            float d = Vector3.Distance(body.transform.position, pos);
            if (d < distance) { distance = d; best = p; }
        }
        return best;
    }

    public static void Clear()
    {
        _players.Clear();
        _byRemoteBody.Clear();
        Local = null;
    }
}
