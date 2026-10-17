using System;
using SodCoop.Core;
using SodCoop.Net;
using SodCoop.Puppet;
using UnityEngine;

namespace SodCoop;

/// <summary>
/// Per-frame driver. Prototype debug keys (Insert…PageDown and Ctrl+F11 need [Debug] TestKeys = true):
///   Keys avoid F-keys the game or Steam use (tested the hard way: F5 is the game's quicksave and
///   overwrote the player's "Quick Save"; F12 is Steam's screenshot key).
///   Insert    add a second player (P1) as a ghost that replays your movement 3 s behind you
///   Home      toggle P1 between ghost-follow and frozen
///   End       trespass test: put P1 inside a far, occupied home and hold it there
///   Delete    remove P1 (gives the citizen body back to the game)
///   PageUp    teleport yourself into P1's room
///   PageDown  toggle "P1 is doing an illegal action" (stands in for a networked lockpick/search)
///   F10       toggle keeping rooms around remote players loaded (A2)   Ctrl+F10 toggle the session overlay
///   F11       start a network session as configured in [Net] / log its status
///             Shift+F11 while hosting on Steam: invite dialog. Ctrl+F11 test: pin evidence to the active case
///             Alt+F11 swap between hosting and joining (saved to the config)
///   Ctrl+Home    test: Steam messaging loopback (send to our own SteamID, read it back)
///   Ctrl+Insert  test: talk to the nearest citizen (conversation arbitration, B4)
///   Ctrl+Delete  leave the network session
///   Ctrl+PageUp  remove the frame-rate cap (B5)   Ctrl+PageDown snapshot timing (B1)
/// </summary>
public class CoopBehaviour : MonoBehaviour
{
    const float GhostDelay = 3f;
    static readonly Vector3 GhostOffset = new(1.2f, 0f, 0f);

    GhostReplaySource _ghost;

    /// <summary>A2 experiment toggle (F10), so runs with and without it can be compared.</summary>
    public static bool RoomLoading = true;
    float _nextDiag;

    public CoopBehaviour(IntPtr ptr) : base(ptr) { }

    void Update()
    {
        float now = Time.unscaledTime;
        // Each subsystem isolated: one failing (e.g. a socket error) must not stop the others this frame.
        Step("world", World.Tick);
        Step("player", EnsureLocalPlayer);
        Step("net", () => NetSession.Current?.Update(now));
        Step("autojoin", AutoJoinFromCommandLine);
        Step("steamjoin", SteamJoinRequests.EnsureRegistered);
        Step("joinsave", SnapshotSync.CleanupAfterLoad);
        Step("ghost", RecordGhost);
        Step("throws", ItemSync.Tick);
        Step("menu", () => { if (Time.frameCount % 30 == 0) Patches.CoopMenu.Refresh(); });
        Step("keys", HandleKeys);
        Step("snapshot", PollSnapshot);
        Step("perf", SamplePerf);
        Step("steamtest", PollSteamLoopback);
        bool ready = World.Ready;
        Step("bodies", () => { if (ready) foreach (var p in PlayerRegistry.All) p.RemoteBody?.Update(now); });
        Step("npcs", () => { if (ready) NpcPuppets.Update(now); });
        // Host only: keeps walls around remote bodies for its citizens' perception. Clients' citizens don't act on players.
        Step("rooms", () => { if (RoomLoading && ready && NetSession.Current?.Role != NetSession.SessionRole.Client) RemoteRoomLoader.Update(now); });
        Step("diag", () =>
        {
            if (NetConfig.VerboseLog.Value && now >= _nextDiag) { _nextDiag = now + 3f; Patches.SightingDiag.Dump(); }
        });
    }

    /// <summary>After the Animator: remote players' heads and carried objects.</summary>
    void LateUpdate()
    {
        if (TraceFrames > 0) TraceFrames--;
        if (!World.Ready) return;
        Step("late", () => { foreach (var p in PlayerRegistry.All) p.RemoteBody?.LateApply(); });
        Step("koview", KoView.LateUpdate);
    }

    readonly System.Collections.Generic.Dictionary<string, float> _nextErrorLog = new();

    /// <summary>Run one subsystem; log its errors at most every 5 s each.</summary>
    /// <summary>Crash hunt: log every step for the first frames of a new session (the log is flushed line by line).</summary>
    internal static int TraceFrames;
    static object _tracedSession;

    void Step(string name, Action action)
    {
        if (NetSession.Current != null && !ReferenceEquals(_tracedSession, NetSession.Current)) { _tracedSession = NetSession.Current; TraceFrames = 3; }
        bool trace = TraceFrames > 0;
        if (trace) Plugin.Event($"TRACE f{Time.frameCount} {name} >");
        try { action(); if (trace) Plugin.Event($"TRACE f{Time.frameCount} {name} <"); }
        catch (Exception e)
        {
            float now = Time.unscaledTime;
            if (_nextErrorLog.TryGetValue(name, out var next) && now < next) return;
            _nextErrorLog[name] = now + 5f;
            Plugin.Event($"update ({name}) failed: {e}");
        }
    }

    static void EnsureLocalPlayer()
    {
        if (PlayerRegistry.Local != null || Player.Instance == null) return;
        // Everyone starts as P0; a client is moved to the id the host assigns (Welcome).
        var name = NetConfig.PlayerName.Value;
        PlayerRegistry.Add(PlayerId.Host, string.IsNullOrEmpty(name) ? "Host" : name, isLocal: true);
        Plugin.Event("registered local player as P0 (host)");
    }

    static bool _autoJoinChecked;

    /// <summary>
    /// An accepted invite (<c>+connect_lobby &lt;id&gt;</c>) or Steam's "Join game" (<c>+mp_sod_lobby &lt;id&gt;</c>) launches
    /// us with a lobby id; join as soon as the main menu is up (no world of our own is needed).
    /// </summary>
    static void AutoJoinFromCommandLine()
    {
        if (_autoJoinChecked || NetSession.Current != null || MainMenuController.Instance == null || RestartSafeController.Instance == null) return;
        _autoJoinChecked = true;
        var lobby = SteamLobby.LobbyFromCommandLine();
        if (lobby == 0) return;
        Plugin.Event($"joining Steam lobby {lobby} from the command line");
        try { NetSession.JoinSteam(lobby); }
        catch (Exception e) { Plugin.Event($"auto-join failed: {e}"); }
    }

    void RecordGhost()
    {
        var player = Player.Instance;
        if (_ghost != null && player != null) _ghost.Record(PlayerPose.FromHuman(player, Time.unscaledTime));
    }

    void HandleKeys()
    {
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (Plugin.TestKeys) HandleTestKeys(ctrl);
        if (Input.GetKeyDown(KeyCode.F10) && ctrl)
        {
            SessionOverlay.Visible = !SessionOverlay.Visible;
            Plugin.Event($"Ctrl+F10: session overlay = {SessionOverlay.Visible}");
        }
        else if (Input.GetKeyDown(KeyCode.F10))
        {
            RoomLoading = !RoomLoading;
            if (!RoomLoading) RemoteRoomLoader.ReleaseAll();
            Plugin.Event($"F10: remote room loading = {RoomLoading}");
        }
        bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        if (Input.GetKeyDown(KeyCode.F11) && alt) SwapHostJoin();
        else if (Input.GetKeyDown(KeyCode.F11) && ctrl) { if (Plugin.TestKeys) PinTestEvidence(); }
        else if (Input.GetKeyDown(KeyCode.F11)) StartOrInvite();
        if (Input.GetKeyDown(KeyCode.Delete) && ctrl && NetSession.Current != null) NetSession.Current.Dispose();
    }

    /// <summary>
    /// Prototype test keys, only with [Debug] TestKeys = true. They used to be always on: pressing PageUp in play
    /// teleported the player to the other player's body (seen: "the host was teleported to the client").
    /// </summary>
    void HandleTestKeys(bool ctrl)
    {
        if (Input.GetKeyDown(KeyCode.Insert) && ctrl) TalkToNearestCitizen();
        else if (Input.GetKeyDown(KeyCode.Insert)) AddGhostPlayer();
        if (Input.GetKeyDown(KeyCode.Home) && ctrl) SteamLoopbackTest();
        else if (Input.GetKeyDown(KeyCode.Home)) ToggleFreeze();
        if (Input.GetKeyDown(KeyCode.End) && !ctrl) TrespassTest();
        if (Input.GetKeyDown(KeyCode.Delete) && !ctrl) RemoveRemotePlayers();
        if (Input.GetKeyDown(KeyCode.PageUp) && !ctrl) JoinRemotePlayer();
        if (Input.GetKeyDown(KeyCode.PageDown) && !ctrl) ToggleIllegalAction();
        if (Input.GetKeyDown(KeyCode.PageDown) && ctrl) StartSnapshotTest();
        if (Input.GetKeyDown(KeyCode.PageUp) && ctrl)
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Plugin.Event($"Ctrl+PageUp: frame cap removed for measurement (runInBackground={Application.runInBackground})");
        }
    }

    /// <summary>
    /// Teleport the local player into the remote body's room, so that room's geometry loads (A2).
    /// The log records which player each reaction targets, so the two stay distinguishable.
    /// </summary>
    void JoinRemotePlayer()
    {
        var body = FirstRemote()?.RemoteBody?.Human;
        if (body?.currentRoom == null) { Plugin.Event("PageUp: no remote body in a room"); return; }
        var node = Player.Instance.FindSafeTeleport(body.currentRoom, false);
        if (node == null) { Plugin.Event("PageUp: no safe node"); return; }
        Player.Instance.Teleport(node, null, true, false, true);
        Plugin.Event($"PageUp: local player joined room {body.currentRoom.roomID} " +
                     $"(geomLoaded={body.currentRoom.geometryLoaded}, dist to P1 {Vector3.Distance(Player.Instance.transform.position, body.transform.position):F1} m)");
    }

    void ToggleIllegalAction()
    {
        var body = FirstRemote()?.RemoteBody;
        if (body == null) { Plugin.Event("PageDown: add a remote player first (Insert)"); return; }
        body.IllegalActionActive = !body.IllegalActionActive;
        Plugin.Event($"PageDown: {body.Owner} illegal action = {body.IllegalActionActive}");
    }

    /// <summary>Alt+F11: switch [Net] Mode between host and join (Steam or UDP alike). Saved, so it sticks after a restart.</summary>
    static void SwapHostJoin()
    {
        if (NetSession.Current != null)
        {
            SessionOverlay.Flash("leave the session first (Ctrl+Delete), then Alt+F11 to swap host/join");
            return;
        }
        var mode = NetConfig.Mode.Value switch
        {
            NetConfig.NetMode.HostSteam => NetConfig.NetMode.JoinSteam,
            NetConfig.NetMode.JoinSteam => NetConfig.NetMode.HostSteam,
            NetConfig.NetMode.HostUdp => NetConfig.NetMode.JoinUdp,
            _ => NetConfig.NetMode.HostUdp,
        };
        NetConfig.Mode.Value = mode; // BepInEx saves the config file on set
        bool host = mode == NetConfig.NetMode.HostSteam || mode == NetConfig.NetMode.HostUdp;
        string msg = host ? "mode: HOST (load your game, then F11 to host)" : "mode: JOIN (F11 to find a friend who is hosting)";
        Plugin.Event($"Alt+F11: {msg} [{mode}]");
        SessionOverlay.Flash(msg);
    }

    internal static void StartOrInvite()
    {
        var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        if (NetSession.Current != null)
        {
            if (shift) NetSession.Current.OpenInvite();
            Plugin.Event($"F11: {NetSession.Current.Status()}");
            return;
        }
        if (FirstRemote() != null) { Plugin.Event("F11: remove the test ghost first (Delete)"); return; }
        var mode = NetConfig.Mode.Value;
        if ((mode == NetConfig.NetMode.HostSteam || mode == NetConfig.NetMode.HostUdp) && !World.Ready)
        {
            // A host without a world can't give joiners a body or a snapshot (seen in the first two-machine test).
            const string msg = "F11: load your game first, then press F11 to host";
            Plugin.Event(msg);
            SessionOverlay.Flash(msg.Substring(5));
            return;
        }
        if ((mode == NetConfig.NetMode.HostSteam || mode == NetConfig.NetMode.HostUdp) && Game.Instance != null && !Game.Instance.sandboxMode)
        {
            // Story chapters are scripted around one player (calls, teleports, the player's apartment); not supported.
            // A warning, not a block, until the flag is confirmed in a real story game.
            const string msg = "F11: story mode isn't supported in co-op (chapters are scripted for one player); hosting anyway";
            Plugin.Event($"{msg} [sandboxMode={Game.Instance.sandboxMode}]");
            SessionOverlay.Flash(msg.Substring(5));
        }
        try { NetConfig.StartFromConfig(PlayerRegistry.MaxPlayers); }
        catch (Exception e) { Plugin.Event($"F11: could not start session: {e}"); }
    }

    // --- B1 experiment: can the host snapshot its state mid-game for a joining client? ---

    Il2CppSystem.Threading.Tasks.Task _snapshot;
    float _snapshotStart, _snapshotWorstFrame;
    int _snapshotFrames;
    string _snapshotPath;

    void StartSnapshotTest()
    {
        if (_snapshot != null || SaveStateController.Instance == null) return;
        _snapshotPath = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "mp_sod_snapshot.sod");
        _snapshotStart = Time.realtimeSinceStartup;
        _snapshotWorstFrame = 0f;
        _snapshotFrames = 0;
        var sd = SessionData.Instance;
        Plugin.Event($"Ctrl+PageDown: snapshot start (play={sd?.play}, gameTime={sd?.gameTime:F4})");
        _snapshot = SaveStateController.Instance.CaptureSaveStateAsync(_snapshotPath, true);
    }

    void PollSnapshot()
    {
        if (_snapshot == null) return;
        _snapshotFrames++;
        _snapshotWorstFrame = Mathf.Max(_snapshotWorstFrame, Time.unscaledDeltaTime);
        if (!_snapshot.IsCompleted) return;
        var sd = SessionData.Instance;
        long size = System.IO.File.Exists(_snapshotPath) ? new System.IO.FileInfo(_snapshotPath).Length : -1;
        Plugin.Event($"Ctrl+PageDown: snapshot done in {Time.realtimeSinceStartup - _snapshotStart:F2}s over {_snapshotFrames} frames, " +
                     $"worst frame {_snapshotWorstFrame * 1000f:F0} ms, faulted={_snapshot.IsFaulted}, file {size / 1024} KB, " +
                     $"play={sd?.play}, gameTime={sd?.gameTime:F4}");
        _snapshot = null;
    }

    // --- B5: frame time, logged every 10 s ---
    readonly System.Collections.Generic.List<float> _frames = new();
    float _nextPerf = 10f;

    void SamplePerf()
    {
        _frames.Add(Time.unscaledDeltaTime * 1000f);
        if (Time.unscaledTime < _nextPerf) return;
        _nextPerf = Time.unscaledTime + 10f;
        // Only useful during a session (or when asked for): keeps a real test's log readable.
        if (!NetConfig.VerboseLog.Value && NetSession.Current == null) { _frames.Clear(); return; }
        _frames.Sort();
        float sum = 0f;
        foreach (var f in _frames) sum += f;
        int remotes = 0, forced = 0;
        foreach (var p in PlayerRegistry.All) if (!p.IsLocal && p.RemoteBody != null) remotes++;
        forced = RemoteRoomLoader.ForcedCount;
        Plugin.Event($"PERF avg {sum / _frames.Count:F1} ms, p95 {_frames[(int)(_frames.Count * 0.95f)]:F1} ms, max {_frames[_frames.Count - 1]:F0} ms " +
                     $"({_frames.Count} frames) remotes={remotes} forcedRooms={forced}");
        _frames.Clear();
    }

    /// <summary>Test helper for case board sync: pin one found evidence item via the game's own PinToCasePanel.</summary>
    static void PinTestEvidence()
    {
        var panel = CasePanelController.Instance;
        var dict = GameplayController.Instance?.evidenceDictionary;
        if (panel?.activeCase == null || dict == null) { Plugin.Event("Ctrl+F11: no active case"); return; }
        foreach (var kv in dict)
        {
            var ev = kv.Value;
            if (ev == null || !ev.isFound) continue;
            var keys = new Il2CppSystem.Collections.Generic.List<Evidence.DataKey>();
            keys.Add(Evidence.DataKey.name);
            panel.PinToCasePanel(panel.activeCase, ev, keys, true, default, false);
            Plugin.Event($"Ctrl+F11: pinned '{kv.Key}' to case {panel.activeCase.id} ({panel.activeCase.caseElements.Count} pins now)");
            return;
        }
    }

    /// <summary>Test helper for B4: start a conversation the way the game does (NewAIController.TalkTo).</summary>
    static void TalkToNearestCitizen()
    {
        var me = Player.Instance;
        var citizens = CityData.Instance?.citizenDirectory;
        if (me == null || citizens == null) return;
        Citizen best = null;
        float bestD = float.MaxValue;
        for (int i = 0; i < citizens.Count; i++)
        {
            var c = citizens[i];
            if (c == null || c.isDead || c.ai == null || PlayerRegistry.IsRemoteBody(c)) continue;
            float d = Vector3.Distance(c.transform.position, me.transform.position);
            if (d < bestD) { bestD = d; best = c; }
        }
        if (best == null) return;
        Plugin.Event($"Ctrl+Insert: talking to citizen {best.humanID} at {bestD:F1} m");
        best.ai.TalkTo(InteractionController.ConversationType.normal);
    }

    // --- Steam transport self-test: checks SendMessageToUser / ReceiveMessagesOnChannel / struct offsets ---
    SteamTransport _steamTest;
    float _steamTestUntil;
    byte[] _steamTestPayload;

    void SteamLoopbackTest()
    {
        try
        {
            _steamTest?.Dispose();
            _steamTest = new SteamTransport();
            _steamTestPayload = Protocol.Hello(new HelloMsg { ProtocolVersion = Protocol.Version, GameVersion = "loopback", ModVersion = NetSession.ModVersion, Name = "self" });
            _steamTest.Send(_steamTest.Self, _steamTestPayload, true);
            _steamTest.Send(_steamTest.Self, Protocol.Ping(), false);
            _steamTestUntil = Time.unscaledTime + 5f;
            Plugin.Event($"Ctrl+Home: sent 2 messages to ourselves ({_steamTest.Self}), waiting up to 5 s");
        }
        catch (Exception e) { Plugin.Event($"Ctrl+Home: Steam loopback failed to start: {e.Message}"); _steamTest = null; }
    }

    void PollSteamLoopback()
    {
        if (_steamTest == null) return;
        while (_steamTest.TryReceive(out var from, out var data))
        {
            var type = Protocol.PeekType(data);
            string detail = type == MsgType.Hello ? $"name='{Protocol.ReadHello(data).Name}' identical={data.AsSpan().SequenceEqual(_steamTestPayload)}" : "";
            Plugin.Event($"Ctrl+Home: received {type} ({data.Length} bytes) from {from} {detail}");
        }
        if (Time.unscaledTime > _steamTestUntil)
        {
            // Also exercises the diagnostics and the friend search used by a real two-machine session.
            Plugin.Event($"Ctrl+Home: session with self: {_steamTest.Describe(_steamTest.Self, out _)}; relay={_steamTest.RelayStatus}");
            try
            {
                using var lobby = new SteamLobby();
                Plugin.Event($"Ctrl+Home: friends hosting right now: {lobby.FriendLobbies(requestMissing: false).Count}; " +
                             $"command-line lobby: {SteamLobby.LobbyFromCommandLine()}; parse test: {SteamLobby.ParseLobbyArg("+connect_lobby 109775241234567890")}");
            }
            catch (Exception e) { Plugin.Event($"Ctrl+Home: friend search failed: {e.Message}"); }
            // Fake "Join game" for a lobby that doesn't exist: expect the join to start, fail and close itself.
            if (NetSession.Current == null) SteamJoinRequests.SelfTest(109775240000000001UL);
            Plugin.Event("Ctrl+Home: loopback test finished");
            _steamTest = null;
        }
    }

    static NetPlayer FirstRemote()
    {
        foreach (var p in PlayerRegistry.All) if (!p.IsLocal) return p;
        return null;
    }

    void AddGhostPlayer()
    {
        if (FirstRemote() != null) { Plugin.Event("Insert: a remote player already exists"); return; }
        var id = PlayerRegistry.NextFreeId();
        if (id == null) { Plugin.Event($"Insert: session full ({PlayerRegistry.MaxPlayers})"); return; }

        var donor = RemotePlayerBody.PickDonor(Player.Instance.transform.position);
        if (donor == null) { Plugin.Event("Insert: no suitable donor citizen"); return; }

        var p = PlayerRegistry.Add(id.Value, "Ghost", isLocal: false);
        var body = RemotePlayerBody.Possess(p, donor);
        _ghost = new GhostReplaySource(GhostDelay, GhostOffset);
        body.PoseSource = _ghost;

        // Start right next to the local player.
        body.Apply(PlayerPose.FromHuman(Player.Instance, Time.unscaledTime) with
        {
            Position = Player.Instance.transform.position + GhostOffset,
        });
        Plugin.Event($"Insert: {p} added as ghost (replays your movement {GhostDelay}s behind)");
    }

    void ToggleFreeze()
    {
        var p = FirstRemote();
        if (p?.RemoteBody == null) return;
        if (p.RemoteBody.PoseSource == null)
        {
            _ghost = new GhostReplaySource(GhostDelay, GhostOffset);
            p.RemoteBody.PoseSource = _ghost;
            Plugin.Event($"Home: {p} follows again");
        }
        else
        {
            p.RemoteBody.PoseSource = null;
            _ghost = null;
            Plugin.Event($"Home: {p} frozen");
        }
    }

    void TrespassTest()
    {
        var p = FirstRemote();
        if (p?.RemoteBody?.Human == null) { Plugin.Event("End: add a remote player first (Insert)"); return; }
        var body = p.RemoteBody;

        // Find a home with an awake resident inside, away from the local player.
        var citizens = CityData.Instance.citizenDirectory;
        var playerPos = Player.Instance.transform.position;
        Citizen resident = null;
        for (int i = 0; i < citizens.Count; i++)
        {
            var c = citizens[i];
            if (c == null || c.Pointer == body.Human.Pointer || c.isDead || c.isAsleep || c.isOnStreet) continue;
            if (c.currentRoom == null || c.home == null || c.currentGameLocation == null) continue;
            if (c.currentGameLocation.Pointer != c.home.Pointer) continue; // at home
            float d = Vector3.Distance(c.transform.position, playerPos);
            if (d > 40f && d < 200f) { resident = c; break; }
        }
        if (resident == null) { Plugin.Event("End: no awake resident at home found"); return; }

        var node = body.Human.FindSafeTeleport(resident.currentRoom, false);
        if (node == null) { Plugin.Event("End: no safe node in that room"); return; }

        _ghost = null;
        body.PoseSource = new FixedPoseSource(new PlayerPose { Time = Time.unscaledTime, Position = node.position, Yaw = 0f });
        Plugin.Event($"End: {p} placed in home of citizen {resident.humanID} \"{resident.citizenName}\" " +
                     $"room={resident.currentRoom.roomID}; watching for SEES-ILLEGAL / PURSUED-BY / TRESPASS");
    }

    void RemoveRemotePlayers()
    {
        var p = FirstRemote();
        if (p == null) return;
        p.RemoteBody?.Release();
        PlayerRegistry.Remove(p.Id);
        _ghost = null;
        Plugin.Event($"Delete: removed {p}");
    }
}
