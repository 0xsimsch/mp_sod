"""Fake co-op client speaking SodCoop's UDP protocol (plugins/SodCoop/Net/Protocol.cs).

Joins a host started with [Net] Mode=HostUdp + F11, then walks in a circle around the host's
streamed position, so the host sees its remote-player body move under network control.

usage: python -I fake_client.py [host:port] [seconds] [--illegal-after S] [--open-door KEY --door-after S] [--ko-after S --recover-after S] [--found-evidence EVID] [--spawn PRESET [--move-delete]] [--save-murders DIR] [--talk ID --talk-at S,S,... --talk-end-at S] [--prints]
"""
import math
import socket
import struct
import sys
import time

MAGIC = 0x4D53
VERSION = 38  # SodCoop 0.9.10
HELLO, WELCOME, REJECT, POSE, ILLEGAL, DAMAGE, DOOR, KO, CASE, SPAWN, ITEM, NPCS, TIMESYNC, BYE, PING, LEFT, DEATH, MURDER, CASEREC, SNAP, TALKREQ, TALKREPLY, TALKEND, FORENSIC = 1, 2, 3, 10, 11, 12, 13, 15, 16, 17, 18, 19, 14, 20, 30, 21, 22, 23, 24, 25, 26, 27, 28, 29


def s8(text):
    b = text.encode()[:255]
    return bytes([len(b)]) + b


def msg(t, payload=b""):
    return struct.pack("<HB", MAGIC, t) + payload


def read_str(buf, off):
    n = buf[off]
    return buf[off + 1:off + 1 + n].decode(), off + 1 + n


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    host, port = (args[0] if args else "127.0.0.1:47615").split(":")
    seconds = float(args[1]) if len(args) > 1 else 60
    illegal_after = None
    if "--illegal-after" in sys.argv:
        illegal_after = float(sys.argv[sys.argv.index("--illegal-after") + 1])
    addr = (host, int(port))
    door_key = int(sys.argv[sys.argv.index("--open-door") + 1]) if "--open-door" in sys.argv else None
    door_after = float(sys.argv[sys.argv.index("--door-after") + 1]) if "--door-after" in sys.argv else 10
    door_sent = 0
    ko_after = float(sys.argv[sys.argv.index("--ko-after") + 1]) if "--ko-after" in sys.argv else None
    recover_after = float(sys.argv[sys.argv.index("--recover-after") + 1]) if "--recover-after" in sys.argv else None
    found_ev = sys.argv[sys.argv.index("--found-evidence") + 1] if "--found-evidence" in sys.argv else None
    found_sent = False
    spawn_preset = sys.argv[sys.argv.index("--spawn") + 1] if "--spawn" in sys.argv else None
    spawn_sent = False
    move_delete = "--move-delete" in sys.argv
    item_stage = 0
    items_in = 0
    npc_packets = 0
    npc_ids = set()
    npc_bytes = 0
    host_paused = None
    other_poses = 0
    murder_dir = sys.argv[sys.argv.index("--save-murders") + 1] if "--save-murders" in sys.argv else None
    murders_in = 0
    snap = None
    snap_got = 0
    talk_id = int(sys.argv[sys.argv.index("--talk") + 1]) if "--talk" in sys.argv else None
    talk_at = [float(x) for x in sys.argv[sys.argv.index("--talk-at") + 1].split(",")] if "--talk-at" in sys.argv else []
    talk_end_at = float(sys.argv[sys.argv.index("--talk-end-at") + 1]) if "--talk-end-at" in sys.argv else None
    ko_state = 0  # 0 none sent, 1 KO sent, 2 recovery sent
    doors_in = []

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setblocking(False)
    my_id = None
    host_pos = None
    host_poses = 0
    damage = []
    seq = 0
    start = time.monotonic()
    next_hello = next_pose = next_report = 0.0
    sent_illegal = False

    while time.monotonic() - start < seconds:
        now = time.monotonic() - start
        if my_id is None and now >= next_hello:
            next_hello = now + 1
            sock.sendto(msg(HELLO, struct.pack("<H", VERSION) + s8("") + s8("fake-0.1") + s8("FakeClient")), addr)

        while True:
            try:
                data, _ = sock.recvfrom(65535)
            except BlockingIOError:
                break
            magic, t = struct.unpack_from("<HB", data)
            if magic != MAGIC:
                continue
            if t == WELCOME:
                my_id, maxp = data[3], data[4]
                code, _ = read_str(data, 5)
                print(f"[{now:6.2f}] WELCOME id=P{my_id} max={maxp} city={code!r}", flush=True)
            elif t == REJECT:
                reason, _ = read_str(data, 3)
                print(f"[{now:6.2f}] REJECT {reason}", flush=True)
                return 1
            elif t == POSE:
                pid, _seq, _t, x, y, z, yaw, _pitch, _flags, _look_yaw, _held_id, _held, _activity = struct.unpack_from("<BHffffffBfiBB", data, 3)
                if pid == 0:
                    host_pos = (x, y, z)
                    host_poses += 1
                elif pid != my_id:
                    other_poses += 1
            elif t == DAMAGE:
                pid, amount, attacker = struct.unpack_from("<Bfi", data, 3)
                damage.append((now, amount, attacker))
                print(f"[{now:6.2f}] DAMAGE {amount:.2f} from citizen {attacker}", flush=True)
            elif t == DOOR:
                key, ajar, locked, by = struct.unpack_from("<if?B", data, 3)
                doors_in.append((now, key, ajar, locked, by))
                if len(doors_in) <= 5:
                    print(f"[{now:6.2f}] DOOR key={key} ajar={ajar:.2f} locked={locked} by={by}", flush=True)
            elif t == CASE:
                by, kind = data[3], data[4]
                n = data[5]
                cid = data[6:6 + n].decode()
                print(f"[{now:6.2f}] CASE kind={kind} id={cid!r} by=P{by}", flush=True)
            elif t == SPAWN:
                by, sid = struct.unpack_from("<Bi", data, 3)
                n = data[8]
                print(f"[{now:6.2f}] SPAWN #{sid} {data[9:9 + n].decode()!r} by P{by}", flush=True)
            elif t == NPCS:
                npc_packets += 1
                npc_bytes += len(data)
                n = data[3 + 2 + 4]
                for k in range(n):
                    npc_ids.add(struct.unpack_from("<i", data, 10 + k * 23)[0])
            elif t == TIMESYNC:
                gt, speed, leap, paused = struct.unpack_from("<fBi?", data, 3)
                if paused != host_paused:
                    print(f"[{now:6.2f}] TIMESYNC paused={paused} gameTime={gt:.3f}", flush=True)
                    host_paused = paused
            elif t == MURDER:
                n = struct.unpack_from("<i", data, 3)[0]
                js = data[7:7 + n].decode()
                murders_in += 1
                print(f"[{now:6.2f}] MURDER record {len(js)} chars: {js[:160]}", flush=True)
                if murder_dir:
                    open(f"{murder_dir}/murder_{murders_in}.json", "w").write(js)
            elif t == CASEREC:
                active = data[3]
                n = struct.unpack_from("<i", data, 4)[0]
                js = data[8:8 + n].decode()
                murders_in += 1
                print(f"[{now:6.2f}] CASE record active={active} {len(js)} chars: {js[:140]}", flush=True)
                if murder_dir:
                    open(f"{murder_dir}/case_{murders_in}.json", "w").write(js)
            elif t == SNAP:
                kind = data[3]
                nl = data[4]
                fname = data[5:5 + nl].decode()
                base = 5 + nl
                total, off, h, n = struct.unpack_from("<iiIi", data, base)
                if kind == 1:
                    if off == 0:
                        print(f"[{now:6.2f}] CITY file {fname!r} {total} bytes incoming", flush=True)
                    continue
                if snap is None or len(snap) != total:
                    snap = bytearray(total)
                    snap_got = 0
                snap[off:off + n] = data[base + 16:base + 16 + n]
                snap_got += n
                if snap_got >= total:
                    hh = 2166136261
                    for b in snap:
                        hh = ((hh ^ b) * 16777619) & 0xFFFFFFFF
                    print(f"[{now:6.2f}] SNAPSHOT complete {total} bytes, hash ok={hh == h}", flush=True)
                    if murder_dir:
                        open(f"{murder_dir}/snapshot.sodb", "wb").write(snap)
            elif t == TALKREPLY:
                hid, granted = struct.unpack_from("<i?", data, 3)
                print(f"[{now:6.2f}] TALK reply citizen {hid}: {'granted' if granted else 'busy'}", flush=True)
            elif t == DEATH:
                v, k, mid, w = struct.unpack_from("<iiii", data, 3)
                print(f"[{now:6.2f}] DEATH citizen {v} killer {k} murder {mid} weapon {w}", flush=True)
            elif t == LEFT:
                print(f"[{now:6.2f}] PLAYER LEFT P{data[3]}", flush=True)
            elif t == ITEM:
                items_in += 1
            elif t == BYE:
                print(f"[{now:6.2f}] host said bye", flush=True)
                return 0

        if my_id is not None and host_pos is not None and now >= next_pose:
            next_pose = now + 0.05
            seq = (seq + 1) & 0xFFFF
            ang = now * 0.6
            x = host_pos[0] + 2.5 * math.cos(ang)
            z = host_pos[2] + 2.5 * math.sin(ang)
            yaw = (math.degrees(math.atan2(-math.sin(ang), math.cos(ang))) + 360) % 360
            # pose v25+: ... flags, look yaw, held interactable id (-1), held kind (0)
            sock.sendto(msg(POSE, struct.pack("<BHffffffBfiBB", my_id, seq, now, x, host_pos[1], z, yaw, 0.0, 4, yaw, -1, 0, 0)), addr)

        if my_id is not None and illegal_after is not None and not sent_illegal and now >= illegal_after:
            sent_illegal = True
            sock.sendto(msg(ILLEGAL, struct.pack("<BB", my_id, 1)), addr)
            print(f"[{now:6.2f}] sent IllegalAction(active)", flush=True)

        if my_id is not None and door_key is not None and door_sent < 2 and now >= door_after + door_sent * 6:
            ajar = 1.0 if door_sent == 0 else 0.0
            sock.sendto(msg(DOOR, struct.pack("<if?B", door_key, ajar, False, my_id)), addr)
            print(f"[{now:6.2f}] sent DOOR key={door_key} ajar={ajar}", flush=True)
            door_sent += 1

        if my_id is not None and ko_after is not None and ko_state == 0 and now >= ko_after:
            ko_state = 1
            sock.sendto(msg(KO, struct.pack("<BB", my_id, 1)), addr)
            print(f"[{now:6.2f}] sent PlayerKO(true)", flush=True)
        if my_id is not None and recover_after is not None and ko_state == 1 and now >= recover_after:
            ko_state = 2
            sock.sendto(msg(KO, struct.pack("<BB", my_id, 0)), addr)
            print(f"[{now:6.2f}] sent PlayerKO(false)", flush=True)

        if my_id is not None and found_ev and not found_sent and now >= 5:
            found_sent = True
            b = found_ev.encode()
            sock.sendto(msg(CASE, bytes([my_id, 1, len(b)]) + b + struct.pack("<ii", 0, 0)), addr)
            print(f"[{now:6.2f}] sent CASE EvidenceFound {found_ev!r}", flush=True)

        if my_id is not None and spawn_preset and not spawn_sent and host_pos is not None and now >= 4:
            spawn_sent = True
            sid = 0x40000000 + (my_id << 24) + 1
            pos = (host_pos[0] + 1.0, host_pos[1], host_pos[2])
            payload = struct.pack("<Bi", my_id, sid) + s8(spawn_preset) + struct.pack("<iii", -1, -1, -1)
            payload += struct.pack("<6f", *pos, 0, 0, 0) + s8("") + bytes([0])
            sock.sendto(msg(SPAWN, payload), addr)
            print(f"[{now:6.2f}] sent SPAWN #{sid} {spawn_preset!r} at {pos}", flush=True)

        if spawn_sent and move_delete and item_stage < 2 and now >= 7 + item_stage * 3:
            sid = 0x40000000 + (my_id << 24) + 1
            kind = 1 if item_stage == 0 else 4
            pos = (host_pos[0] + 1.0, host_pos[1], host_pos[2] + 1.0)
            sock.sendto(msg(ITEM, struct.pack("<BBi", my_id, kind, sid) + struct.pack("<6f", *pos, 0, 45, 0) + struct.pack("<i?", -1, False)), addr)
            print(f"[{now:6.2f}] sent ITEM {'Move' if kind == 1 else 'Delete'} #{sid}", flush=True)
            item_stage += 1

        if my_id is not None and talk_id is not None and talk_at and now >= talk_at[0]:
            talk_at.pop(0)
            sock.sendto(msg(TALKREQ, struct.pack("<i", talk_id)), addr)
            print(f"[{now:6.2f}] TALK request citizen {talk_id}", flush=True)
        if my_id is not None and talk_id is not None and talk_end_at is not None and now >= talk_end_at:
            talk_end_at = None
            sock.sendto(msg(TALKEND, struct.pack("<i", talk_id)), addr)
            print(f"[{now:6.2f}] TALK end citizen {talk_id}", flush=True)

        if my_id is not None and "--prints" in sys.argv and host_pos is not None and not getattr(main, "prints_sent", False) and now >= 4:
            main.prints_sent = True
            hx, hy, hz = host_pos
            fp = '{"worldPos":{"x":%f,"y":%f,"z":%f},"normal":{"x":0,"y":1,"z":0},"type":0,"source":0}' % (hx + 0.5, hy + 0.9, hz)
            ft = '{"hID":10,"rID":1,"wP":{"x":%f,"y":%f,"z":%f},"eU":{"x":0,"y":90,"z":0},"str":1.0,"bl":0.0,"t":43.5}' % (hx + 1, hy, hz + 1)
            for kind, sid, js, pos in ((1, 0x40000000 + (my_id << 24) + 11, fp, (hx + 0.5, hy + 0.9, hz)), (2, 0x40000000 + (my_id << 24) + 12, ft, (hx + 1, hy, hz + 1))):
                b = js.encode()
                payload = struct.pack("<BBii", my_id, kind, sid, -1) + struct.pack("<6f", *pos, 0, 0, 0) + struct.pack("<i", len(b)) + b
                sock.sendto(msg(FORENSIC, payload), addr)
                print(f"[{now:6.2f}] sent {'fingerprint' if kind == 1 else 'footprint'} #{sid}", flush=True)

        if now >= next_report:
            next_report = now + 5
            print(f"[{now:6.2f}] id={my_id} host_poses={host_poses} host_pos={host_pos}", flush=True)
        time.sleep(0.005)

    sock.sendto(msg(BYE), addr)
    print(f"done: host_poses={host_poses} damage_events={len(damage)} door_events_in={len(doors_in)} item_events_in={items_in} npc_packets={npc_packets} distinct_npcs={len(npc_ids)} npc_bytes_per_s={npc_bytes / max(seconds, 1):.0f} other_player_poses={other_poses}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
