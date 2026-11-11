using System;
using System.Collections.Generic;
using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// Fight sounds. The game plays them only on the machine where the hit is worked out:
/// <list type="bullet">
/// <item>a player's punch / weapon impact in <c>FirstPersonItemController.MeleeAttack</c> and <c>CounterAttack</c>
///   (<c>AudioControls.punchHit*</c>, the weapon's <c>impactEvent</c>), on that player's machine;</item>
/// <item>a citizen's attack in <c>NewAIController.AttackUpdate</c> (<c>MurderWeaponPreset.fireEvent</c>, gunshots
///   included) and its impact in <c>DamageColliderController.ProcessHit</c> (<c>impactEvent</c> /
///   <c>impactEventBody</c>), both on the host only: a client's citizens have no AI, so no attack update and no
///   damage collider.</item>
/// </list>
/// So a client heard its own punches and nothing else of a fight, and the host never heard the client's.
/// Every <c>AudioController.PlayWorldOneShot</c> of one of those events is sent (event name, position, volume) and
/// played at the same place on the other machines. Nothing else is relayed: doors, switches and the like are
/// replayed through the game's own code and make their sound there.
/// <para>A citizen hitting a player: <c>ProcessHit</c> plays the weapon's <c>impactEventPlayer</c> in 2D for a hit
/// actor with <c>isPlayer</c>, which on the host is also a client's body. The message says whose body was hit, and
/// that player gets the 2D sound too.</para>
/// </summary>
public static class SoundSync
{
    const byte KindOneShot = 1; // str event, vec3 position, float volume, byte hitPlayer (255 = nobody), str 2D event for that player
    /// <summary>Weapon fire (gunshots) carries this far.</summary>
    const float MaxDistance = 60f;
    /// <summary>Punches and impacts: <see cref="NetConfig.FightSoundDistance"/> (config), never beyond <see cref="MaxDistance"/>.</summary>
    static float HitDistance => Mathf.Clamp(NetConfig.FightSoundDistance?.Value ?? 15f, 0f, MaxDistance);
    const int MaxPerSecond = 30;

    public static int Sent { get; private set; }
    public static int Applied { get; private set; }

    static readonly Dictionary<IntPtr, string> _relayed = new();       // event → its name
    static readonly Dictionary<IntPtr, string> _playerImpact = new();   // impactEvent → the same weapon's impactEventPlayer
    static readonly Dictionary<string, AudioEvent> _byName = new();
    static readonly HashSet<string> _fire = new();                      // names of the weapons' fire events
    static bool _built, _applying, _failLogged;
    static float _windowStart;
    static int _inWindow, _dropped;

    static NetSession S => NetSession.Current;

    public static void Reset()
    {
        _built = false;
        _relayed.Clear(); _playerImpact.Clear(); _byName.Clear(); _fire.Clear();
        _dropped = 0;
    }

    public static string Status() => $"sound sent={Sent} applied={Applied}";

    static void Add(AudioEvent e)
    {
        if (e == null) return;
        string n = e.name;
        if (string.IsNullOrEmpty(n)) return;
        _relayed[e.Pointer] = n;
        _byName[n] = e;
    }

    /// <summary>The fight events, from the game's own data (the same on every machine).</summary>
    static void Build()
    {
        _built = true;
        try
        {
            var ac = AudioControls.Instance;
            if (ac != null)
            {
                Add(ac.punchHitFabric); Add(ac.punchHitWood); Add(ac.punchHitCarpet); Add(ac.punchHitPlaster);
                Add(ac.punchHitConcrete); Add(ac.punchHitTile); Add(ac.punchHitGlass); Add(ac.punchHitMetal);
                Add(ac.punchHitFlesh); Add(ac.punchHitPlayer); Add(ac.punchHitWall);
            }
            int weapons = 0;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MurderWeaponPreset>()))
            {
                var w = o.TryCast<MurderWeaponPreset>();
                if (w == null) continue;
                weapons++;
                Add(w.fireEvent); Add(w.impactEvent); Add(w.impactEventBody);
                if (w.fireEvent != null && !string.IsNullOrEmpty(w.fireEvent.name)) _fire.Add(w.fireEvent.name);
                if (w.impactEventPlayer != null)
                {
                    string pn = w.impactEventPlayer.name;
                    if (!string.IsNullOrEmpty(pn))
                    {
                        _byName[pn] = w.impactEventPlayer;
                        if (w.impactEvent != null) _playerImpact[w.impactEvent.Pointer] = pn;
                    }
                }
            }
            Plugin.Event($"SOUND relaying {_relayed.Count} fight sound event(s) ({weapons} weapon presets)");
        }
        catch (Exception e) { Plugin.Event($"SOUND could not list the fight sounds: {e.Message}"); }
    }

    [HarmonyPatch(typeof(AudioController), nameof(AudioController.PlayWorldOneShot))]
    static class OneShot
    {
        static void Postfix(AudioEvent eventPreset, Actor who, Vector3 worldPosition, float volumeOverride)
        {
            if (_applying || eventPreset == null) return;
            var s = S;
            if (s == null || !World.Ready || !s.ClientWorldJoined) return;
            if (s.Role == NetSession.SessionRole.Host && !s.HasClients) return;
            try
            {
                if (!_built) Build();
                if (!_relayed.TryGetValue(eventPreset.Pointer, out var name)) return;

                float now = Time.unscaledTime;
                if (now - _windowStart >= 1f) { _windowStart = now; _inWindow = 0; }
                if (++_inWindow > MaxPerSecond)
                {
                    if (_dropped++ == 0) Plugin.Event($"SOUND more than {MaxPerSecond} fight sounds in a second: the rest of each second is not sent");
                    return;
                }

                // Whose body was hit, for the 2D "you were hit" sound the game only plays for the local machine.
                byte hit = 255;
                string forPlayer = "";
                var owner = who != null ? PlayerRegistry.RemoteOwnerOf(who) : null;
                if (owner != null && _playerImpact.TryGetValue(eventPreset.Pointer, out var pn)) { hit = owner.Id.Value; forPlayer = pn; }

                s.SendExtra(ExtraWire.Write(ExtraWire.Sound, KindOneShot, w =>
                {
                    ExtraWire.Str(w, name);
                    w.Write(worldPosition.x); w.Write(worldPosition.y); w.Write(worldPosition.z);
                    w.Write(volumeOverride);
                    w.Write(hit);
                    ExtraWire.Str(w, forPlayer);
                }));
                if (Sent++ == 0) Plugin.Event($"SOUND first fight sound sent: {name} at {worldPosition}");
            }
            catch (Exception e)
            {
                if (!_failLogged) { _failLogged = true; Plugin.Event($"SOUND send failed: {e.Message}"); }
            }
        }
    }

    public static void OnPacket(NetSession s, PeerId from, PlayerId? fromPlayer, byte[] data)
    {
        using var r = ExtraWire.Read(data, out var kind);
        if (kind != KindOneShot) return;
        string name = ExtraWire.Str(r);
        var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        float volume = r.ReadSingle();
        byte hit = r.ReadByte();
        string forPlayer = ExtraWire.Str(r);

        if (s.Role == NetSession.SessionRole.Host) s.SendExtraExcept(data, from); // a client's sound, for the other clients
        if (!World.Ready || !s.ClientWorldJoined) return;
        var me = Player.Instance;
        var audio = AudioController.Instance;
        if (me == null || audio == null) return;
        if (!_built) Build();
        if (float.IsNaN(volume) || volume <= 0f || volume > 4f) volume = 1f;

        bool hitMe = hit != 255 && PlayerRegistry.Local != null && PlayerRegistry.Local.Id.Value == hit;
        // Another player's punches and impacts were audible from 60 m (seen: the host heard the client's hits from
        // far off); only gunshots keep that range.
        if (!hitMe && Vector3.Distance(me.transform.position, pos) > (_fire.Contains(name) ? MaxDistance : HitDistance)) return;
        if (!_byName.TryGetValue(name, out var ev) || ev == null)
        {
            if (!_failLogged) { _failLogged = true; Plugin.Event($"SOUND unknown event \"{name}\": not played"); }
            return;
        }
        _applying = true;
        try
        {
            // No actor: a sound with one is also something citizens hear and blame on it.
            audio.PlayWorldOneShot(ev, null, null, pos, null, null, volume, null, false, null, false);
            if (hitMe && forPlayer.Length > 0 && _byName.TryGetValue(forPlayer, out var ev2) && ev2 != null)
                audio.Play2DSound(ev2, null, volume);
            if (Applied++ == 0) Plugin.Event($"SOUND first fight sound played: {name} at {pos}{(hitMe ? " (hit us)" : "")}");
        }
        finally { _applying = false; }
    }
}
