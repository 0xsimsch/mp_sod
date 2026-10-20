"""Fake co-op host speaking SodCoop's UDP protocol, to test the game's *client* side.

Start this, then in game set [Net] Mode=JoinUdp, JoinAddress=127.0.0.1:47615 and press F11.
The fake host welcomes the game as P1, walks a "host" body (P0) in a circle around the client,
and after --damage-after seconds sends a Damage message the client must apply to its own Player.

usage: python -I fake_host.py [port] [seconds] [--damage-after S] [--game-time H] [--npcs ID,ID,...] [--kill ID --kill-after S [--kill-by HUMANID --kill-murder MURDERID]] [--murder-json FILE] [--case-json FILE] [--snapshot FILE [--city FILE --city-name NAME]]
"""
import math
import socket
import struct
import sys
import time

MAGIC = 0x4D53
HELLO, WELCOME, REJECT, POSE, ILLEGAL, DAMAGE, TIMESYNC, NPCS, BYE, DEATH, MURDER, CASEREC, SNAP, TALKREQ, TALKREPLY, TALKEND = 1, 2, 3, 10, 11, 12, 14, 19, 20, 22, 23, 24, 25, 26, 27, 28  # protocol v36 (SodCoop 0.9.4)


def s8(text):
    b = text.encode()[:255]
    return bytes([len(b)]) + b


def msg(t, payload=b""):
    return struct.pack("<HB", MAGIC, t) + payload


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    port = int(args[0]) if args else 47615
    seconds = float(args[1]) if len(args) > 1 else 60
    damage_after = float(sys.argv[sys.argv.index("--damage-after") + 1]) if "--damage-after" in sys.argv else None

    game_time = float(sys.argv[sys.argv.index("--game-time") + 1]) if "--game-time" in sys.argv else None
    next_time = 0.0
    counts = {}
    npcs = [int(x) for x in sys.argv[sys.argv.index("--npcs") + 1].split(",")] if "--npcs" in sys.argv else []
    next_npc = 0.0
    kill_id = int(sys.argv[sys.argv.index("--kill") + 1]) if "--kill" in sys.argv else None
    kill_after = float(sys.argv[sys.argv.index("--kill-after") + 1]) if "--kill-after" in sys.argv else 6
    killed = False
    kill_by = int(sys.argv[sys.argv.index("--kill-by") + 1]) if "--kill-by" in sys.argv else -1
    kill_murder = int(sys.argv[sys.argv.index("--kill-murder") + 1]) if "--kill-murder" in sys.argv else -1
    murder_json = open(sys.argv[sys.argv.index("--murder-json") + 1]).read() if "--murder-json" in sys.argv else None
    murder_sent = False
    case_json = open(sys.argv[sys.argv.index("--case-json") + 1]).read() if "--case-json" in sys.argv else None
    case_sent = False
    snapshot = open(sys.argv[sys.argv.index("--snapshot") + 1], "rb").read() if "--snapshot" in sys.argv else None
    snap_sent = False
    npc_seq = 0
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("0.0.0.0", port))
    sock.setblocking(False)
    client = None
    client_pos = None
    client_poses = 0
    seq = 0
    start = time.monotonic()
    welcomed_at = None
    next_pose = next_report = 0.0
    damage_sent = False

    while time.monotonic() - start < seconds:
        now = time.monotonic() - start
        while True:
            try:
                data, addr = sock.recvfrom(65535)
            except BlockingIOError:
                break
            magic, t = struct.unpack_from("<HB", data)
            if magic != MAGIC:
                continue
            if t == HELLO:
                ver = struct.unpack_from("<H", data, 3)[0]
                client = addr
                if welcomed_at is None:
                    welcomed_at = now
                    print(f"[{now:6.2f}] HELLO v{ver} from {addr}; welcoming as P1", flush=True)
                sock.sendto(msg(WELCOME, bytes([1, 2]) + s8("fake-host-city")), addr)
            elif t == POSE:
                pid, _seq, _t, x, y, z = struct.unpack_from("<BHffff", data, 3)
                client_pos = (x, y, z)
                client_poses += 1
            elif t == ILLEGAL:
                pid, active = struct.unpack_from("<BB", data, 3)
                print(f"[{now:6.2f}] client P{pid} illegal action = {bool(active)}", flush=True)
            elif t == TALKREQ:
                hid = struct.unpack_from("<i", data, 3)[0]
                print(f"[{now:6.2f}] TALK request for citizen {hid}: granting", flush=True)
                sock.sendto(msg(TALKREPLY, struct.pack("<i?", hid, True)), addr)
            elif t == TALKEND:
                print(f"[{now:6.2f}] TALK end citizen {struct.unpack_from('<i', data, 3)[0]}", flush=True)
            elif t not in (POSE,):
                counts[t] = counts.get(t, 0) + 1
            if t == BYE:
                print(f"[{now:6.2f}] client left", flush=True)
                client = None

        if client and client_pos and now >= next_pose:
            next_pose = now + 0.05
            seq = (seq + 1) & 0xFFFF
            ang = now * 0.5
            x, z = client_pos[0] + 3 * math.cos(ang), client_pos[2] + 3 * math.sin(ang)
            sock.sendto(msg(POSE, struct.pack("<BHffffffBfiBB", 0, seq, now, x, client_pos[1], z, 0.0, 0.0, 4, 0.0, -1, 0, 0)), client)

        if client and client_pos and damage_after is not None and not damage_sent and now >= damage_after:
            damage_sent = True
            sock.sendto(msg(DAMAGE, struct.pack("<Bfi", 1, 0.3, -1) + struct.pack("<6f", *client_pos, 1, 0, 0)), client)
            print(f"[{now:6.2f}] sent Damage 0.30 to P1", flush=True)

        if client and game_time is not None and now >= next_time:
            next_time = now + 2
            # game hours advance 0.0125 h per real second at normal speed
            sock.sendto(msg(TIMESYNC, struct.pack("<fBi?B", game_time + 0.0025 * (now - (welcomed_at or 0)), 1, 0, False, 1)), client)

        if client and client_pos and npcs and now >= next_npc:
            next_npc = now + 0.1
            npc_seq = (npc_seq + 1) & 0xFFFF
            body = b""
            for k, hid in enumerate(npcs):
                ang = now * 0.4 + k * 2.1
                x, z = client_pos[0] + 4 * math.cos(ang), client_pos[2] + 4 * math.sin(ang)
                yaw = (math.degrees(math.atan2(-math.sin(ang), math.cos(ang))) + 360) % 360
                body += struct.pack("<i3ffBBB", hid, x, client_pos[1], z, yaw, 4, 1 if hid == npcs[0] else 0, 0)
            sock.sendto(msg(NPCS, struct.pack("<HfB", npc_seq, now, len(npcs)) + body), client)

        if client and murder_json and not murder_sent and now >= 3:
            murder_sent = True
            b = murder_json.encode()
            sock.sendto(msg(MURDER, struct.pack("<i", len(b)) + b), client)
            print(f"[{now:6.2f}] sent MurderRecord ({len(b)} bytes)", flush=True)

        if client and case_json and not case_sent and now >= 4:
            case_sent = True
            b = case_json.encode()
            sock.sendto(msg(CASEREC, bytes([1]) + struct.pack("<i", len(b)) + b), client)
            print(f"[{now:6.2f}] sent CaseRecord ({len(b)} bytes, active)", flush=True)

        if client and snapshot and not snap_sent and now >= 3:
            snap_sent = True
            hh = 2166136261
            for b in snapshot:
                hh = ((hh ^ b) * 16777619) & 0xFFFFFFFF
            def stream(kind, name, blob):
                h2 = 2166136261
                for b in blob:
                    h2 = ((h2 ^ b) * 16777619) & 0xFFFFFFFF
                for off in range(0, len(blob), 16384):
                    part = blob[off:off + 16384]
                    sock.sendto(msg(SNAP, bytes([kind]) + s8(name) + struct.pack("<iiIi", len(blob), off, h2, len(part)) + part), client)
                    time.sleep(0.02)
            if "--city" in sys.argv:
                stream(1, sys.argv[sys.argv.index("--city-name") + 1], open(sys.argv[sys.argv.index("--city") + 1], "rb").read())
                print(f"[{now:6.2f}] sent city file", flush=True)
            stream(0, "", snapshot)
            print(f"[{now:6.2f}] sent snapshot {len(snapshot)} bytes", flush=True)

        if client and kill_id is not None and not killed and now >= kill_after:
            killed = True
            npcs = [n for n in npcs if n != kill_id]  # the host stops streaming the dead
            sock.sendto(msg(DEATH, struct.pack("<iiii", kill_id, kill_by, kill_murder, -1) + struct.pack("<4f", *client_pos, 0.0)), client)  # v26+: victim position + yaw
            print(f"[{now:6.2f}] sent Death citizen {kill_id}", flush=True)

        if now >= next_report:
            next_report = now + 5
            print(f"[{now:6.2f}] client={client} client_poses={client_poses} client_pos={client_pos}", flush=True)
        time.sleep(0.005)

    if client:
        sock.sendto(msg(BYE), client)
    print(f"done: client_poses={client_poses} other messages by type={counts}", flush=True)


if __name__ == "__main__":
    main()
