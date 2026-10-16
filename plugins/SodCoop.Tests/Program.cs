using System;
using System.Net;
using System.Threading;
using SodCoop.Net;

// Minimal self-checking test runner: `dotnet run` exits non-zero on the first failure.
static class Program
{
    static int _passed;

    static void Check(bool ok, string what)
    {
        if (!ok) { Console.WriteLine($"FAIL {what}"); Environment.Exit(1); }
        _passed++;
    }

    static int Main()
    {
        var hello = Protocol.ReadHello(Protocol.Hello(new HelloMsg { ProtocolVersion = Protocol.Version, GameVersion = "42.04", ModVersion = "0.2.0", Name = "Zoë" }));
        Check(hello.ProtocolVersion == Protocol.Version && hello.GameVersion == "42.04" && hello.Name == "Zoë", "Hello round trip (UTF-8 name)");
        Check(Protocol.ReadPauseState(Protocol.PauseState(true)) && !Protocol.ReadPauseState(Protocol.PauseState(false)), "PauseState round trip");
        var jr = Protocol.ReadJobRecord(Protocol.JobRecord("SideJobAffair", "{\"jobID\":7}"));
        Check(jr.type == "SideJobAffair" && jr.json == "{\"jobID\":7}", "JobRecord round trip");
        Check(Protocol.ReadJobAccept(Protocol.JobAccept(42)) == 42, "JobAccept round trip");
        var bm = Protocol.ReadBodyMap(Protocol.BodyMap(new System.Collections.Generic.List<(byte, int)> { (0, 17), (1, 2048) }));
        Check(bm.Count == 2 && bm[0] == (0, 17) && bm[1] == (1, 2048), "BodyMap round trip");
        var th = Protocol.ReadItem(Protocol.Item(new ItemMsg { Kind = ItemEventKind.Throw, Id = 5, Velocity = new Vec3(1, 2, 3), Spin = new Vec3(4, 5, 6) }));
        Check(th.Kind == ItemEventKind.Throw && th.Id == 5 && th.Velocity.Z == 3 && th.Spin.X == 4, "Item throw round trip");
        var mv = Protocol.ReadItem(Protocol.Item(new ItemMsg { Kind = ItemEventKind.Move, Id = 6 }));
        Check(mv.Kind == ItemEventKind.Move && mv.Id == 6, "Item move round trip (no throw fields)");
        var pz = Protocol.ReadPose(Protocol.Pose(new PoseMsg { PlayerId = 2, Seq = 9, Yaw = 10f, Pitch = -20f, LookYaw = 33f, HeldId = 1234, Held = HeldKind.Carried, Swings = 0x85 }));
        Check(pz.PlayerId == 2 && pz.Seq == 9 && pz.Pitch == -20f && pz.LookYaw == 33f && pz.HeldId == 1234 && pz.Held == HeldKind.Carried && pz.Swings == 0x85, "Pose round trip with look, held item and swings");
        var cd = Protocol.ReadDamage(Protocol.CitizenDamage(new DamageMsg { PlayerId = 1, Amount = 0.5f, AttackerHumanId = 79 }));
        Check(cd.PlayerId == 1 && cd.AttackerHumanId == 79 && Math.Abs(cd.Amount - 0.5f) < 1e-6, "CitizenDamage round trip");

        var w = Protocol.ReadWelcome(Protocol.Welcome(new WelcomeMsg { PlayerId = 3, MaxPlayers = 4, CityShareCode = "City|seed" }));
        Check(w.PlayerId == 3 && w.MaxPlayers == 4 && w.CityShareCode == "City|seed", "Welcome round trip");

        var poseIn = new PoseMsg { PlayerId = 1, Seq = 65535, Time = 12.5f, Position = new Vec3(1, 2, 3), Yaw = 270f, Pitch = -10f, Flags = PoseFlags.Running | PoseFlags.Moving };
        var poseBytes = Protocol.Pose(poseIn);
        var p = Protocol.ReadPose(poseBytes);
        Check(p.PlayerId == 1 && p.Seq == 65535 && p.Time == 12.5f && p.Position.Y == 2 && p.Yaw == 270f && p.Flags == (PoseFlags.Running | PoseFlags.Moving), "Pose round trip");
        Check(poseBytes.Length == 3 + 1 + 2 + 4 + 12 + 4 + 4 + 1 + 4 + 4 + 1 + 1 + 1 + 1, $"Pose size is 43 bytes (got {poseBytes.Length})");

        var d = Protocol.ReadDamage(Protocol.Damage(new DamageMsg { PlayerId = 1, Amount = 0.2f, AttackerHumanId = 237, Position = new Vec3(4, 5, 6), Direction = new Vec3(0, 0, 1) }));
        Check(d.AttackerHumanId == 237 && d.Amount == 0.2f && d.Direction.Z == 1, "Damage round trip");

        var ia = Protocol.ReadIllegalAction(Protocol.IllegalAction(new IllegalActionMsg { PlayerId = 2, Active = true }));
        Check(ia.PlayerId == 2 && ia.Active, "IllegalAction round trip");

        var door = Protocol.ReadDoorState(Protocol.DoorState(new DoorStateMsg { DoorKey = 221001, Ajar = -1f, Locked = true, ByPlayer = 255 }));
        Check(door.DoorKey == 221001 && door.Ajar == -1f && door.Locked && door.ByPlayer == 255, "DoorState round trip");

        var ts = Protocol.ReadTimeSync(Protocol.TimeSync(new TimeSyncMsg { GameTime = 46.25f, TimeSpeed = 2, LeapYearCycle = 3, Paused = true }));
        Check(ts.GameTime == 46.25f && ts.TimeSpeed == 2 && ts.LeapYearCycle == 3 && ts.Paused, "TimeSync round trip");

        var ko = Protocol.ReadPlayerKO(Protocol.PlayerKO(new PlayerKOMsg { PlayerId = 1, KnockedOut = true }));
        Check(ko.PlayerId == 1 && ko.KnockedOut, "PlayerKO round trip");

        var ce = Protocol.ReadCaseEvent(Protocol.CaseEvent(new CaseEventMsg { ByPlayer = 1, Kind = CaseEventKind.MergeKeys, Id = "ResidentRoster1", A = 3, B = 7 }));
        Check(ce.ByPlayer == 1 && ce.Kind == CaseEventKind.MergeKeys && ce.Id == "ResidentRoster1" && ce.A == 3 && ce.B == 7, "CaseEvent round trip");

        var sp = Protocol.ReadSpawn(Protocol.Spawn(new SpawnMsg
        {
            ByPlayer = 1, Id = 0x41000001, Preset = "Newspaper", BelongsTo = 12, Writer = -1, Receiver = -1,
            Position = new Vec3(1, 2, 3), Euler = new Vec3(0, 90, 0), DdsOverride = "",
            Passed = new[] { new PassedVar { Type = 1, Value = 42, Str = "x" }, new PassedVar { Type = 5, Value = 0.5f, Str = "" } },
        }));
        Check(sp.Id == 0x41000001 && sp.Preset == "Newspaper" && sp.BelongsTo == 12 && sp.Euler.Y == 90 && sp.Passed.Length == 2 && sp.Passed[0].Str == "x" && sp.Passed[1].Value == 0.5f, "Spawn round trip");

        var item = Protocol.ReadItem(Protocol.Item(new ItemMsg { ByPlayer = 1, Kind = ItemEventKind.IntoInventory, Id = 5, Position = new Vec3(1, 1, 1), Euler = new Vec3(0, 0, 0), Holder = 1, HolderIsPlayer = true }));
        Check(item.Kind == ItemEventKind.IntoInventory && item.Id == 5 && item.Holder == 1 && item.HolderIsPlayer, "Item round trip");

        var cps = Protocol.ReadCitizenPoses(Protocol.CitizenPoses(new CitizenPosesMsg
        {
            Seq = 7, Time = 3.5f,
            Citizens = new[] { new CitizenPose { HumanId = 10, Position = new Vec3(1, 2, 3), Yaw = 90, Flags = PoseFlags.Moving | PoseFlags.KnockedOut, Idle = 1, Arms = 8 }, new CitizenPose { HumanId = 11 } },
        }));
        Check(cps.Seq == 7 && cps.Citizens.Length == 2 && cps.Citizens[0].HumanId == 10 && cps.Citizens[0].Yaw == 90 && cps.Citizens[0].Idle == 1 && cps.Citizens[0].Arms == 8 && (cps.Citizens[0].Flags & PoseFlags.KnockedOut) != 0 && cps.Citizens[1].HumanId == 11, "CitizenPoses round trip");
        Check(Protocol.CitizenPoses(new CitizenPosesMsg { Citizens = new CitizenPose[50] }).Length <= 1200, "50 citizens fit in ~1.2 KB");

        Check(Protocol.ReadPlayerLeft(Protocol.PlayerLeft(2)) == 2 && Protocol.PeekType(Protocol.PlayerLeft(2)) == MsgType.PlayerLeft, "PlayerLeft round trip");

        var longJson = "{\"presetStr\":\"SerialKiller\",\"pad\":\"" + new string('x', 5000) + "\"}";
        Check(Protocol.ReadMurderRecord(Protocol.MurderRecord(longJson)) == longJson, "MurderRecord round trip (> 255 chars)");
        var death = Protocol.ReadDeath(Protocol.Death(new DeathMsg { VictimId = 10, KillerId = -3, MurderId = 77, WeaponId = -1 }));
        Check(death.VictimId == 10 && death.KillerId == -3 && death.MurderId == 77, "Death round trip (player killer encoding)");

        var (cj, ca) = Protocol.ReadCaseRecord(Protocol.CaseRecord(longJson, true));
        Check(cj == longJson && ca, "CaseRecord round trip");

        var chunk = new byte[16384];
        new Random(1).NextBytes(chunk);
        var sc = Protocol.ReadSnapshotChunk(Protocol.SnapshotChunk(new SnapshotChunkMsg { Kind = 1, Name = "City.1.4204.seed.citb", Total = 1_000_000, Offset = 32768, Hash = 0xDEADBEEF, Data = chunk }));
        Check(sc.Total == 1_000_000 && sc.Offset == 32768 && sc.Hash == 0xDEADBEEF && sc.Data.Length == 16384 && sc.Data[100] == chunk[100] && sc.Kind == 1 && sc.Name == "City.1.4204.seed.citb", "SnapshotChunk round trip");
        Check(Protocol.Fnv1a(System.Text.Encoding.ASCII.GetBytes("a")) == 0xE40C292Cu, "FNV-1a known vector");

        Check(Protocol.ReadTalkHumanId(Protocol.TalkRequest(147)) == 147 && Protocol.PeekType(Protocol.TalkEnd(1)) == MsgType.TalkEnd, "Talk request/end");
        var tr = Protocol.TalkReply(147, true);
        Check(Protocol.ReadTalkHumanId(tr) == 147 && Protocol.ReadTalkGranted(tr) && !Protocol.ReadTalkGranted(Protocol.TalkReply(147, false)), "Talk reply");

        var fo = Protocol.ReadForensic(Protocol.Forensic(new ForensicMsg { ByPlayer = 1, Kind = 2, Id = 0x4100000C, BelongsTo = -2, Position = new Vec3(1, 2, 3), Euler = new Vec3(0, 90, 0), Json = "{\"hID\":10}" }));
        Check(fo.Kind == 2 && fo.Id == 0x4100000C && fo.BelongsTo == -2 && fo.Euler.Y == 90 && fo.Json == "{\"hID\":10}", "Forensic round trip");

        Check(Protocol.PeekType(Protocol.Bye()) == MsgType.Bye, "PeekType");
        Check(Protocol.PeekType(new byte[] { 1, 2, 3 }) == null, "PeekType rejects foreign packets");
        Check(Protocol.PeekType(new byte[] { 0x53 }) == null, "PeekType rejects short packets");

        Check(Protocol.SeqNewer(1, 65535), "Seq wraps around");
        Check(!Protocol.SeqNewer(65535, 1), "Seq wrap, older");
        Check(Protocol.SeqNewer(200, 100) && !Protocol.SeqNewer(100, 100), "Seq ordering");

        using var host = new UdpTransport(0);
        using var client = new UdpTransport(0);
        int hostPort = int.Parse(host.Name.Split(':')[1]);
        var hostPeer = client.AddPeer(new IPEndPoint(IPAddress.Loopback, hostPort));
        client.Send(hostPeer, Protocol.Hello(new HelloMsg { ProtocolVersion = 1, Name = "c" }), true);
        PeerId from = default; byte[] data = null;
        for (int i = 0; i < 100 && !host.TryReceive(out from, out data); i++) Thread.Sleep(10);
        Check(data != null && Protocol.ReadHello(data).Name == "c", "UDP loopback client → host");
        host.Send(from, Protocol.Welcome(new WelcomeMsg { PlayerId = 1, MaxPlayers = 2, CityShareCode = "" }), true);
        data = null;
        for (int i = 0; i < 100 && !client.TryReceive(out _, out data); i++) Thread.Sleep(10);
        Check(data != null && Protocol.ReadWelcome(data).PlayerId == 1, "UDP loopback host → client (reply to sender)");

        Console.WriteLine($"OK {_passed} checks");
        return 0;
    }
}
