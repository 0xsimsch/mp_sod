using System;
using System.Collections.Generic;
using SodCoop.Core;
using SodCoop.Puppet;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// One co-op session, host or client. Polled from <see cref="CoopBehaviour"/> every frame.
///
/// Host: accepts Hello → assigns a <see cref="PlayerId"/>, borrows a citizen as that player's body
/// and drives it from received poses; relays every pose to the other clients; forwards damage that
/// citizens deal to a remote body back to its owner.
/// Client: Hello until Welcome, then streams its own pose; shows every other player (host included)
/// as a borrowed-citizen body; applies forwarded damage to its own <c>Player</c>.
/// </summary>
public sealed class NetSession : IDisposable
{
    public enum SessionRole { Host, Client }

    public static NetSession Current { get; private set; }

    const float PoseInterval = 0.05f;     // 20 Hz
    const float HelloInterval = 1f;
    const float Timeout = 10f;
    public const string ModVersion = "0.9.12";

    readonly ITransport _transport;
    readonly SteamLobby _lobby;
    public SessionRole Role { get; }

    // peer ↔ player (host: all clients; client: just the host)
    readonly Dictionary<PeerId, PlayerId> _playerOf = new();
    readonly Dictionary<PlayerId, PeerId> _peerOf = new();
    readonly Dictionary<PeerId, float> _lastHeard = new();
    readonly Dictionary<PlayerId, ushort> _lastSeq = new();

    PeerId _host;
    bool _hasHost;
    bool _welcomed;
    bool _rejected;
    /// <summary>Client: bodies we created for other players (host and other clients; all relayed by the host).</summary>
    readonly HashSet<PlayerId> _clientBodies = new();
    readonly Dictionary<PlayerId, float> _spawnRetryAt = new();

    // --- which citizen stands for which player (Puppet/Doubles): the host assigns, everyone borrows the same ones
    /// <summary>Host: the citizen that shows the host on clients (hidden here).</summary>
    Human _selfDouble;
    float _nextBodyMap;
    /// <summary>Client: the host's assignment for each other player.</summary>
    readonly Dictionary<PlayerId, int> _assignedBody = new();
    float _nextHello;
    float _nextPose;
    float _nextTime;
    float _nextKeepAlive;
    float _nextNpc;
    ushort _npcSeq;
    const float NpcInterval = 0.1f;
    const float NpcRadius = 40f;
    const int NpcPerPacket = 50;
    const float NpcKeepAlive = 1f;
    readonly Dictionary<(PeerId, int), (float time, Vector3 pos, float yaw, int state)> _npcLastSent = new();

    public int NpcPosesSent { get; private set; }

    /// <summary>Host: each client gets the citizens within <see cref="NpcRadius"/> of its body (interest management).</summary>
    void SendCitizenPoses(float now)
    {
        var citizens = CityData.Instance?.citizenDirectory;
        if (citizens == null) return;
        _npcSeq++;
        var batch = new List<CitizenPose>();
        foreach (var kv in _playerOf)
        {
            if (!PlayerRegistry.TryGet(kv.Value, out var p) || p.Body == null) continue;
            var center = p.Body.transform.position;
            batch.Clear();
            for (int i = 0; i < citizens.Count && batch.Count < 255; i++)
            {
                var c = citizens[i];
                if (c == null || c.isDead || Puppet.Doubles.IsBorrowed(c)) continue;
                var pos = c.transform.position;
                // 40 m indoors / 70 m outdoors, +10 m for citizens already synced (no pop-in/out at the edge).
                bool synced = _npcLastSent.TryGetValue((kv.Key, c.humanID), out var prevSent) && now - prevSent.time < 3f;
                float radius = CitizenEventSync.SyncRadius(p.Body, synced);
                if ((pos - center).sqrMagnitude > radius * radius) continue;
                float yaw = c.transform.eulerAngles.y;
                var anim = c.animationController;
                byte idle = anim != null ? (byte)anim.idleAnimationState : (byte)0;
                byte arms = anim != null ? (byte)anim.armsBoolAnimationState : (byte)0;
                bool ko = c.ai != null && c.ai.ko;
                var posture = (c.isInBed ? PoseFlags.InBed : 0) | (c.isInLowBed ? PoseFlags.LowBed : 0)
                              | (anim != null && anim.flipToRightAnimation ? PoseFlags.FlipRight : 0) | (c.isAsleep ? PoseFlags.Asleep : 0);
                int state = idle | (arms << 8) | (ko ? 1 << 16 : 0) | ((int)posture << 17);
                // Delta filter: unchanged citizens only every NpcKeepAlive (the client releases after 3 s of silence).
                var key = (kv.Key, c.humanID);
                if (_npcLastSent.TryGetValue(key, out var last) && now - last.time < NpcKeepAlive && last.state == state &&
                    (pos - last.pos).sqrMagnitude < 0.05f * 0.05f && Mathf.Abs(Mathf.DeltaAngle(yaw, last.yaw)) < 5f) continue;
                _npcLastSent[key] = (now, pos, yaw, state);
                batch.Add(new CitizenPose
                {
                    HumanId = c.humanID,
                    Position = ToVec(pos),
                    Yaw = yaw,
                    Flags = (c.isCrouched ? PoseFlags.Crouched : 0) | (c.isRunning ? PoseFlags.Running : 0) | (c.isMoving ? PoseFlags.Moving : 0)
                            | (ko ? PoseFlags.KnockedOut : 0) | posture,
                    Idle = idle,
                    Arms = arms,
                });
            }
            // ≤ 50 per packet (~1 KB): unreliable packets above the MTU get fragmented, and losing one fragment loses all.
            for (int start = 0; start < batch.Count; start += NpcPerPacket)
            {
                var chunk = batch.GetRange(start, Math.Min(NpcPerPacket, batch.Count - start)).ToArray();
                SendTo(kv.Key, Protocol.CitizenPoses(new CitizenPosesMsg { Seq = _npcSeq, Time = now, Citizens = chunk }), false);
            }
            NpcPosesSent += batch.Count;
        }
    }
    ushort _seq;
    bool _lastIllegal;

    public int PacketsIn { get; private set; }
    public int PacketsOut { get; private set; }

    NetSession(SessionRole role, ITransport transport, SteamLobby lobby)
    {
        Role = role;
        _transport = transport;
        _lobby = lobby;
        Core.World.Reloaded += OnWorldReloaded;
        CitizenEventSync.SendToPlayer = (id, data, reliable) => { if (Current == this && _peerOf.TryGetValue(id, out var peer)) SendTo(peer, data, reliable); };
    }

    void OnWorldReloaded()
    {
        if (Current != this) return;
        CaseRecordSync.Reset();
        if (Role != SessionRole.Client) return;
        foreach (var id in new List<PlayerId>(_clientBodies)) ForgetStaleBody(id, "the world was reloaded");
        _lastSeq.Clear();
    }

    // ------------------------------------------------------------------ start / stop

    public static NetSession HostUdp(int port) => Start(new NetSession(SessionRole.Host, new UdpTransport(port), null));

    public static NetSession JoinUdp(string hostPort)
    {
        var t = new UdpTransport(0);
        var s = new NetSession(SessionRole.Client, t, null);
        s.SetHost(t.AddPeer(UdpTransport.Parse(hostPort)));
        return Start(s);
    }

    public static NetSession HostSteam(int maxPlayers)
    {
        var t = new SteamTransport();
        var lobby = new SteamLobby();
        var s = new NetSession(SessionRole.Host, t, lobby);
        lobby.MemberJoined += peer =>
        {
            // Sending first opens the session from our side, so the client's Hello is accepted.
            t.Accept(peer);
            s.SendTo(peer, Protocol.Ping(), true);
            s._lobbyMembers.Add(peer);
            Plugin.Event($"NET lobby: {lobby.FriendName(peer)} ({peer}) joined");
        };
        lobby.MemberLeft += peer => s.Drop(peer, "left the lobby");
        lobby.Failed += why => Plugin.Event($"NET lobby error: {why}");
        lobby.Create(maxPlayers);
        return Start(s);
    }

    public static NetSession JoinSteam(ulong lobbyId)
    {
        var t = new SteamTransport();
        var lobby = new SteamLobby();
        var s = new NetSession(SessionRole.Client, t, lobby);
        lobby.Entered += host =>
        {
            t.Accept(host);
            s.SetHost(host);
            Plugin.Event($"NET lobby {lobby.LobbyId} entered; host is {lobby.FriendName(host)} ({host})");
        };
        lobby.Failed += why =>
        {
            Plugin.Event($"NET lobby error: {why}");
            // Never got to a host: close, so the next F11 tries again instead of printing this dead session.
            if (!s._hasHost) s._closeRequested = true;
        };
        if (lobbyId != 0) lobby.Join(lobbyId);
        else
        {
            Plugin.Event("NET no lobby id given: looking for a friend who is hosting…");
            lobby.FindAndJoin();
        }
        return Start(s);
    }

    static NetSession Start(NetSession s)
    {
        Current?.Dispose();
        DoorSync.Reset();
        CaseRecordSync.Reset();
        Current = s;
        Plugin.Event($"NET {s.Role} session started on {s._transport.Name} (protocol v{Protocol.Version})");
        return s;
    }

    void SetHost(PeerId host)
    {
        _host = host;
        _hasHost = true;
        _lastHeard[host] = Time.unscaledTime;
    }

    public void Dispose()
    {
        if (Role == SessionRole.Client) Profiles.OnLeave("left the session");
        RestSync.Reset();
        ElevatorSync.Reset();
        BloodSync.Reset();
        PhoneSync.Reset();
        Puppet.Doubles.UnhideAll();
        Core.World.Reloaded -= OnWorldReloaded;
        if (Role == SessionRole.Host)
        {
            foreach (var peer in _playerOf.Keys) SendTo(peer, Protocol.Bye(), true);
            foreach (var peer in new List<PeerId>(_playerOf.Keys)) Drop(peer, "session closed");
        }
        else
        {
            if (_hasHost) SendTo(_host, Protocol.Bye(), true);
            ResetClientWorld("session closed");
            if (PlayerRegistry.Local != null && !PlayerRegistry.Local.Id.IsHost) PlayerRegistry.ReassignLocal(PlayerId.Host);
        }
        _lobby?.Dispose();
        _transport.Dispose();
        Puppet.NpcPuppets.ReleaseAll();
        DoorSync.Reset();
        TalkSync.Reset();
        CaseRecordSync.Reset();
        if (ReferenceEquals(Current, this)) Current = null;
        Plugin.Event($"NET session closed (in {PacketsIn}, out {PacketsOut})");
    }

    // ------------------------------------------------------------------ per frame

    public void Update(float now)
    {
        _transport.Tick();
        _lobby?.Tick(now);

        while (_transport.TryReceive(out var from, out var data))
        {
            PacketsIn++;
            _lastHeard[from] = now;
            try { Handle(from, data, now); }
            catch (Exception e) { Plugin.Event($"NET bad packet from {from}: {e.Message}"); }
        }

        if (Role == SessionRole.Client && _hasHost && !_welcomed && !_rejected && now >= _nextHello)
        {
            _nextHello = now + HelloInterval;
            SendTo(_host, Protocol.Hello(new HelloMsg
            {
                ProtocolVersion = Protocol.Version,
                GameVersion = Game.Instance != null ? Game.Instance.buildID : "",
                ModVersion = ModVersion,
                Name = LocalName(),
            }), true);
        }

        // Keepalive: a client loading the host's world sends nothing else for a minute.
        if (Role == SessionRole.Client && _hasHost && now >= _nextKeepAlive)
        {
            _nextKeepAlive = now + 2f;
            SendTo(_host, Protocol.Ping(), false);
        }

        if (now >= _nextPose && CanSendPose())
        {
            _nextPose = now + PoseInterval;
            SendLocalPose(now);
        }

        if (Role == SessionRole.Host && _playerOf.Count > 0 && SessionData.Instance != null && (now >= _nextTime || TimeSync.Dirty))
        {
            _nextTime = now + TimeSync.Interval;
            TimeSync.Dirty = false;
            Broadcast(Protocol.TimeSync(TimeSync.Capture()), false);
        }

        if (Role == SessionRole.Host && _playerOf.Count > 0 && now >= _nextNpc)
        {
            _nextNpc = now + NpcInterval;
            SendCitizenPoses(now);
        }
        if (Role == SessionRole.Host && _playerOf.Count > 0) CitizenEventSync.HostTick(now);
        else if (Role == SessionRole.Client) CitizenEventSync.ClientTick(now);

        if (Role == SessionRole.Host) SnapshotSync.Poll();

        // Pausing: the host pauses the world only while everyone is paused; clients report their own state.
        if (Role == SessionRole.Host && _playerOf.Count > 0) WorldSync.HostTick(now);
        if (Role == SessionRole.Host && _playerOf.Count > 0) ElevatorSync.HostTick(now);
        if (now >= _nextBodyMap)
        {
            _nextBodyMap = now + 5f;
            if (Role == SessionRole.Host && _playerOf.Count > 0) Broadcast(Protocol.BodyMap(BodyMapNow()), true);
            Puppet.Doubles.Tick();
        }
        if (Role == SessionRole.Host) Patches.PauseSync.HostTick(_playerOf.Keys);
        else
        {
            if (_welcomed && _hasHost && Patches.PauseSync.ReportPending)
            {
                Patches.PauseSync.ReportPending = false;
                SendTo(_host, Protocol.PauseState(Patches.PauseSync.LocalPaused), true);
            }
            Patches.PauseSync.ApplyWorld(); // also catches a load that left the game paused
        }
        if ((Role == SessionRole.Host && _playerOf.Count > 0) || (Role == SessionRole.Client && _welcomed))
        {
            CaseRecordSync.Flush(now, (json, active) => Broadcast(Protocol.CaseRecord(json, active), true));
            JobSync.Flush(now, (type, json) => Broadcast(Protocol.JobRecord(type, json), true));
            ObjectiveSync.Tick(now);
            CaseSync.FlushNotices();
            BloodSync.Tick(now);
            RestSync.Tick(now, _playerOf.Values);
            if (Role == SessionRole.Client) Profiles.Tick(now);
            StatusSync.Tick(now);
        }

        SendIllegalActionChanges();
        DropSilentPeers(now);
        WatchSteamConnections(now);
        if (_closeRequested) Dispose();
    }

    bool _closeRequested;

    /// <summary>Name sent in Hello: the configured one, else the Steam name.</summary>
    string LocalName()
    {
        var configured = NetConfig.PlayerName.Value;
        if (!string.IsNullOrEmpty(configured)) return configured;
        if (_transport is SteamTransport)
        {
            var steam = SteamNative.Utf8(SteamNative.SteamAPI_ISteamFriends_GetPersonaName(SteamNative.SteamAPI_SteamFriends_v017()));
            if (!string.IsNullOrEmpty(steam)) return steam;
        }
        return "Player";
    }

    // --- Steam connection diagnostics: log every change of a peer's session state, and the relay status,
    // so a failed two-machine test can be read from the two logs afterwards.
    float _nextSteamWatch;
    int _lastRelay = int.MinValue;
    readonly Dictionary<PeerId, int> _steamState = new();
    readonly HashSet<PeerId> _lobbyMembers = new();

    void WatchSteamConnections(float now)
    {
        if (_transport is not SteamTransport steam || now < _nextSteamWatch) return;
        _nextSteamWatch = now + 1f;
        int relay = steam.RelayStatus;
        if (relay != _lastRelay)
        {
            _lastRelay = relay;
            Plugin.Event($"NET steam relay network: {relay} (100 = ready, negative = failed)");
        }
        foreach (var peer in SteamPeers())
        {
            var line = steam.Describe(peer, out int state);
            if (_steamState.TryGetValue(peer, out var last) && last == state) continue;
            _steamState[peer] = state;
            Plugin.Event($"NET steam session with {peer}: {line}");
        }
    }

    IEnumerable<PeerId> SteamPeers()
    {
        if (Role == SessionRole.Client) { if (_hasHost) yield return _host; yield break; }
        var all = new HashSet<PeerId>(_lobbyMembers);
        all.UnionWith(_lastHeard.Keys);
        foreach (var peer in all) yield return peer;
    }

    string SteamStatus()
    {
        if (_transport is not SteamTransport steam) return "";
        var parts = new List<string> { $"relay={steam.RelayStatus}" };
        foreach (var peer in SteamPeers()) parts.Add($"{peer}: {steam.Describe(peer, out _)}");
        return " steam[" + string.Join("; ", parts) + "]";
    }

    /// <summary>A few lines for the on-screen overlay (<see cref="SessionOverlay"/>): who is connected and how, and the world transfer.</summary>
    public string OverlayText()
    {
        var sb = new System.Text.StringBuilder();
        string relay = _transport is SteamTransport st ? $"  relay {st.RelayStatus}" : "";
        sb.AppendLine($"SoD Co-op {ModVersion}  {(Role == SessionRole.Host ? "HOST" : "CLIENT")}  {_transport.Name}" +
                      (_lobby != null ? $"  lobby {_lobby.LobbyId}" : "") + relay);
        if (Role == SessionRole.Client)
        {
            sb.AppendLine(_rejected ? "rejected by the host (see sodcoop.log)" : _welcomed ? $"in the session as {PlayerRegistry.Local?.Id}" : "connecting to the host…");
            // A host menu pauses everyone, without our pause screen: say so, or it looks like we can't move.
            if (Patches.PauseSync.WorldPaused) sb.AppendLine("world paused: every player is in a menu");
        }
        else
            sb.AppendLine($"players: {_playerOf.Count + 1}/{PlayerRegistry.MaxPlayers}");
        foreach (var peer in SteamPeers())
        {
            string name = _lobby?.FriendName(peer) ?? peer.ToString();
            if (_playerOf.TryGetValue(peer, out var pid) && PlayerRegistry.TryGet(pid, out var np)) name = $"{pid} {np.DisplayName}";
            string state = _transport is SteamTransport s ? s.Describe(peer, out _) : "udp";
            int cut = state.IndexOf(" [", StringComparison.Ordinal); // drop Steam's long connection description
            string menu = Role == SessionRole.Host && Patches.PauseSync.IsClientPaused(peer) ? "  [in a menu]" : "";
            sb.AppendLine($"  {name}: {(cut > 0 ? state.Substring(0, cut) : state)}{menu}");
        }
        foreach (var pl in PlayerRegistry.All)
            if (!pl.IsLocal && pl.RemoteBody != null) sb.AppendLine($"  body of {pl.Id}: {pl.RemoteBody.Describe()}");
        if (SnapshotSync.LastStatus.Length > 0) sb.AppendLine($"world: {SnapshotSync.LastStatus}");
        sb.Append($"packets in {PacketsIn} / out {PacketsOut}    Ctrl+F10 hides this");
        return sb.ToString();
    }

    bool CanSendPose() => Player.Instance != null && PlayerRegistry.Local != null &&
                          (Role == SessionRole.Host ? _playerOf.Count > 0 : _welcomed);

    void SendLocalPose(float now)
    {
        var p = Player.Instance;
        var msg = new PoseMsg
        {
            PlayerId = PlayerRegistry.Local.Id.Value,
            Seq = ++_seq,
            Time = now,
            Position = ToVec(FeetOf(p)),
            Yaw = p.transform.eulerAngles.y,
            Pitch = 0f,
            Flags = (p.isCrouched ? PoseFlags.Crouched : 0) | (p.isRunning ? PoseFlags.Running : 0) | (p.isMoving ? PoseFlags.Moving : 0),
            HeldId = -1,
        };
        if (p.isHiding) msg.Flags |= PoseFlags.PlayerHiding;
        if (p.inAirVent) msg.Flags |= PoseFlags.PlayerInVent;
        var items = FirstPersonItemController.Instance;
        if (items != null && items.flashlight) msg.Flags |= PoseFlags.PlayerFlashlight;
        msg.Flags |= SeatedFlags(p, msg.Position.Y);
        if ((msg.Flags & PoseFlags.PlayerSitting) != 0) LogSeated(p, msg.Position.Y);
        msg.Activity = ActivityOf(p);
        msg.Swings = Patches.SwingCapture.Swings;
        var cam = p.cam != null && p.cam.cam != null ? p.cam.cam.transform : Camera.main != null ? Camera.main.transform : null;
        if (cam != null)
        {
            var fwd = cam.forward;
            msg.Pitch = -Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
            msg.LookYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        }
        else msg.LookYaw = msg.Yaw;
        CaptureHeld(ref msg);
        Broadcast(Protocol.Pose(msg), false);
    }

    static bool _feetLogged;

    /// <summary>
    /// Where the local player stands. Citizens (the bodies others see) are positioned at their feet, but the
    /// first-person player's transform is not (seen: remote bodies floating with their middle at camera height),
    /// so use the bottom of the player's CharacterController capsule.
    /// </summary>
    static Vector3 FeetOf(Player p)
    {
        var pos = p.transform.position;
        var cc = p.charController != null ? p.charController : p.GetComponentInChildren<CharacterController>();
        float feet = cc != null ? cc.bounds.min.y : float.NaN;
        float drop = pos.y - feet;
        if (float.IsNaN(feet) || drop < 0.2f || drop > 2.5f)
        {
            // Capsule reading implausible (a body under the city would be invisible): drop a ray to the floor instead.
            float ground = float.NaN;
            foreach (var hit in Physics.RaycastAll(pos + Vector3.up * 0.1f, Vector3.down, 3f))
                if ((cc == null || hit.collider.Pointer != cc.Pointer) && (float.IsNaN(ground) || hit.point.y > ground)) ground = hit.point.y;
            if (!_feetLogged) Plugin.Event($"POSE capsule bottom implausible ({drop:F2} m below the player); floor by raycast: {(float.IsNaN(ground) ? "none" : ground.ToString("F2"))}");
            _feetLogged = true;
            if (!float.IsNaN(ground)) pos.y = ground;
            return pos;
        }
        if (!_feetLogged)
        {
            _feetLogged = true;
            Plugin.Event($"POSE player transform y={pos.y:F2}, capsule bottom y={feet:F2} (sending feet, {pos.y - feet:F2} m lower)");
        }
        pos.y = feet;
        return pos;
    }

    /// <summary>
    /// Sitting or lying: the player is locked into a furniture interaction (chair, sofa, bed) and its eyes are lowered.
    /// The game has no "sitting" flag for the player; it's a locked-in interaction plus a camera transition.
    /// </summary>
    static PoseFlags SeatedFlags(Player p, float feetY)
    {
        try
        {
            var ic = InteractionController.Instance;
            if (ic == null || ic.lockedInInteraction == null || p.isCrouched) return 0;
            var cam = p.cam != null && p.cam.cam != null ? p.cam.cam.transform : null;
            if (cam == null) return 0;
            float eyes = cam.position.y - feetY;
            if (eyes < 0.7f) return PoseFlags.PlayerLying;
            // Sitting down moves the player up onto the seat, so the eyes can stay at standing height above the
            // capsule (seen: the body stood on the chair). The game keeps the last transition (ChairEnter, SitDown,
            // SitDownHunched, SitFloor) until the next one, so trust its name while we're locked into the seat.
            string transition = p.currentTransition != null ? p.currentTransition.name : "";
            if (transition.StartsWith("Sit") || transition.StartsWith("Chair") || eyes < 1.35f) return PoseFlags.PlayerSitting;
        }
        catch { /* best effort */ }
        return 0;
    }

    static bool _seatLogged;

    /// <summary>
    /// The citizen sitting animation expects the body at seat height, which is where the seated player's capsule rests
    /// (tested: the floor under the seat put the body half a body too low). Logged once for checking other seats.
    /// </summary>
    static void LogSeated(Player p, float feetY)
    {
        if (_seatLogged) return;
        _seatLogged = true;
        try { Plugin.Event($"POSE seated: transition={(p.currentTransition != null ? p.currentTransition.name : "-")} body at seat y={feetY:F2} (floor node y={(p.currentNode != null ? p.currentNode.position.y.ToString("F2") : "-")})"); }
        catch { /* best effort */ }
    }

    /// <summary>What the local player is doing, for the partner's view (first match wins).</summary>
    static PlayerActivity ActivityOf(Player p)
    {
        try
        {
            var items = FirstPersonItemController.Instance;
            var ic = InteractionController.Instance;
            if (p.activeCall != null) return PlayerActivity.Phone;
            var locked = ic != null ? ic.lockedInInteraction : null;
            string lockedName = locked?.preset != null ? locked.preset.name : "";
            if (lockedName.Contains("Computer") || lockedName.Contains("Cruncher") || lockedName.Contains("Terminal")) return PlayerActivity.Typing;
            if (p.isLockpicking) return PlayerActivity.Lockpicking;
            if (items != null && items.isConsuming) return PlayerActivity.Consuming;
            if (items != null && items.smokingTokeActive) return PlayerActivity.Smoking;
            if (ic != null && ic.readingMode) return PlayerActivity.Reading;
            if (p.illegalActionActive) return PlayerActivity.Searching;
        }
        catch { /* best effort */ }
        return PlayerActivity.None;
    }

    /// <summary>What the local player has in their hands: a carried physics object, or the drawn inventory item.</summary>
    static void CaptureHeld(ref PoseMsg msg)
    {
        try
        {
            var carry = InteractionController.Instance != null ? InteractionController.Instance.carryingObject : null;
            if (carry != null && carry.interactable != null) { msg.Held = HeldKind.Carried; msg.HeldId = carry.interactable.id; return; }
            var fpc = FirstPersonItemController.Instance;
            if (fpc == null || fpc.drawnItem == null || !fpc.drawnItem.modelActive) return;
            var slot = BioScreenController.Instance != null ? BioScreenController.Instance.selectedSlot : null;
            msg.HeldId = slot != null ? slot.interactableID : -1;
            msg.Held = fpc.isRaised ? HeldKind.Raised : HeldKind.Item;
        }
        catch { /* best effort: a pose without the hands is still a pose */ }
    }

    void SendIllegalActionChanges()
    {
        if (Role != SessionRole.Client || !_welcomed || Player.Instance == null) return;
        bool now = Player.Instance.illegalActionActive;
        if (now == _lastIllegal) return;
        _lastIllegal = now;
        SendTo(_host, Protocol.IllegalAction(new IllegalActionMsg { PlayerId = PlayerRegistry.Local.Id.Value, Active = now }), true);
    }

    void DropSilentPeers(float now)
    {
        List<PeerId> silent = null;
        foreach (var kv in _lastHeard)
            if (now - kv.Value > Timeout) (silent ??= new()).Add(kv.Key);
        if (silent == null) return;
        foreach (var peer in silent)
        {
            _lastHeard.Remove(peer);
            if (Role == SessionRole.Client && _hasHost && peer == _host)
            {
                Plugin.Event("NET host timed out");
                _welcomed = false;
                ResetClientWorld("host timed out");
            }
            else Drop(peer, "timed out");
        }
    }

    // ------------------------------------------------------------------ receive

    void Handle(PeerId from, byte[] data, float now)
    {
        var type = Protocol.PeekType(data);
        if (type == null) return;
        // A client only listens to its host (stray UDP endpoints, old sessions).
        if (Role == SessionRole.Client && (!_hasHost || from != _host)) return;
        switch (type.Value)
        {
            case MsgType.Hello when Role == SessionRole.Host: OnHello(from, Protocol.ReadHello(data)); break;
            case MsgType.Welcome when Role == SessionRole.Client: OnWelcome(Protocol.ReadWelcome(data)); break;
            case MsgType.Reject when Role == SessionRole.Client:
                _rejected = true; // stop asking; F12 + F11 to retry
                Plugin.Event($"NET rejected by host: {Protocol.ReadReject(data).Reason}");
                break;
            case MsgType.Pose: OnPose(from, data, now); break;
            case MsgType.IllegalAction when Role == SessionRole.Host: OnIllegalAction(from, Protocol.ReadIllegalAction(data)); break;
            case MsgType.Damage when Role == SessionRole.Client: OnDamage(Protocol.ReadDamage(data)); break;
            case MsgType.CitizenDamage when Role == SessionRole.Host && _playerOf.TryGetValue(from, out var hitter):
                OnCitizenDamage(hitter, Protocol.ReadDamage(data)); break;
            case MsgType.Bye:
                if (Role == SessionRole.Host) Drop(from, "said bye");
                else if (_hasHost && from == _host) { _welcomed = false; ResetClientWorld("host left"); }
                break;
            case MsgType.DoorState: OnDoorState(from, data); break;
            case MsgType.TimeSync when Role == SessionRole.Client: TimeSync.Apply(Protocol.ReadTimeSync(data)); break;
            case MsgType.PlayerKO: OnPlayerKO(from, data); break;
            case MsgType.PauseState when Role == SessionRole.Host && _playerOf.ContainsKey(from):
                Patches.PauseSync.OnClientPaused(from, Protocol.ReadPauseState(data)); break;
            case MsgType.CaseEvent: OnCaseEvent(from, data); break;
            case MsgType.Spawn: OnSpawn(from, data); break;
            case MsgType.Item: OnItem(from, data); break;
            case MsgType.CitizenPoses when Role == SessionRole.Client: Puppet.NpcPuppets.OnPoses(Protocol.ReadCitizenPoses(data), now); break;
            case MsgType.DialogExecute when Role == SessionRole.Host && _playerOf.TryGetValue(from, out var dlgFrom): DialogSync.OnPacket(dlgFrom, data); break;
            case MsgType.DialogEnd when Role == SessionRole.Host && _playerOf.TryGetValue(from, out var dlgEndFrom): DialogSync.OnPacket(dlgEndFrom, data); break;
            case MsgType.VictimDiscovered when Role == SessionRole.Host && _playerOf.TryGetValue(from, out var finder): CaseIds.OnClientDiscovery(finder); break;
            case MsgType.Phone:
                PhoneSync.OnPacket(Role == SessionRole.Host && _playerOf.TryGetValue(from, out var phoneSender) ? phoneSender : (PlayerId?)null, data);
                break;
            case MsgType.Lift:
                if (Role == SessionRole.Host && !_playerOf.ContainsKey(from)) break;
                ElevatorSync.OnPacket(data); break;
            case MsgType.Gadget when Role == SessionRole.Host && _playerOf.TryGetValue(from, out var gadgetFrom):
                GadgetSync.OnPacket(gadgetFrom, data); break;
            case MsgType.RestState when Role == SessionRole.Host && _playerOf.TryGetValue(from, out var rester): RestSync.OnPacket(rester, data); break;
            case MsgType.Spatter:
                if (Role == SessionRole.Host && !_playerOf.ContainsKey(from)) break;
                BloodSync.OnPacket(data, Role == SessionRole.Host); break;
            case MsgType.Newspaper when Role == SessionRole.Client: NewsSync.OnPacket(data); break;
            case MsgType.BodyMap when Role == SessionRole.Client: OnBodyMap(Protocol.ReadBodyMap(data)); break;
            case MsgType.PlayerLeft when Role == SessionRole.Client: DropRemoteBody(new PlayerId(Protocol.ReadPlayerLeft(data)), "left the session"); break;
            case MsgType.Death when Role == SessionRole.Client: DeathSync.Apply(Protocol.ReadDeath(data)); break;
            case MsgType.MurderRecord when Role == SessionRole.Client: MurderSync.Apply(Protocol.ReadMurderRecord(data)); break;
            case (MsgType)ExtraWire.Objective: case (MsgType)ExtraWire.CaseResolve:
            case (MsgType)ExtraWire.Status: case (MsgType)ExtraWire.Notice:
                ExtraWire.OnPacket(this, from, Role == SessionRole.Host && _playerOf.TryGetValue(from, out var xp) ? xp : (PlayerId?)null, (byte)type.Value, data);
                break;
            case MsgType.CitizenEvent when Role == SessionRole.Client: CitizenEventSync.OnEvent(data); break;
            case MsgType.CitizenStates when Role == SessionRole.Client: CitizenEventSync.OnStates(data); break;
            case MsgType.WorldEvent:
                if (Role == SessionRole.Host)
                {
                    // A client's own action (window, wedge, tape, barge, breaker): apply, then pass on.
                    if (!_playerOf.ContainsKey(from)) break;
                    foreach (var peer in _playerOf.Keys) if (peer != from) SendTo(peer, data, true);
                }
                WorldSync.Apply(WorldProtocol.Read(data));
                break;
            case MsgType.JobRecord:
                if (Role == SessionRole.Host)
                {
                    // Job info a client acquired: apply, then pass on (last writer wins).
                    if (!_playerOf.ContainsKey(from)) break;
                    foreach (var peer in _playerOf.Keys) if (peer != from) SendTo(peer, data, true);
                }
                var (jobType, jobJson) = Protocol.ReadJobRecord(data);
                JobSync.Apply(jobType, jobJson);
                break;
            case MsgType.JobAccept when Role == SessionRole.Host && _playerOf.TryGetValue(from, out var jobTaker):
                JobSync.OnAcceptRequest(Protocol.ReadJobAccept(data), jobTaker.ToString());
                break;
            case MsgType.CaseRecord:
                if (Role == SessionRole.Host)
                {
                    // A client's board edit: apply, then pass on (last writer wins).
                    if (!_playerOf.ContainsKey(from)) break;
                    foreach (var peer in _playerOf.Keys) if (peer != from) SendTo(peer, data, true);
                }
                var (caseJson, caseActive) = Protocol.ReadCaseRecord(data);
                CaseRecordSync.Apply(caseJson, caseActive && Role == SessionRole.Client); // only the host picks the active case
                break;
            case MsgType.SnapshotChunk when Role == SessionRole.Client: SnapshotSync.OnChunk(Protocol.ReadSnapshotChunk(data)); break;
            case MsgType.TalkRequest when Role == SessionRole.Host:
                if (_playerOf.TryGetValue(from, out var asker))
                {
                    int talkId = Protocol.ReadTalkHumanId(data);
                    SendTo(from, Protocol.TalkReply(talkId, TalkSync.HostDecide(asker, talkId)), true);
                }
                break;
            case MsgType.TalkEnd when Role == SessionRole.Host:
                if (_playerOf.TryGetValue(from, out var talker)) TalkSync.HostRelease(talker, Protocol.ReadTalkHumanId(data));
                break;
            case MsgType.TalkReply when Role == SessionRole.Client: TalkSync.ClientReply(Protocol.ReadTalkHumanId(data), Protocol.ReadTalkGranted(data)); break;
            case MsgType.Forensic:
                if (Role == SessionRole.Host)
                {
                    if (!_playerOf.TryGetValue(from, out var fid) || fid.Value != data[3]) break;
                    foreach (var peer in _playerOf.Keys) if (peer != from) SendTo(peer, data, true);
                }
                ForensicSync.Apply(Protocol.ReadForensic(data));
                break;
            case MsgType.Ping: break;
        }
    }

    void OnHello(PeerId from, HelloMsg m)
    {
        if (m.ProtocolVersion != Protocol.Version)
        {
            SendTo(from, Protocol.Reject(new RejectMsg { Reason = $"protocol v{m.ProtocolVersion}, host has v{Protocol.Version}" }), true);
            return;
        }
        string hostBuild = Game.Instance != null ? Game.Instance.buildID : "";
        if (!string.IsNullOrEmpty(m.GameVersion) && m.GameVersion != hostBuild)
        {
            SendTo(from, Protocol.Reject(new RejectMsg { Reason = $"game {m.GameVersion}, host has {hostBuild}" }), true);
            return;
        }

        if (_playerOf.TryGetValue(from, out var known)) _lastSeq.Remove(known); // same peer restarted: seq starts over
        bool isNewPlayer = !_playerOf.ContainsKey(from);
        if (!_playerOf.TryGetValue(from, out var id))
        {
            var free = PlayerRegistry.NextFreeId();
            if (free == null) { SendTo(from, Protocol.Reject(new RejectMsg { Reason = "session full" }), true); return; }
            var body = SpawnBody(free.Value, string.IsNullOrEmpty(m.Name) ? "Client" : m.Name);
            if (body == null) { SendTo(from, Protocol.Reject(new RejectMsg { Reason = "host could not create a body" }), true); return; }
            id = free.Value;
            _playerOf[from] = id;
            _peerOf[id] = from;
            Plugin.Event($"NET {from} joined as {id} \"{m.Name}\" (mod {m.ModVersion}, game {m.GameVersion})");
            Notify.Show($"{(string.IsNullOrEmpty(m.Name) ? "A player" : m.Name)} joined", InterfaceControls.Icon.agent);
        }

        SendTo(from, Protocol.Welcome(new WelcomeMsg
        {
            PlayerId = id.Value,
            MaxPlayers = (byte)PlayerRegistry.MaxPlayers,
            CityShareCode = CityId(),
        }), true);
        MurderSync.SendAll(json => SendTo(from, Protocol.MurderRecord(json), true));
        CaseRecordSync.SendAll((json, active) => SendTo(from, Protocol.CaseRecord(json, active), true));
        SendTo(from, Protocol.BodyMap(BodyMapNow()), true);
        NewsSync.SendTo(data => SendTo(from, data, true));
        if (isNewPlayer) SnapshotSync.RequestFor(chunk => SendTo(from, chunk, true), () => _transport.PendingReliableBytes(from));
    }

    void OnWelcome(WelcomeMsg m)
    {
        if (_welcomed) return;
        _welcomed = true;
        Patches.PauseSync.ReportPending = true; // tell the host whether we are in a menu right now
        Notify.Show("Connected to the host", InterfaceControls.Icon.agent);
        PlayerRegistry.MaxPlayers = m.MaxPlayers;
        // Joined from the main menu: no local player registered yet (EnsureLocalPlayer waits for a world).
        if (PlayerRegistry.Local == null) PlayerRegistry.Add(new PlayerId(m.PlayerId), LocalName(), isLocal: true);
        else PlayerRegistry.ReassignLocal(new PlayerId(m.PlayerId));
        Puppet.NpcPuppets.ReleaseAll(); // the host's NPC sequence restarted
        _lastIllegal = false;           // a fresh body starts legal; if we're mid-action, the next frame re-sends it
        string mine = CityId();
        Plugin.Event($"NET welcomed as P{m.PlayerId} (max {m.MaxPlayers}); host city \"{m.CityShareCode}\"" +
                     (m.CityShareCode == mine ? "" : $" — DIFFERENT from ours \"{mine}\""));
    }

    void OnPose(PeerId from, byte[] data, float now)
    {
        var m = Protocol.ReadPose(data);
        var id = new PlayerId(m.PlayerId);

        if (Role == SessionRole.Host)
        {
            // A client may only move its own player.
            if (!_playerOf.TryGetValue(from, out var owner) || owner != id) return;
            foreach (var peer in _playerOf.Keys) if (peer != from) SendTo(peer, data, false);
        }
        else if (PlayerRegistry.Local != null && id == PlayerRegistry.Local.Id) return;

        if (_lastSeq.TryGetValue(id, out var last) && !Protocol.SeqNewer(m.Seq, last)) return;
        _lastSeq[id] = m.Seq;

        if (Role == SessionRole.Client)
        {
            // Never borrow a citizen from a world that is still loading or about to be replaced: that body ends up
            // pointing at a destroyed citizen and the player is invisible for the rest of the session.
            if (!Core.World.Ready) return;
            if (PlayerRegistry.TryGet(id, out var existing) && !existing.IsLocal && existing.RemoteBody != null && existing.RemoteBody.Stale)
                ForgetStaleBody(id, "its citizen belonged to the previous world");
        }

        if (!PlayerRegistry.TryGet(id, out var player))
        {
            if (Role == SessionRole.Host) return;
            // Client: first pose of the host or another client → give them a body here (retry at most every 5 s).
            if (_spawnRetryAt.TryGetValue(id, out var retry) && now < retry) return;
            // Borrow the citizen the host assigned (the same one everywhere); none yet → wait for the body map.
            if (!_assignedBody.TryGetValue(id, out var assigned)) return;
            if (SpawnBody(id, id.IsHost ? "Host" : $"Player {id.Value}", assigned) == null) { _spawnRetryAt[id] = now + 5f; return; }
            _spawnRetryAt.Remove(id);
            _clientBodies.Add(id);
            PlayerRegistry.TryGet(id, out player);
            if (id.IsHost) SnapshotSync.HandOverHostItems();
        }

        player?.RemoteBody?.SetLookAndHeld(m.Pitch, m.LookYaw, m.HeldId, m.Held);
        player?.RemoteBody?.SetPlayerFlags(m.Flags);
        player?.RemoteBody?.SetActivity(m.Activity);
        player?.RemoteBody?.SetSwings(m.Swings);
        if (player?.RemoteBody?.PoseSource is NetworkPoseSource src)
            src.Push(new PlayerPose
            {
                Time = m.Time,
                Position = new Vector3(m.Position.X, m.Position.Y, m.Position.Z),
                Yaw = m.Yaw,
                Crouched = (m.Flags & PoseFlags.Crouched) != 0,
                Running = (m.Flags & PoseFlags.Running) != 0,
                Moving = (m.Flags & PoseFlags.Moving) != 0,
            }, now);
    }

    void OnIllegalAction(PeerId from, IllegalActionMsg m)
    {
        if (!_playerOf.TryGetValue(from, out var id) || id.Value != m.PlayerId) return;
        if (PlayerRegistry.TryGet(id, out var p) && p.RemoteBody != null)
        {
            p.RemoteBody.IllegalActionActive = m.Active;
            Plugin.Event($"NET {id} illegal action = {m.Active}");
        }
    }

    void OnDamage(DamageMsg m)
    {
        var me = Player.Instance;
        if (me == null || PlayerRegistry.Local == null || m.PlayerId != PlayerRegistry.Local.Id.Value) return;
        Human attacker = null;
        if (m.AttackerHumanId >= 0) CityData.Instance?.citizenDictionary?.TryGetValue(m.AttackerHumanId, out attacker);
        float before = me.currentHealth;
        Patches.ClientPerception.ApplyingNetworkDamage = true;
        try
        {
            me.RecieveDamage(m.Amount, attacker, new Vector3(m.Position.X, m.Position.Y, m.Position.Z),
                new Vector3(m.Direction.X, m.Direction.Y, m.Direction.Z), null, null);
        }
        finally { Patches.ClientPerception.ApplyingNetworkDamage = false; }
        // Diagnostics: a report said clients take no damage although these arrive.
        Plugin.Event($"NET damage {m.Amount:F2} from citizen {m.AttackerHumanId} (attacker {(attacker != null ? "found" : "NOT found")}): " +
                     $"health {before:F2} -> {me.currentHealth:F2} / max {me.maximumHealth:F2}, koInProgress={me.playerKOInProgress}");
    }

    void OnDoorState(PeerId from, byte[] data)
    {
        var m = Protocol.ReadDoorState(data);
        Actor actor = null;
        if (Role == SessionRole.Host)
        {
            // Only the client's own actions are accepted, attributed to its body.
            if (!_playerOf.TryGetValue(from, out var id) || id.Value != m.ByPlayer) return;
            if (PlayerRegistry.TryGet(id, out var p)) actor = p.RemoteBody?.Human;
            foreach (var peer in _playerOf.Keys) if (peer != from) SendTo(peer, data, true);
        }
        else if (m.ByPlayer != 255 && PlayerRegistry.TryGet(new PlayerId(m.ByPlayer), out var p))
        {
            actor = p.Body;
        }
        DoorSync.Apply(m, actor);
    }

    /// <summary>
    /// Send a local door change. Client → host. Host → every client whose body is within
    /// <paramref name="radius"/> of the door (interest management).
    /// </summary>
    public bool SendDoor(DoorStateMsg m, Vector3 doorPos, float radius)
    {
        var data = Protocol.DoorState(m);
        if (Role == SessionRole.Client)
        {
            if (!_welcomed) return false;
            SendTo(_host, data, true);
            return true;
        }
        bool any = false;
        foreach (var kv in _playerOf)
        {
            if (!PlayerRegistry.TryGet(kv.Value, out var p) || p.Body == null) continue;
            if (Vector3.Distance(p.Body.transform.position, doorPos) > radius) continue;
            SendTo(kv.Key, data, true);
            any = true;
        }
        return any;
    }

    void OnPlayerKO(PeerId from, byte[] data)
    {
        var m = Protocol.ReadPlayerKO(data);
        if (Role == SessionRole.Host)
        {
            if (!_playerOf.TryGetValue(from, out var id) || id.Value != m.PlayerId) return;
            foreach (var peer in _playerOf.Keys) if (peer != from) SendTo(peer, data, true);
        }
        if (PlayerRegistry.TryGet(new PlayerId(m.PlayerId), out var p) && p.RemoteBody != null)
        {
            p.RemoteBody.SetKnockedOut(m.KnockedOut);
            Plugin.Event($"NET P{m.PlayerId} knocked out = {m.KnockedOut}");
            if (m.KnockedOut) Notify.Show($"{Notify.NameOf(m.PlayerId)} was knocked out", InterfaceControls.Icon.run);
        }
    }

    void OnCaseEvent(PeerId from, byte[] data)
    {
        var m = Protocol.ReadCaseEvent(data);
        if (Role == SessionRole.Host)
        {
            if (!_playerOf.TryGetValue(from, out var id) || id.Value != m.ByPlayer) return;
            foreach (var peer in _playerOf.Keys) if (peer != from) SendTo(peer, data, true);
        }
        CaseSync.Apply(m);
    }

    public void SendCaseEvent(CaseEventMsg m)
    {
        if (Role == SessionRole.Client && !_welcomed) return;
        Broadcast(Protocol.CaseEvent(m), true);
    }

    void OnSpawn(PeerId from, byte[] data)
    {
        var m = Protocol.ReadSpawn(data);
        if (Role == SessionRole.Host)
        {
            if (!_playerOf.TryGetValue(from, out var id) || id.Value != m.ByPlayer) return;
            foreach (var peer in _playerOf.Keys) if (peer != from) SendTo(peer, data, true);
        }
        SpawnSync.Apply(m);
    }

    public void SendSpawn(SpawnMsg m)
    {
        if (Role == SessionRole.Client && !_welcomed) return;
        Broadcast(Protocol.Spawn(m), true);
    }

    void OnItem(PeerId from, byte[] data)
    {
        var m = Protocol.ReadItem(data);
        if (Role == SessionRole.Host)
        {
            if (!_playerOf.TryGetValue(from, out var id) || id.Value != m.ByPlayer) return;
            foreach (var peer in _playerOf.Keys) if (peer != from) SendTo(peer, data, true);
        }
        ItemSync.Apply(m);
    }

    public void SendItem(ItemMsg m)
    {
        if (Role == SessionRole.Client && !_welcomed) return;
        Broadcast(Protocol.Item(m), true);
    }

    public void SendDeath(DeathMsg m) => Broadcast(Protocol.Death(m), true);

    public void SendForensic(ForensicMsg m) { if (Role == SessionRole.Host || _welcomed) Broadcast(Protocol.Forensic(m), true); }

    public void SendTalk(byte[] packet) { if (_hasHost) SendTo(_host, packet, true); }

    public void SendMurderRecord(string json) => Broadcast(Protocol.MurderRecord(json), true);

    /// <summary>ExtraWire messages: client → host; host → one peer, or all clients.</summary>
    public void SendExtra(byte[] data, PeerId? to = null)
    {
        if (Role == SessionRole.Client) { if (_welcomed && _hasHost) SendTo(_host, data, true); }
        else if (to != null) SendTo(to.Value, data, true);
        else Broadcast(data, true);
    }

    public void SendExtraExcept(byte[] data, PeerId except)
    {
        if (Role != SessionRole.Host) return;
        foreach (var p in _playerOf.Keys) if (p != except) SendTo(p, data, true);
    }

    public bool TryPeerOf(PlayerId id, out PeerId peer) => _peerOf.TryGetValue(id, out peer);

    /// <summary>WorldSync: client → host; host → every client.</summary>
    public void SendWorld(byte[] data)
    {
        if (Role == SessionRole.Client) { if (_welcomed && _hasHost) SendTo(_host, data, true); return; }
        foreach (var peer in _playerOf.Keys) SendTo(peer, data, true);
    }

    public void SendVictimDiscovered()
    {
        if (Role == SessionRole.Client && _welcomed && _hasHost) SendTo(_host, Protocol.VictimDiscovered(), true);
    }

    /// <summary>Client: the host's SteamID (Profiles keys a player's profile by host and city).</summary>
    public ulong HostSteamId => _hasHost ? _host.Value : 0;

    public void SendJobAccept(int jobId)
    {
        if (Role == SessionRole.Client && _welcomed && _hasHost) SendTo(_host, Protocol.JobAccept(jobId), true);
    }

    /// <summary>The local player was knocked out or recovered: tell the others.</summary>
    public void SendLocalKO(bool knockedOut)
    {
        if (PlayerRegistry.Local == null || (Role == SessionRole.Client && !_welcomed)) return;
        if (!knockedOut && Player.Instance != null) _lastIllegal = !Player.Instance.illegalActionActive; // host cleared it on KO
        Broadcast(Protocol.PlayerKO(new PlayerKOMsg { PlayerId = PlayerRegistry.Local.Id.Value, KnockedOut = knockedOut }), true);
    }

    // ------------------------------------------------------------------ host → client events

    /// <summary>Host: a citizen hit a remote player's body; send it to that player instead.</summary>
    /// <summary>Client: our player hit a citizen. The host owns citizens, so it applies the hit there.</summary>
    public bool ForwardCitizenHit(Citizen target, float amount, Vector3 position, Vector3 direction)
    {
        if (Role != SessionRole.Client || !_welcomed || !_hasHost || PlayerRegistry.Local == null) return false;
        SendTo(_host, Protocol.CitizenDamage(new DamageMsg
        {
            PlayerId = PlayerRegistry.Local.Id.Value,
            Amount = amount,
            AttackerHumanId = target.humanID,
            Position = ToVec(position),
            Direction = ToVec(direction),
        }), true);
        Plugin.Event($"NET hit citizen {target.humanID} for {amount:F2}; sent to the host");
        return true;
    }

    /// <summary>Host: a client hit one of our citizens; apply it with the client's body as the attacker.</summary>
    void OnCitizenDamage(PlayerId hitter, DamageMsg m)
    {
        Citizen target = null;
        if (CityData.Instance?.citizenDictionary == null || !CityData.Instance.citizenDictionary.TryGetValue(m.AttackerHumanId, out var h)) return;
        target = h?.TryCast<Citizen>();
        if (target == null || target.isDead) return;
        Human attacker = PlayerRegistry.TryGet(hitter, out var p) ? p.RemoteBody?.Human : null;
        Patches.DamageForward.ApplyingRemoteHit = true;
        try
        {
            target.RecieveDamage(m.Amount, attacker, new Vector3(m.Position.X, m.Position.Y, m.Position.Z),
                new Vector3(m.Direction.X, m.Direction.Y, m.Direction.Z), null, null);
        }
        finally { Patches.DamageForward.ApplyingRemoteHit = false; }
        Plugin.Event($"NET {hitter} hit citizen {m.AttackerHumanId} for {m.Amount:F2}: health {target.currentHealth:F2}, ko={(target.ai != null && target.ai.ko)}");
    }

    /// <summary>One swing reaches a borrowed citizen body through several hit colliders; forward it once.</summary>
    const float HitDedupWindow = 0.2f;
    readonly Dictionary<(int attacker, PlayerId target), float> _lastForwardedHit = new();

    public bool ForwardDamage(Human body, float amount, Actor fromWho, Vector3 position, Vector3 direction)
    {
        if (Role != SessionRole.Host) return false;
        var owner = PlayerRegistry.RemoteOwnerOf(body);
        if (owner == null || !_peerOf.TryGetValue(owner.Id, out var peer)) return false;
        var attacker = fromWho != null ? fromWho.TryCast<Human>() : null;
        // Seen: the same hit sent 3-4 times in one millisecond (one swing, several colliders). Swallow the repeats.
        var key = (attacker != null ? attacker.humanID : -1, owner.Id);
        float t = Time.unscaledTime;
        if (_lastForwardedHit.TryGetValue(key, out var lastHit) && t - lastHit < HitDedupWindow) return true;
        _lastForwardedHit[key] = t;
        SendTo(peer, Protocol.Damage(new DamageMsg
        {
            PlayerId = owner.Id.Value,
            Amount = amount,
            AttackerHumanId = attacker != null ? attacker.humanID : -1,
            Position = ToVec(position),
            Direction = ToVec(direction),
        }), true);
        return true;
    }

    // ------------------------------------------------------------------ helpers

    static RemotePlayerBody SpawnBody(PlayerId id, string name, int assignedHumanId = -1)
    {
        var local = Player.Instance;
        Citizen donor = null;
        if (assignedHumanId >= 0 && CityData.Instance?.citizenDictionary != null &&
            CityData.Instance.citizenDictionary.TryGetValue(assignedHumanId, out var assignedHuman))
            donor = assignedHuman?.TryCast<Citizen>();
        if (donor != null) Puppet.NpcPuppets.Forget(donor.humanID); // it may have been a host-driven citizen until now
        if (donor == null) donor = RemotePlayerBody.PickDonor(local != null ? local.transform.position : Vector3.zero);
        if (donor == null) { Plugin.Event($"NET no donor citizen for {id}"); return null; }
        NetPlayer p;
        try { p = PlayerRegistry.Add(id, name, isLocal: false); }
        catch (Exception e) { Plugin.Event($"NET cannot add {id}: {e.Message}"); return null; }
        try
        {
            var body = RemotePlayerBody.Possess(p, donor);
            body.PoseSource = new NetworkPoseSource();
            return body;
        }
        catch (Exception e)
        {
            PlayerRegistry.Remove(id);
            Plugin.Event($"NET could not give {id} a body: {e.Message}");
            return null;
        }
    }

    /// <summary>Client is about to load the host's world: everything borrowed from the current world goes.</summary>
    public void PrepareForWorldReload()
    {
        foreach (var id in new List<PlayerId>(_clientBodies)) DropRemoteBody(id, "loading the host's world");
        Puppet.NpcPuppets.ReleaseAll();
        DoorSync.Reset();
        CaseRecordSync.Reset();
        _spawnRetryAt.Clear();
    }

    /// <summary>Client: the host is gone; release every other player's body, NPC puppets and host pause.</summary>
    void ResetClientWorld(string why)
    {
        Profiles.OnLeave("host gone");
        RestSync.Reset();
        ElevatorSync.Reset();
        BloodSync.Reset();
        PhoneSync.Reset();
        Puppet.Doubles.UnhideAll();
        _assignedBody.Clear();
        foreach (var id in new List<PlayerId>(_clientBodies)) DropRemoteBody(id, why);
        Puppet.NpcPuppets.ReleaseAll();
        TimeSync.Reset();
    }

    /// <summary>Host: who stands for whom: the host's own stand-in (hidden here) and each client's body.</summary>
    List<(byte, int)> BodyMapNow()
    {
        var map = new List<(byte, int)>();
        if (_selfDouble == null || _selfDouble.WasCollected || _selfDouble.isDead)
        {
            var local = Player.Instance;
            _selfDouble = RemotePlayerBody.PickDonor(local != null ? local.transform.position : Vector3.zero);
            if (_selfDouble != null) Puppet.Doubles.Hide(_selfDouble, "stands for the host on clients");
        }
        if (_selfDouble != null && PlayerRegistry.Local != null) map.Add((PlayerRegistry.Local.Id.Value, _selfDouble.humanID));
        foreach (var kv in _playerOf)
            if (PlayerRegistry.TryGet(kv.Value, out var p) && p.RemoteBody?.Human != null) map.Add((kv.Value.Value, p.RemoteBody.Human.humanID));
        return map;
    }

    /// <summary>Client: the host's assignment. Hide the citizen that stands for us; re-borrow bodies that changed.</summary>
    void OnBodyMap(List<(byte player, int humanId)> map)
    {
        if (!Core.World.Ready || PlayerRegistry.Local == null) return;
        var dict = CityData.Instance?.citizenDictionary;
        foreach (var (player, humanId) in map)
        {
            var id = new PlayerId(player);
            if (id == PlayerRegistry.Local.Id)
            {
                if (dict != null && dict.TryGetValue(humanId, out var me) && me != null) Puppet.Doubles.HideOnly(me, "stands for us on the host");
                continue;
            }
            bool changed = !_assignedBody.TryGetValue(id, out var before) || before != humanId;
            _assignedBody[id] = humanId;
            if (changed && PlayerRegistry.TryGet(id, out var p) && p.RemoteBody?.Human != null && p.RemoteBody.Human.humanID != humanId)
                DropRemoteBody(id, $"host assigned citizen {humanId}");
        }
    }

    /// <summary>Client: forget a body whose citizen is gone with the old world (nothing to release); the next pose re-creates it.</summary>
    void ForgetStaleBody(PlayerId id, string why)
    {
        _clientBodies.Remove(id);
        _spawnRetryAt.Remove(id);
        PlayerRegistry.Remove(id);
        Plugin.Event($"NET {id} gets a new body: {why}");
    }

    /// <summary>Client: release one other player's body.</summary>
    void DropRemoteBody(PlayerId id, string why)
    {
        _clientBodies.Remove(id);
        _lastSeq.Remove(id);
        if (!PlayerRegistry.TryGet(id, out var p) || p.IsLocal) return;
        p.RemoteBody?.Release();
        PlayerRegistry.Remove(id);
        Plugin.Event($"NET {id} dropped: {why}");
    }

    void Drop(PeerId peer, string why)
    {
        _lastHeard.Remove(peer);
        foreach (var k in new List<(PeerId, int)>(_npcLastSent.Keys)) if (k.Item1 == peer) _npcLastSent.Remove(k);
        Patches.PauseSync.Forget(peer);
        if (!_playerOf.Remove(peer, out var id)) return;
        CitizenEventSync.Forget(id);
        StatusSync.Forget(id);
        RestSync.Forget(id);
        Notify.Show($"{Notify.NameOf(id.Value)} left the session", InterfaceControls.Icon.agent);
        _peerOf.Remove(id);
        _lastSeq.Remove(id);
        TalkSync.HostReleaseAll(id);
        if (PlayerRegistry.TryGet(id, out var p))
        {
            p.RemoteBody?.Release();
            PlayerRegistry.Remove(id);
        }
        foreach (var other in _playerOf.Keys) SendTo(other, Protocol.PlayerLeft(id.Value), true);
        Plugin.Event($"NET {id} dropped: {why}");
    }

    void Broadcast(byte[] data, bool reliable)
    {
        if (Role == SessionRole.Client) { if (_hasHost) SendTo(_host, data, reliable); return; }
        foreach (var peer in _playerOf.Keys) SendTo(peer, data, reliable);
    }

    bool SendTo(PeerId peer, byte[] data, bool reliable)
    {
        PacketsOut++;
        return _transport.Send(peer, data, reliable);
    }

    static Vec3 ToVec(Vector3 v) => new(v.x, v.y, v.z);

    static string CityId()
    {
        var c = CityData.Instance;
        return c == null ? "" : $"{c.cityName}|{c.seed}";
    }

    public void OpenInvite() => _lobby?.OpenInviteDialog();

    public ulong LobbyId => _lobby?.LobbyId ?? 0;

    /// <summary>Debug: key of the door nearest to the local player.</summary>
    public static string NearestDoor()
    {
        var dict = CityData.Instance?.doorDictionary;
        var me = Player.Instance;
        if (dict == null || me == null) return "none";
        int best = 0; float bestD = float.MaxValue; NewDoor bestDoor = null;
        foreach (var kv in dict)
        {
            if (kv.Value == null) continue;
            float d = Vector3.Distance(kv.Value.transform.position, me.transform.position);
            if (d < bestD) { bestD = d; best = kv.Key; bestDoor = kv.Value; }
        }
        return bestDoor == null ? "none" : $"key={best} dist={bestD:F1} ajar={bestDoor.ajar:F2} locked={bestDoor.isLocked}";
    }

    public string Status() =>
        $"{Role} on {_transport.Name}" +
        (_lobby != null ? $" lobby={_lobby.LobbyId}" : "") + SteamStatus() +
        (Role == SessionRole.Client ? $" welcomed={_welcomed} rejected={_rejected} bodies={_clientBodies.Count}" : $" clients={_playerOf.Count}") +
        $" snapshot[{SnapshotSync.LastStatus}] in={PacketsIn} out={PacketsOut} npcPosesSent={NpcPosesSent} npcDriven={Puppet.NpcPuppets.Count} npcReceived={Puppet.NpcPuppets.Received} doors sent={DoorSync.Sent} applied={DoorSync.Applied} nearestDoor[{NearestDoor()}] {CaseSync.Describe()} cit ev={CitizenEventSync.EventsSent}/{CitizenEventSync.EventsApplied} st={CitizenEventSync.StatesSent}/{CitizenEventSync.StatesApplied} world sent={WorldSync.Sent} applied={WorldSync.Applied} lift sent={ElevatorSync.Sent} applied={ElevatorSync.Applied} gadget sent={GadgetSync.Sent} applied={GadgetSync.Applied} blood sent={BloodSync.Sent} applied={BloodSync.Applied} removed={BloodSync.RemovedLocal} news {NewsSync.Sent}/{NewsSync.Applied} phone sent={PhoneSync.Sent} applied={PhoneSync.Applied} dialog sent={DialogSync.Sent} applied={DialogSync.Applied} jobs sent={JobSync.Sent} applied={JobSync.Applied} acceptRequests={JobSync.AcceptRequests} items sent={ItemSync.Sent} applied={ItemSync.Applied} missed={ItemSync.Missed} cases sent={CaseRecordSync.Sent} applied={CaseRecordSync.Applied} murders sent={MurderSync.Sent} applied={MurderSync.Applied} forensics sent={ForensicSync.Sent} applied={ForensicSync.Applied} deaths sent={DeathSync.Sent} applied={DeathSync.Applied} spawns sent={SpawnSync.Sent} applied={SpawnSync.Applied} failed={SpawnSync.Failed} presets[{SpawnSync.SamplePresets()}] worldAssignID={Interactable.worldAssignID}";
}
