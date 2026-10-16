using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SodCoop.Net;

// Wire format. Pure .NET (no Unity types) so it can be unit tested and spoken by test tools
// (analysis/tools/fake_client.py). Every message: [magic u16 'SM'][type u8][payload]. Little endian.

public enum MsgType : byte
{
    /// <summary>client → host: join request.</summary>
    Hello = 1,
    /// <summary>host → client: accepted, here is your id.</summary>
    Welcome = 2,
    /// <summary>host → client: refused (version mismatch, session full, …).</summary>
    Reject = 3,
    /// <summary>both ways, unreliable: a player's body pose. Host relays client poses to the others.</summary>
    Pose = 10,
    /// <summary>client → host: the client started/stopped an illegal action (lockpicking, searching, …).</summary>
    IllegalAction = 11,
    /// <summary>host → client: a citizen hit your body on the host; apply it to your own Player.</summary>
    Damage = 12,
    /// <summary>both ways: a door moved or was (un)locked. Client → host for its own actions; host → clients for all.</summary>
    DoorState = 13,
    /// <summary>host → clients, every 2 s: the authoritative clock (design: only the host controls time).</summary>
    TimeSync = 14,
    /// <summary>client → host (relayed): the player was knocked out / recovered (design: KO affects only that player).</summary>
    PlayerKO = 15,
    /// <summary>both ways (host relays): a player found evidence / a fact, or learned something about it (shared case).</summary>
    CaseEvent = 16,
    /// <summary>both ways (host relays): a world interactable was created at runtime (B2).</summary>
    Spawn = 17,
    /// <summary>both ways (host relays): an existing interactable moved, went into/out of an inventory, or was deleted.</summary>
    Item = 18,
    /// <summary>host → client, 10 Hz, unreliable: poses of the citizens near that client (NPC sync).</summary>
    CitizenPoses = 19,
    /// <summary>host → clients: a player left (release its body).</summary>
    PlayerLeft = 21,
    /// <summary>host → clients: a citizen was killed (Human.Murder), replayed so bodies/evidence match.</summary>
    Death = 22,
    /// <summary>host → clients: a murder record (JSON, the game's own serialized form) was created or changed state.</summary>
    MurderRecord = 23,
    /// <summary>host → clients: a case (JSON, the game's save form) and whether it's the active one.</summary>
    CaseRecord = 24,
    /// <summary>host → joining client: one chunk of a save-state snapshot (B1 mid-game join).</summary>
    SnapshotChunk = 25,
    /// <summary>client → host: may I talk to this citizen? (B4: one conversation per citizen, first come first served)</summary>
    TalkRequest = 26,
    /// <summary>host → client: granted or busy.</summary>
    TalkReply = 27,
    /// <summary>client → host: conversation over.</summary>
    TalkEnd = 28,
    /// <summary>both ways (host relays): a fingerprint/footprint interactable was created (revealed).</summary>
    Forensic = 29,
    /// <summary>both ways: leaving.</summary>
    Bye = 20,
    /// <summary>host → new lobby member: opens the Steam messaging session from the host side.</summary>
    Ping = 30,
    /// <summary>client → host: this player's own menu/case board has its game paused, or not (the world pauses only when all are).</summary>
    PauseState = 31,
    /// <summary>both ways (host relays): a side job in the game's save form (JSON) plus its concrete type.</summary>
    JobRecord = 32,
    /// <summary>client → host: the player took this job's paper; the host accepts the job for everyone.</summary>
    JobAccept = 33,
    /// <summary>client → host: the client hit a citizen; the host applies it (KO, injuries) and the result comes back with the citizen sync.</summary>
    CitizenDamage = 34,
    /// <summary>both ways (host relays): world state — weather, citizen-made switches, alarms, breakers, windows, door jam/tape/barge (WorldSync).</summary>
    WorldEvent = 35,
    /// <summary>host → client: a driven citizen's attack/throw/block/abort, speech or bark (CitizenEventSync).</summary>
    CitizenEvent = 37,
    /// <summary>host → client: driven citizens' reaction toward players, alertness, outfit, combat/weapon, held props.</summary>
    CitizenStates = 38,
    /// <summary>ExtraWire (ObjectiveSync, StatusSync, Notify): objectives, case resolution, player status, notices.</summary>
    ObjectiveSync = 39, CaseResolve = 40, PlayerStatus = 41, Notice = 42,
    /// <summary>host → clients: which citizen stands for which player (every machine borrows the same ones; see Puppet/Doubles).</summary>
    BodyMap = 43,
    /// <summary>both ways: telephones (PhoneSync). Host → clients: calls created/answered/ended; client → host: the player answered, hung up, dialled.</summary>
    Phone = 44,
    /// <summary>client → host: the client chose a dialog option; the host replays it with the client's result (DialogSync).</summary>
    DialogExecute = 45,
    /// <summary>client → host: a dialog ended (job/murder end-of-dialog effects) (DialogSync).</summary>
    DialogEnd = 46,
    /// <summary>client → host: the client found the current murder victim; the host opens the murder case (CaseIds).</summary>
    VictimDiscovered = 47,
    /// <summary>both ways (host relays): a blood spatter (BloodSync).</summary>
    /// <summary>both ways: elevators (ElevatorSync). Client → host: a button press; host → clients: calls and car state.</summary>
    Lift = 48,
    /// <summary>client → host: a gadget effect on the host's citizens: restrain/handcuff, grenade/flash bomb/incapacitator going off (GadgetSync).</summary>
    Gadget = 49,
    Spatter = 50,
    /// <summary>host → clients: the newspaper (NewsSync).</summary>
    Newspaper = 51,
    /// <summary>client → host: the client's rest state (sleeping/waiting/KO'd) (RestSync).</summary>
    RestState = 52,
}

public struct Vec3
{
    public float X, Y, Z;
    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public override string ToString() => $"({X:F1}, {Y:F1}, {Z:F1})";
}

public struct HelloMsg
{
    public ushort ProtocolVersion;
    public string GameVersion;
    public string ModVersion;
    public string Name;
}

public struct WelcomeMsg
{
    public byte PlayerId;
    public byte MaxPlayers;
    /// <summary>City share code the host is playing; the client must load the same city.</summary>
    public string CityShareCode;
}

public struct RejectMsg
{
    public string Reason;
}

[Flags]
public enum PoseFlags : byte
{
    None = 0,
    Crouched = 1,
    Running = 2,
    Moving = 4,
    /// <summary>Citizens only: knocked out.</summary>
    KnockedOut = 8,
    /// <summary>Citizens only: lying in a bed (<c>Actor.isInBed</c>)…</summary>
    InBed = 16,
    /// <summary>…a low bed / sofa (<c>Actor.isInLowBed</c>).</summary>
    LowBed = 32,
    /// <summary>Citizens only: sit/lie mirrored to the right (<c>CitizenAnimationController.flipToRightAnimation</c>).</summary>
    FlipRight = 64,
    /// <summary>Citizens only: asleep.</summary>
    Asleep = 128,

    // Player poses reuse the citizen-only bits above:
    /// <summary>Players only: hiding (closet, under a bed).</summary>
    PlayerHiding = 16,
    /// <summary>Players only: crawling in an air vent.</summary>
    PlayerInVent = 32,
    /// <summary>Players only: flashlight on.</summary>
    PlayerFlashlight = 64,
    /// <summary>Players only: sitting (locked into a seat, eyes lowered).</summary>
    PlayerSitting = 8,
    /// <summary>Players only: lying down (bed).</summary>
    PlayerLying = 128,
}

public struct PoseMsg
{
    public byte PlayerId;
    /// <summary>Sender's sequence number; receivers drop older poses (unreliable channel can reorder).</summary>
    public ushort Seq;
    /// <summary>Sender's clock (seconds); used to space out interpolation.</summary>
    public float Time;
    public Vec3 Position;
    public float Yaw;
    /// <summary>Camera pitch in degrees (positive = looking down).</summary>
    public float Pitch;
    public PoseFlags Flags;
    /// <summary>Camera yaw in degrees (absolute; the body yaw is <see cref="Yaw"/>).</summary>
    public float LookYaw;
    /// <summary>World interactable in the player's hand or carried, -1 if none.</summary>
    public int HeldId;
    public HeldKind Held;
    /// <summary>What the player is doing with its hands/body (shown with the nearest citizen animation).</summary>
    public PlayerActivity Activity;
    /// <summary>Punches/weapon swings so far: low 7 bits count (wrapping), bit 7 = the last one was with a weapon.</summary>
    public byte Swings;
}

public enum PlayerActivity : byte
{
    None = 0,
    Lockpicking = 1,
    Typing = 2,
    Phone = 3,
    Consuming = 4,
    Smoking = 5,
    Reading = 6,
    Searching = 7,
}

public enum HeldKind : byte
{
    None = 0,
    /// <summary>An item from the inventory, drawn.</summary>
    Item = 1,
    /// <summary>A physics object carried in both hands.</summary>
    Carried = 2,
    /// <summary>A drawn item raised to aim/use.</summary>
    Raised = 3,
}

public struct IllegalActionMsg
{
    public byte PlayerId;
    public bool Active;
}

public struct DamageMsg
{
    public byte PlayerId;
    public float Amount;
    /// <summary>humanID of the attacker on the host, -1 if none.</summary>
    public int AttackerHumanId;
    public Vec3 Position;
    public Vec3 Direction;
}

public struct DoorStateMsg
{
    /// <summary>Key in <c>CityData.doorDictionary</c> (identical on every machine: generation-time).</summary>
    public int DoorKey;
    public float Ajar;
    public bool Locked;
    /// <summary>Player who caused it, 255 = a citizen / the world.</summary>
    public byte ByPlayer;
}

public struct TimeSyncMsg
{
    public float GameTime;
    public byte TimeSpeed;
    public int LeapYearCycle;
    /// <summary>Design: a host pause pauses everyone.</summary>
    public bool Paused;
}

public struct PlayerKOMsg
{
    public byte PlayerId;
    public bool KnockedOut;
}

public enum CaseEventKind : byte
{
    /// <summary>CaseComponent.SetFound(true) on an Evidence; Id = evID.</summary>
    EvidenceFound = 1,
    /// <summary>CaseComponent.SetFound(true) on a Fact; Id = Fact.GetIdentifier().</summary>
    FactFound = 2,
    /// <summary>Evidence.AddDiscovery(A); Id = evID, A = Evidence.Discovery.</summary>
    Discovery = 3,
    /// <summary>Evidence.MergeDataKeys(A, B); Id = evID, A/B = Evidence.DataKey.</summary>
    MergeKeys = 4,
    /// <summary>Evidence.SetNote: player note text. Id = evID ␟ data keys (comma list) ␟ text.</summary>
    NoteText = 5,
    /// <summary>A player sticky note was created; Id = its evID ("StickyNote&lt;n&gt;").</summary>
    StickyNote = 6,
    /// <summary>A player-drawn string (FactCustom "CustomLink"). Id = from evID ␟ to evID ␟ from keys ␟ to keys; A = forceDiscovery.</summary>
    CustomLink = 7,
    /// <summary>FactCustom.SetCustomName on a player-drawn string. Id = from evID ␟ to evID ␟ name.</summary>
    CustomLinkName = 8,
}

public struct CaseEventMsg
{
    public byte ByPlayer;
    public CaseEventKind Kind;
    public string Id;
    public int A;
    public int B;
}

public struct PassedVar
{
    public byte Type;
    public float Value;
    public string Str;
}

public struct SpawnMsg
{
    public byte ByPlayer;
    /// <summary>Interactable.id the creator assigned; the receiver forces the same id.</summary>
    public int Id;
    public string Preset;
    /// <summary>humanIDs, -1 = none.</summary>
    public int BelongsTo, Writer, Receiver;
    public Vec3 Position;
    public Vec3 Euler;
    public string DdsOverride;
    public PassedVar[] Passed;
}

public enum ItemEventKind : byte
{
    Move = 1,
    IntoInventory = 2,
    OutOfInventory = 3,
    Delete = 4,
    /// <summary>An on/off state (light, TV, tap, drawer, lock…): Holder = (int)InteractablePreset.Switch, HolderIsPlayer = the new value.</summary>
    Switch = 5,
    /// <summary>Thrown or dropped by a player: Position/Euler = start, Velocity/Spin = rigidbody state. The resting place follows as a Move.</summary>
    Throw = 6,
}

public struct ItemMsg
{
    public byte ByPlayer;
    public ItemEventKind Kind;
    public int Id;
    public Vec3 Position;
    public Vec3 Euler;
    /// <summary>IntoInventory: holder. Player id if <see cref="HolderIsPlayer"/>, else a citizen humanID.</summary>
    public int Holder;
    public bool HolderIsPlayer;
    /// <summary>Throw only.</summary>
    public Vec3 Velocity, Spin;
}

public struct CitizenPose
{
    public int HumanId;
    public Vec3 Position;
    public float Yaw;
    public PoseFlags Flags;
    /// <summary>CitizenAnimationController.IdleAnimationState (sitting, sweeping, phone, …).</summary>
    public byte Idle;
    /// <summary>CitizenAnimationController.ArmsBoolSate (typing, smoking, reading, cuffed, …).</summary>
    public byte Arms;
}

public struct CitizenPosesMsg
{
    public ushort Seq;
    public float Time;
    public CitizenPose[] Citizens;
}

public struct DeathMsg
{
    public int VictimId;
    /// <summary>Citizen humanID; -1 = none; ≤ -2 = player (id = -KillerId - 2), since every machine's own Player shares one humanID.</summary>
    public int KillerId;
    /// <summary>MurderController.Murder.murderID or -1.</summary>
    public int MurderId;
    /// <summary>Weapon Interactable.id or -1.</summary>
    public int WeaponId;
    /// <summary>Where the victim was on the host when killed (far from a client, the client's own AI had them elsewhere).</summary>
    public Vec3 Position;
    public float Yaw;
}

public struct SnapshotChunkMsg
{
    /// <summary>0 = save-state snapshot, 1 = the host's generated city file (.citb).</summary>
    public byte Kind;
    /// <summary>City file name (kind 1); empty for the save.</summary>
    public string Name;
    public int Total;
    public int Offset;
    /// <summary>FNV-1a of the whole file, so the client can check it.</summary>
    public uint Hash;
    public byte[] Data;
}

public struct ForensicMsg
{
    public byte ByPlayer;
    /// <summary>1 = fingerprint, 2 = footprint.</summary>
    public byte Kind;
    public int Id;
    /// <summary>humanID, -1 none, ≤ -2 player (see DeathMsg).</summary>
    public int BelongsTo;
    public Vec3 Position;
    public Vec3 Euler;
    /// <summary>FingerprintScannerController.Print / GameplayController.Footprint as JSON.</summary>
    public string Json;
}

public static class Protocol
{
    /// <summary>Bump on any wire change. Hosts reject other versions in the handshake.</summary>
    public const ushort Version = 38;
    const ushort Magic = 0x4D53; // "SM"

    public static byte[] Hello(HelloMsg m) => Write(MsgType.Hello, w =>
    {
        w.Write(m.ProtocolVersion);
        Str(w, m.GameVersion); Str(w, m.ModVersion); Str(w, m.Name);
    });

    public static byte[] Welcome(WelcomeMsg m) => Write(MsgType.Welcome, w =>
    {
        w.Write(m.PlayerId); w.Write(m.MaxPlayers); Str(w, m.CityShareCode);
    });

    public static byte[] Reject(RejectMsg m) => Write(MsgType.Reject, w => Str(w, m.Reason));

    public static byte[] Pose(PoseMsg m)
    {
        var copy = m;
        return Write(MsgType.Pose, w =>
        {
            w.Write(copy.PlayerId); w.Write(copy.Seq); w.Write(copy.Time);
            V(w, copy.Position); w.Write(copy.Yaw); w.Write(copy.Pitch); w.Write((byte)copy.Flags);
            w.Write(copy.LookYaw); w.Write(copy.HeldId); w.Write((byte)copy.Held); w.Write((byte)copy.Activity);
            w.Write(copy.Swings);
        });
    }

    public static byte[] IllegalAction(IllegalActionMsg m)
    {
        var copy = m;
        return Write(MsgType.IllegalAction, w => { w.Write(copy.PlayerId); w.Write(copy.Active); });
    }

    public static byte[] Damage(DamageMsg m) => DamageOf(MsgType.Damage, m);

    /// <summary>client → host: the client's player hit a citizen. PlayerId = attacker, AttackerHumanId = the citizen hit.</summary>
    public static byte[] CitizenDamage(DamageMsg m) => DamageOf(MsgType.CitizenDamage, m);

    static byte[] DamageOf(MsgType type, DamageMsg m)
    {
        var copy = m;
        return Write(type, w =>
        {
            w.Write(copy.PlayerId); w.Write(copy.Amount); w.Write(copy.AttackerHumanId);
            V(w, copy.Position); V(w, copy.Direction);
        });
    }

    public static byte[] DoorState(DoorStateMsg m) => Write(MsgType.DoorState, w =>
    {
        w.Write(m.DoorKey); w.Write(m.Ajar); w.Write(m.Locked); w.Write(m.ByPlayer);
    });

    public static DoorStateMsg ReadDoorState(byte[] d) => Read(d, r => new DoorStateMsg
    {
        DoorKey = r.ReadInt32(), Ajar = r.ReadSingle(), Locked = r.ReadBoolean(), ByPlayer = r.ReadByte(),
    });

    public static byte[] TimeSync(TimeSyncMsg m) => Write(MsgType.TimeSync, w =>
    {
        w.Write(m.GameTime); w.Write(m.TimeSpeed); w.Write(m.LeapYearCycle); w.Write(m.Paused);
    });

    public static TimeSyncMsg ReadTimeSync(byte[] d) => Read(d, r => new TimeSyncMsg
    {
        GameTime = r.ReadSingle(), TimeSpeed = r.ReadByte(), LeapYearCycle = r.ReadInt32(), Paused = r.ReadBoolean(),
    });

    public static byte[] PlayerKO(PlayerKOMsg m) => Write(MsgType.PlayerKO, w => { w.Write(m.PlayerId); w.Write(m.KnockedOut); });

    public static byte[] JobRecord(string type, string json) => Write(MsgType.JobRecord, w =>
    {
        Str(w, type);
        var b = Encoding.UTF8.GetBytes(json ?? "");
        w.Write(b.Length);
        w.Write(b);
    });

    public static (string type, string json) ReadJobRecord(byte[] d) => Read(d, r => (Str(r), Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()))));

    public static byte[] BodyMap(IReadOnlyList<(byte player, int humanId)> map) => Write(MsgType.BodyMap, w =>
    {
        w.Write((byte)map.Count);
        foreach (var (player, humanId) in map) { w.Write(player); w.Write(humanId); }
    });

    public static List<(byte player, int humanId)> ReadBodyMap(byte[] d) => Read(d, r =>
    {
        int n = r.ReadByte();
        var list = new List<(byte, int)>(n);
        for (int i = 0; i < n; i++) list.Add((r.ReadByte(), r.ReadInt32()));
        return list;
    });

    public static byte[] VictimDiscovered() => Write(MsgType.VictimDiscovered, w => { });

    public static byte[] JobAccept(int jobId) => Write(MsgType.JobAccept, w => w.Write(jobId));

    public static int ReadJobAccept(byte[] d) => Read(d, r => r.ReadInt32());

    public static byte[] PauseState(bool paused) => Write(MsgType.PauseState, w => w.Write(paused));

    public static bool ReadPauseState(byte[] d) => Read(d, r => r.ReadBoolean());

    public static PlayerKOMsg ReadPlayerKO(byte[] d) => Read(d, r => new PlayerKOMsg { PlayerId = r.ReadByte(), KnockedOut = r.ReadBoolean() });

    public static byte[] CaseEvent(CaseEventMsg m) => Write(MsgType.CaseEvent, w =>
    {
        w.Write(m.ByPlayer); w.Write((byte)m.Kind); Str(w, m.Id); w.Write(m.A); w.Write(m.B);
    });

    public static CaseEventMsg ReadCaseEvent(byte[] d) => Read(d, r => new CaseEventMsg
    {
        ByPlayer = r.ReadByte(), Kind = (CaseEventKind)r.ReadByte(), Id = Str(r), A = r.ReadInt32(), B = r.ReadInt32(),
    });

    public static byte[] Spawn(SpawnMsg m) => Write(MsgType.Spawn, w =>
    {
        w.Write(m.ByPlayer); w.Write(m.Id); Str(w, m.Preset);
        w.Write(m.BelongsTo); w.Write(m.Writer); w.Write(m.Receiver);
        V(w, m.Position); V(w, m.Euler); Str(w, m.DdsOverride);
        var passed = m.Passed ?? Array.Empty<PassedVar>();
        w.Write((byte)Math.Min(passed.Length, 255));
        for (int i = 0; i < passed.Length && i < 255; i++) { w.Write(passed[i].Type); w.Write(passed[i].Value); Str(w, passed[i].Str); }
    });

    public static SpawnMsg ReadSpawn(byte[] d) => Read(d, r =>
    {
        var m = new SpawnMsg
        {
            ByPlayer = r.ReadByte(), Id = r.ReadInt32(), Preset = Str(r),
            BelongsTo = r.ReadInt32(), Writer = r.ReadInt32(), Receiver = r.ReadInt32(),
            Position = V(r), Euler = V(r), DdsOverride = Str(r),
        };
        m.Passed = new PassedVar[r.ReadByte()];
        for (int i = 0; i < m.Passed.Length; i++) m.Passed[i] = new PassedVar { Type = r.ReadByte(), Value = r.ReadSingle(), Str = Str(r) };
        return m;
    });

    public static byte[] Item(ItemMsg m) => Write(MsgType.Item, w =>
    {
        w.Write(m.ByPlayer); w.Write((byte)m.Kind); w.Write(m.Id); V(w, m.Position); V(w, m.Euler); w.Write(m.Holder); w.Write(m.HolderIsPlayer);
        if (m.Kind == ItemEventKind.Throw) { V(w, m.Velocity); V(w, m.Spin); }
    });

    public static ItemMsg ReadItem(byte[] d) => Read(d, r =>
    {
        var m = new ItemMsg
        {
            ByPlayer = r.ReadByte(), Kind = (ItemEventKind)r.ReadByte(), Id = r.ReadInt32(), Position = V(r), Euler = V(r),
            Holder = r.ReadInt32(), HolderIsPlayer = r.ReadBoolean(),
        };
        if (m.Kind == ItemEventKind.Throw) { m.Velocity = V(r); m.Spin = V(r); }
        return m;
    });

    public static byte[] CitizenPoses(CitizenPosesMsg m) => Write(MsgType.CitizenPoses, w =>
    {
        w.Write(m.Seq); w.Write(m.Time);
        var list = m.Citizens ?? Array.Empty<CitizenPose>();
        int n = Math.Min(list.Length, 255);
        w.Write((byte)n);
        for (int i = 0; i < n; i++)
        {
            w.Write(list[i].HumanId); V(w, list[i].Position); w.Write(list[i].Yaw); w.Write((byte)list[i].Flags);
            w.Write(list[i].Idle); w.Write(list[i].Arms);
        }
    });

    public static CitizenPosesMsg ReadCitizenPoses(byte[] d) => Read(d, r =>
    {
        var m = new CitizenPosesMsg { Seq = r.ReadUInt16(), Time = r.ReadSingle() };
        m.Citizens = new CitizenPose[r.ReadByte()];
        for (int i = 0; i < m.Citizens.Length; i++)
            m.Citizens[i] = new CitizenPose
            {
                HumanId = r.ReadInt32(), Position = V(r), Yaw = r.ReadSingle(), Flags = (PoseFlags)r.ReadByte(),
                Idle = r.ReadByte(), Arms = r.ReadByte(),
            };
        return m;
    });

    public static byte[] PlayerLeft(byte playerId) => Write(MsgType.PlayerLeft, w => w.Write(playerId));

    public static byte ReadPlayerLeft(byte[] d) => d[3];

    public static byte[] Death(DeathMsg m) => Write(MsgType.Death, w =>
    {
        w.Write(m.VictimId); w.Write(m.KillerId); w.Write(m.MurderId); w.Write(m.WeaponId); V(w, m.Position); w.Write(m.Yaw);
    });

    public static DeathMsg ReadDeath(byte[] d) => Read(d, r => new DeathMsg
    {
        VictimId = r.ReadInt32(), KillerId = r.ReadInt32(), MurderId = r.ReadInt32(), WeaponId = r.ReadInt32(), Position = V(r), Yaw = r.ReadSingle(),
    });

    public static byte[] MurderRecord(string json) => Write(MsgType.MurderRecord, w =>
    {
        var b = Encoding.UTF8.GetBytes(json ?? "");
        w.Write(b.Length);
        w.Write(b);
    });

    public static string ReadMurderRecord(byte[] d) => Read(d, r => Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32())));

    public static byte[] CaseRecord(string json, bool active) => Write(MsgType.CaseRecord, w =>
    {
        w.Write(active);
        var b = Encoding.UTF8.GetBytes(json ?? "");
        w.Write(b.Length);
        w.Write(b);
    });

    public static (string json, bool active) ReadCaseRecord(byte[] d) => Read(d, r =>
    {
        bool active = r.ReadBoolean();
        return (Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32())), active);
    });

    public static byte[] SnapshotChunk(SnapshotChunkMsg m) => Write(MsgType.SnapshotChunk, w =>
    {
        w.Write(m.Kind); Str(w, m.Name);
        w.Write(m.Total); w.Write(m.Offset); w.Write(m.Hash); w.Write(m.Data.Length); w.Write(m.Data);
    });

    public static SnapshotChunkMsg ReadSnapshotChunk(byte[] d) => Read(d, r => new SnapshotChunkMsg
    {
        Kind = r.ReadByte(), Name = Str(r),
        Total = r.ReadInt32(), Offset = r.ReadInt32(), Hash = r.ReadUInt32(), Data = r.ReadBytes(r.ReadInt32()),
    });

    public static uint Fnv1a(byte[] data)
    {
        uint h = 2166136261;
        foreach (var b in data) { h ^= b; h *= 16777619; }
        return h;
    }

    public static byte[] TalkRequest(int humanId) => Write(MsgType.TalkRequest, w => w.Write(humanId));
    public static byte[] TalkEnd(int humanId) => Write(MsgType.TalkEnd, w => w.Write(humanId));
    public static byte[] TalkReply(int humanId, bool granted) => Write(MsgType.TalkReply, w => { w.Write(humanId); w.Write(granted); });
    public static int ReadTalkHumanId(byte[] d) => BitConverter.ToInt32(d, 3);
    public static bool ReadTalkGranted(byte[] d) => d.Length > 7 && d[7] != 0;

    public static byte[] Forensic(ForensicMsg m) => Write(MsgType.Forensic, w =>
    {
        w.Write(m.ByPlayer); w.Write(m.Kind); w.Write(m.Id); w.Write(m.BelongsTo); V(w, m.Position); V(w, m.Euler);
        var b = Encoding.UTF8.GetBytes(m.Json ?? "");
        w.Write(b.Length);
        w.Write(b);
    });

    public static ForensicMsg ReadForensic(byte[] d) => Read(d, r => new ForensicMsg
    {
        ByPlayer = r.ReadByte(), Kind = r.ReadByte(), Id = r.ReadInt32(), BelongsTo = r.ReadInt32(), Position = V(r), Euler = V(r),
        Json = Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32())),
    });

    public static byte[] Bye() => Write(MsgType.Bye, _ => { });

    public static byte[] Ping() => Write(MsgType.Ping, _ => { });

    /// <summary>Message type, or null if the packet isn't ours or is truncated.</summary>
    public static MsgType? PeekType(byte[] data)
    {
        if (data == null || data.Length < 3 || BitConverter.ToUInt16(data, 0) != Magic) return null;
        return (MsgType)data[2];
    }

    public static HelloMsg ReadHello(byte[] d) => Read(d, r => new HelloMsg
    {
        ProtocolVersion = r.ReadUInt16(), GameVersion = Str(r), ModVersion = Str(r), Name = Str(r),
    });

    public static WelcomeMsg ReadWelcome(byte[] d) => Read(d, r => new WelcomeMsg
    {
        PlayerId = r.ReadByte(), MaxPlayers = r.ReadByte(), CityShareCode = Str(r),
    });

    public static RejectMsg ReadReject(byte[] d) => Read(d, r => new RejectMsg { Reason = Str(r) });

    public static PoseMsg ReadPose(byte[] d) => Read(d, r => new PoseMsg
    {
        PlayerId = r.ReadByte(), Seq = r.ReadUInt16(), Time = r.ReadSingle(), Position = V(r),
        Yaw = r.ReadSingle(), Pitch = r.ReadSingle(), Flags = (PoseFlags)r.ReadByte(),
        LookYaw = r.ReadSingle(), HeldId = r.ReadInt32(), Held = (HeldKind)r.ReadByte(), Activity = (PlayerActivity)r.ReadByte(),
        Swings = r.ReadByte(),
    });

    public static IllegalActionMsg ReadIllegalAction(byte[] d) => Read(d, r => new IllegalActionMsg
    {
        PlayerId = r.ReadByte(), Active = r.ReadBoolean(),
    });

    public static DamageMsg ReadDamage(byte[] d) => Read(d, r => new DamageMsg
    {
        PlayerId = r.ReadByte(), Amount = r.ReadSingle(), AttackerHumanId = r.ReadInt32(), Position = V(r), Direction = V(r),
    });

    /// <summary>True if <paramref name="newer"/> comes after <paramref name="older"/>, with wrap-around.</summary>
    public static bool SeqNewer(ushort newer, ushort older) => (short)(newer - older) > 0;

    static byte[] Write(MsgType type, Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream(64);
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic);
        w.Write((byte)type);
        body(w);
        w.Flush();
        return ms.ToArray();
    }

    static T Read<T>(byte[] data, Func<BinaryReader, T> body)
    {
        using var ms = new MemoryStream(data, 3, data.Length - 3);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        return body(r);
    }

    static void Str(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s ?? "");
        if (b.Length > 255) Array.Resize(ref b, 255);
        w.Write((byte)b.Length);
        w.Write(b);
    }

    static string Str(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));

    static void V(BinaryWriter w, Vec3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    static Vec3 V(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}
