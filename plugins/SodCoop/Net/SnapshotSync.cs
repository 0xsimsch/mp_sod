using System;
using System.Collections.Generic;
using System.IO;
using SodCoop.Core;

namespace SodCoop.Net;

/// <summary>
/// B1, mid-game join. When a client is welcomed, the host captures a save-state snapshot during play
/// (<c>SaveStateController.CaptureSaveStateAsync</c>: ~0.23 s, ~1 MB, no pause) and streams it in 16 KB
/// chunks. The client reassembles it, checks size and hash, and writes it to its saves folder as
/// "MP Join.sodb", next to the player's own saves and never over them. Loading it is then the game's own
/// loaded automatically (see <see cref="LoadJoinSave"/>), and the client's own money and inventory are kept
/// through <see cref="Profiles"/>.
/// </summary>
public static class SnapshotSync
{
    const int ChunkSize = 16 * 1024;
    public const string JoinSaveName = "MP Join";

    // host
    static Il2CppSystem.Threading.Tasks.Task _capture;
    static string _capturePath;
    static readonly List<(Func<byte[], bool> send, Func<int> pending)> _waiting = new();
    /// <summary>
    /// Outgoing chunks, paced: a 1 MB burst overflows socket buffers (seen with UDP), and Steam refuses
    /// reliable messages once its 512 KB send buffer is full (first two-machine test: the relay sends
    /// ~256 KB/s, the refused chunks were lost and the client never completed the snapshot).
    /// </summary>
    static readonly Queue<(Func<byte[], bool> send, Func<int> pending, byte[] packet)> _outbox = new();
    const int ChunksPerFrame = 16;
    /// <summary>Stop handing Steam chunks while this much reliable data is still queued or unacknowledged.</summary>
    const int MaxPendingBytes = 2 * 1024 * 1024; // well under the 8 MB send buffer SteamTransport sets
    static int _outboxTotal;

    // client
    static byte[] _buffer;
    static int _received;
    static uint _hash;

    public static string LastStatus { get; private set; } = "";

    static readonly List<Action> _captured = new();

    /// <summary>Host: snapshot for a new client (one capture serves everyone waiting).</summary>
    public static void RequestFor(Func<byte[], bool> send, Func<int> pending, Action captured = null)
    {
        _waiting.Add((send, pending));
        if (captured != null) _captured.Add(captured);
        if (_capture != null || SaveStateController.Instance == null) return;
        _capturePath = Path.Combine(BepInEx.Paths.BepInExRootPath, "mp_sod_join_snapshot.sod");
        _capture = SaveStateController.Instance.CaptureSaveStateAsync(_capturePath, true);
        Plugin.Event("SNAPSHOT capturing for a joining client");
    }

    /// <summary>Host, per frame.</summary>
    public static void Poll()
    {
        for (int i = 0; i < ChunksPerFrame && _outbox.Count > 0; i++)
        {
            var (send, pending, packet) = _outbox.Peek();
            if (pending() > MaxPendingBytes || !send(packet)) break; // retry next frame
            _outbox.Dequeue();
            if (_outbox.Count == 0) { LastStatus = $"sent all {_outboxTotal} chunks"; Plugin.Event($"SNAPSHOT {LastStatus}"); }
            else if (_outbox.Count % 32 == 0) LastStatus = $"sending, {_outboxTotal - _outbox.Count}/{_outboxTotal} chunks handed to the transport";
        }
        if (_capture == null || !_capture.IsCompleted) return;
        _capture = null;
        // From here on, what happens on the host isn't in the save the clients load: NetSession holds it for them.
        foreach (var c in _captured) { try { c(); } catch (Exception e) { Plugin.Event($"SNAPSHOT capture callback failed: {e.Message}"); } }
        _captured.Clear();
        var file = _capturePath + "b"; // the game appends 'b' for the compressed form
        if (!File.Exists(file)) file = _capturePath;
        if (!File.Exists(file)) { Plugin.Event("SNAPSHOT capture produced no file"); _waiting.Clear(); return; }
        var bytes = File.ReadAllBytes(file);
        try { File.Delete(file); } catch { /* temp file */ }
        var city = HostCityFile();
        byte[] cityBytes = city != null ? File.ReadAllBytes(city) : null;
        foreach (var (send, pending) in _waiting)
        {
            // The city first, so it's there when the save loads.
            if (cityBytes != null) Enqueue(send, pending, 1, Path.GetFileName(city), cityBytes);
            Enqueue(send, pending, 0, "", bytes);
        }
        _outboxTotal = _outbox.Count;
        LastStatus = $"sending {bytes.Length / 1024} KB" + (cityBytes != null ? $" + city {cityBytes.Length / 1024} KB" : "") +
                     $" to {_waiting.Count} client(s), {_outboxTotal} chunks";
        Plugin.Event($"SNAPSHOT {LastStatus}");
        _waiting.Clear();
    }

    static void Enqueue(Func<byte[], bool> send, Func<int> pending, byte kind, string name, byte[] bytes)
    {
        uint hash = Protocol.Fnv1a(bytes);
        for (int off = 0; off < bytes.Length; off += ChunkSize)
        {
            var part = new byte[Math.Min(ChunkSize, bytes.Length - off)];
            Buffer.BlockCopy(bytes, off, part, 0, part.Length);
            _outbox.Enqueue((send, pending, Protocol.SnapshotChunk(new SnapshotChunkMsg { Kind = kind, Name = name, Total = bytes.Length, Offset = off, Hash = hash, Data = part })));
        }
    }

    /// <summary>
    /// The host's city file if the client may not have it: built-in cities ship with the game
    /// (StreamingAssets/Cities, e.g. "Casablanca Quarter"); generated ones only exist in the user's
    /// Cities folder as "&lt;name&gt;.&lt;size&gt;.&lt;version&gt;.&lt;seed&gt;.citb".
    /// </summary>
    static string HostCityFile()
    {
        var c = CityData.Instance;
        if (c == null || string.IsNullOrEmpty(c.seed)) return null;
        string pattern = $"{c.cityName}.*.{c.seed}.cit*";
        var builtIn = Path.Combine(UnityEngine.Application.streamingAssetsPath, "Cities");
        if (Directory.Exists(builtIn) && Directory.GetFiles(builtIn, pattern).Length > 0) return null;
        var mine = Path.Combine(UnityEngine.Application.persistentDataPath, "Cities");
        if (!Directory.Exists(mine)) return null;
        var files = Directory.GetFiles(mine, pattern);
        return files.Length > 0 ? files[0] : null;
    }

    // client: city file being received
    static byte[] _cityBuffer;
    static int _cityReceived;

    /// <summary>The session whose world we already loaded: a second copy would reload and destroy every borrowed body.</summary>
    static NetSession _loadedFor;
    static bool _duplicateLogged;

    /// <summary>Client: one chunk arrived.</summary>
    public static void OnChunk(SnapshotChunkMsg m)
    {
        if (_loadedFor != null && _loadedFor == NetSession.Current)
        {
            if (!_duplicateLogged) Plugin.Event("SNAPSHOT ignoring a second copy of the host's world (already loaded in this session)");
            _duplicateLogged = true;
            return;
        }
        if (m.Kind == 1) { OnCityChunk(m); return; }
        if (_buffer == null || _buffer.Length != m.Total || _hash != m.Hash)
        {
            _buffer = new byte[m.Total];
            _received = 0;
            _hash = m.Hash;
        }
        if (m.Offset < 0 || m.Offset + m.Data.Length > _buffer.Length) return;
        Buffer.BlockCopy(m.Data, 0, _buffer, m.Offset, m.Data.Length);
        int before = _received * 4 / _buffer.Length;
        _received += m.Data.Length;
        LastStatus = $"receiving the host's world, {_received / 1024}/{_buffer.Length / 1024} KB";
        if (_received < _buffer.Length)
        {
            if (_received * 4 / _buffer.Length != before) Plugin.Event($"SNAPSHOT {LastStatus}");
            return;
        }

        var bytes = _buffer;
        _buffer = null;
        if (Protocol.Fnv1a(bytes) != _hash) { LastStatus = "snapshot hash mismatch"; Plugin.Event($"SNAPSHOT {LastStatus}"); return; }
        var dir = Path.Combine(UnityEngine.Application.persistentDataPath, "Save");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, JoinSaveName + ".sodb");
        File.WriteAllBytes(path, bytes);
        LastStatus = $"received {bytes.Length / 1024} KB → {path}";
        Plugin.Event($"SNAPSHOT {LastStatus}");
        LoadJoinSave(path);
    }

    static void OnCityChunk(SnapshotChunkMsg m)
    {
        if (_cityBuffer == null || _cityBuffer.Length != m.Total) { _cityBuffer = new byte[m.Total]; _cityReceived = 0; }
        if (m.Offset < 0 || m.Offset + m.Data.Length > _cityBuffer.Length) return;
        Buffer.BlockCopy(m.Data, 0, _cityBuffer, m.Offset, m.Data.Length);
        if (_cityReceived == 0) Plugin.Event($"SNAPSHOT receiving the host's city {m.Name} ({m.Total / 1024} KB)");
        _cityReceived += m.Data.Length;
        LastStatus = $"receiving the host's city, {_cityReceived / 1024}/{_cityBuffer.Length / 1024} KB";
        if (_cityReceived < _cityBuffer.Length) return;
        var bytes = _cityBuffer;
        _cityBuffer = null;
        var name = Path.GetFileName(m.Name ?? "");
        if (name.Length == 0 || Protocol.Fnv1a(bytes) != m.Hash) { Plugin.Event("SNAPSHOT city file corrupt, skipped"); return; }
        var dir = Path.Combine(UnityEngine.Application.persistentDataPath, "Cities");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        if (File.Exists(path)) { Plugin.Event($"SNAPSHOT city {name}: already have it"); return; }
        File.WriteAllBytes(path, bytes);
        Plugin.Event($"SNAPSHOT city {name}: received {bytes.Length / 1024} KB (the host's generated city)");
    }

    /// <summary>
    /// Design: inventory is per player. The loaded Player holds the *host's* items (the same world objects the
    /// host holds), so empty our non-static slots (tools like watch/fists/coin stay) without reporting it, and
    /// hand those items to the host's body here, which is who really holds them.
    /// </summary>
    static void GiveBackHostInventory()
    {
        var fpc = FirstPersonItemController.Instance;
        if (fpc?.slots == null) return;
        int moved = 0;
        using var quiet = ItemSync.Suppress();
        for (int i = 0; i < fpc.slots.Count; i++)
        {
            var slot = fpc.slots[i];
            if (slot == null || slot.isStatic != FirstPersonItemController.InventorySlot.StaticSlot.nonStatic) continue;
            var it = slot.GetInteractable();
            if (it == null) continue;
            fpc.EmptySlot(slot, false, false, false, false);
            _hostItems.Add(it.id);
            moved++;
        }
        if (moved > 0) Plugin.Event($"SNAPSHOT emptied {moved} slot(s) holding the host's items (inventory is per player)");
        HandOverHostItems();
    }

    static readonly List<int> _hostItems = new();

    /// <summary>Put the host's items into the host body's inventory once that body exists here.</summary>
    public static void HandOverHostItems()
    {
        if (_hostItems.Count == 0 || !PlayerRegistry.TryGet(PlayerId.Host, out var host) || host.Body == null) return;
        var dict = CityData.Instance?.savableInteractableDictionary;
        if (dict == null) return;
        using var quiet = ItemSync.Suppress();
        foreach (var id in _hostItems)
            if (dict.TryGetValue(id, out var it) && it != null) it.SetInInventory(host.Body);
        Plugin.Event($"SNAPSHOT {_hostItems.Count} item(s) handed to the host's body");
        _hostItems.Clear();
    }

    // The garbage collector during the join load. Seen 3 times (2026-10-16/17): the client froze at "loading the host's
    // world…" behind a Windows message box "SuspendThread loop failed": IL2CPP's Boehm GC must suspend every thread to
    // collect, and under Wine/Proton suspending a thread that sits in a system call (the loading threads read files)
    // fails; the GC gives up with that box and the game waits on it forever. Loading allocates a lot, so a collection
    // during the load was likely. No collections while the host's world loads: the GC is off from the switch to the
    // loading screen until the world runs (at most GcMaxPause seconds, or when the session resets).
    static float _gcPausedAt = -1f;
    const float GcMaxPause = 180f;

    static void PauseGc()
    {
        try
        {
            UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Disabled;
            _gcPausedAt = UnityEngine.Time.unscaledTime;
            Plugin.Event("SNAPSHOT garbage collector off while the host's world loads");
        }
        catch (Exception e) { Plugin.Event($"SNAPSHOT could not pause the garbage collector: {e.Message}"); }
    }

    public static void ResumeGc(string why)
    {
        if (_gcPausedAt < 0f) return;
        _gcPausedAt = -1f;
        try
        {
            UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
            Plugin.Event($"SNAPSHOT garbage collector on again ({why})");
        }
        catch (Exception e) { Plugin.Event($"SNAPSHOT could not resume the garbage collector: {e.Message}"); }
    }

    static string _pendingDelete;
    static bool _sawLoading;
    static int _keepMoney = -1;

    /// <summary>
    /// What the Load screen's Start leaves behind: <c>RestartSafeController</c> pointing at the save, then the
    /// <c>loadingCity</c> menu component, whose switch-complete handler calls <c>CityConstructor.StartLoading</c>.
    /// </summary>
    static void LoadJoinSave(string path)
    {
        _loadedFor = NetSession.Current;
        _duplicateLogged = false;
        try
        {
            var rs = RestartSafeController.Instance;
            var menu = MainMenuController.Instance;
            if (rs == null || menu == null) { Plugin.Event("SNAPSHOT can't auto-load: no menu; load 'MP Join' by hand"); return; }
            rs.saveStateFileInfo = new Il2CppSystem.IO.FileInfo(path);
            rs.loadSaveGame = true;
            rs.newGameLoadCity = false;
            _pendingDelete = path;
            _sawLoading = false;
            // Design: money is per player. The snapshot carries the host's, so keep ours.
            // Joined from the main menu: no money of our own yet, so take the save's.
            bool inWorld = CityData.Instance != null && SessionData.Instance != null && SessionData.Instance.play;
            _keepMoney = inWorld && GameplayController.Instance != null ? GameplayController.Instance.money : -1;
            Profiles.CaptureBeforeJoin(); // our own money, upgrades, items… before the host's save replaces them
            NetSession.Current?.PrepareForWorldReload(); // borrowed bodies belong to the old world
            PauseGc();
            menu.EnableMainMenu(true, false, false, MainMenuController.Component.loadingCity);
            menu.SetMenuComponent(MainMenuController.Component.loadingCity);
            Plugin.Event("SNAPSHOT loading the host's world…");
        }
        catch (Exception e) { Plugin.Event($"SNAPSHOT auto-load failed ({e.Message}); load 'MP Join' by hand"); }
    }

    /// <summary>Per frame on clients: once the joined world runs, remove the temporary save again.</summary>
    public static void CleanupAfterLoad()
    {
        if (_gcPausedAt >= 0f && UnityEngine.Time.unscaledTime - _gcPausedAt > GcMaxPause) ResumeGc("the load is taking too long");
        if (_pendingDelete == null) return;
        // Wait for the load to actually happen (the old world is still running when it starts).
        if (SessionData.Instance == null || !SessionData.Instance.play || CityData.Instance == null) { _sawLoading = true; return; }
        if (!_sawLoading) return;
        if (_keepMoney >= 0 && GameplayController.Instance != null)
        {
            int hostMoney = GameplayController.Instance.money;
            GameplayController.Instance.SetMoney(_keepMoney);
            Plugin.Event($"SNAPSHOT kept our own money: {_keepMoney} (the host's save had {hostMoney})");
            _keepMoney = -1;
        }
        GiveBackHostInventory();
        Profiles.RestoreAfterJoin();
        ResumeGc("the joined world is running");
        NetSession.Current?.SendWorldLoaded();
        try { File.Delete(_pendingDelete); Plugin.Event("SNAPSHOT joined world is running; removed the temporary 'MP Join' save"); }
        catch (Exception e) { Plugin.Event($"SNAPSHOT could not remove {_pendingDelete}: {e.Message}"); }
        _pendingDelete = null;
    }
}
