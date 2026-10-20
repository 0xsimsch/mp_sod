"""Snapshot the game while it hangs on IL2CPP's "SuspendThread loop failed" box, to find the thread the GC couldn't stop.

The Boehm GC's stop-the-world (GameAssembly+0x2ec710, this build) walks its thread table (256 hash buckets, linked lists)
and for each registered thread loops SuspendThread + GetThreadContext; after 1,000,000 failed tries it shows the box and
waits. Threads it already stopped have the "suspended" byte (+0x3b) set and stay stopped, so the culprit is the first
registered thread (table order) that isn't. The entry also has its Windows thread id; Wine keeps every thread's TEB at its
GS base, so ptrace (gdb) maps Windows ids to Linux threads. A culprit with no live thread = a thread that exited without
leaving the GC's table (SuspendThread then fails forever); a live one = Wine couldn't stop it, and its state/backtrace says
where it sat.

Needs ptrace rights on the game (kernel.yama.ptrace_scope=1 allows only parents): run with sudo, or
`sudo sysctl kernel.yama.ptrace_scope=0` for the session.

usage: sudo python3 -I gc_hang_trace.py [--watch] [--out FILE] [--gdb PATH]
  --watch   wait for the hang (sodcoop.log silent for 20 s while the game runs), then snapshot
"""
import os
import re
import shutil
import struct
import subprocess
import sys
import time

GAME_DIR = os.path.expanduser("~s/.local/share/Steam/steamapps/common/Shadows of Doubt")
MOD_LOG = os.path.join(GAME_DIR, "BepInEx", "sodcoop.log")
LEA_TABLE = 0x2EC74B  # lea r14, [rip+disp32] -> thread table, in GC_stop_world's suspend loop
LEA_LEN = 7
BUCKETS = 256
# GC_thread entry fields (from the suspend loop's accesses)
F_NEXT, F_ID, F_HANDLE, F_STACK_BASE, F_BLOCKED, F_SUSPENDED, F_SAVED_SP = 0x0, 0x8, 0x10, 0x18, 0x28, 0x3B, 0x40
TEB_STACK_BASE, TEB_STACK_LIMIT, TEB_TID = 0x8, 0x10, 0x48


def game_pid():
    best = None
    for p in os.listdir("/proc"):
        if not p.isdigit():
            continue
        try:
            cmd = open(f"/proc/{p}/cmdline", "rb").read()
            maps = open(f"/proc/{p}/maps").read() if b"Shadows of Doubt.exe" in cmd else ""
        except OSError:
            continue
        if "GameAssembly.dll" in maps:
            n = len(os.listdir(f"/proc/{p}/task"))
            if best is None or n > best[1]:
                best = (int(p), n)
    return best[0] if best else None


def module_base(pid, name):
    for line in open(f"/proc/{pid}/maps"):
        parts = line.split()
        if len(parts) >= 6 and parts[-1].endswith(name) and int(parts[2], 16) == 0:
            return int(parts[0].split("-")[0], 16)
    return None


class Mem:
    def __init__(self, pid):
        self.f = open(f"/proc/{pid}/mem", "rb", buffering=0)

    def read(self, addr, n):
        self.f.seek(addr)
        return self.f.read(n)

    def q(self, addr):
        return struct.unpack("<Q", self.read(addr, 8))[0]

    def d(self, addr):
        return struct.unpack("<I", self.read(addr, 4))[0]

    def b(self, addr):
        return self.read(addr, 1)[0]


def gc_threads(mem, ga):
    ins = mem.read(ga + LEA_TABLE, LEA_LEN)
    if ins[:3] != b"\x4c\x8d\x35":
        raise SystemExit(f"unexpected code at GameAssembly+{LEA_TABLE:#x} ({ins.hex()}): different game build, re-derive the offsets")
    table = ga + LEA_TABLE + LEA_LEN + struct.unpack("<i", ins[3:7])[0]
    out = []
    for i in range(BUCKETS):
        e = mem.q(table + 8 * i)
        while e:
            out.append({
                "entry": e, "bucket": i, "id": mem.d(e + F_ID), "handle": mem.q(e + F_HANDLE),
                "stack_base": mem.q(e + F_STACK_BASE), "blocked": mem.q(e + F_BLOCKED),
                "suspended": mem.b(e + F_SUSPENDED), "saved_sp": mem.q(e + F_SAVED_SP),
            })
            e = mem.q(e + F_NEXT)
    return table, out


def find_gdb(arg):
    if arg:
        return arg
    g = shutil.which("gdb")
    if g:
        return g
    out = subprocess.run(["nix", "build", "nixpkgs#gdb", "--no-link", "--print-out-paths"], capture_output=True, text=True)
    return out.stdout.strip().splitlines()[-1] + "/bin/gdb"


GDB_PY = r"""
import gdb
for t in sorted(gdb.selected_inferior().threads(), key=lambda t: t.ptid[1]):
    try:
        t.switch()
        gs = int(gdb.parse_and_eval("$gs_base"))
        bt = gdb.execute("bt 25", to_string=True)
    except gdb.error as e:
        gs, bt = 0, "(%s)" % e
    print("@@LWP %d GS %#x" % (t.ptid[1], gs))
    for line in bt.splitlines():
        print("@@BT %d %s" % (t.ptid[1], line))
"""


def gdb_threads(gdb, pid):
    """LWP -> gs_base and LWP -> backtrace lines, for every thread (gdb attaches, reads, detaches)."""
    script = f"/tmp/gc_hang_trace_{os.getpid()}.py"
    with open(script, "w") as f:
        f.write(GDB_PY)
    try:
        res = subprocess.run([gdb, "-q", "-nx", "-batch", "-ex", "set pagination off", "-p", str(pid), "-x", script],
                             capture_output=True, text=True, timeout=900)
    finally:
        os.remove(script)
    gs, bts = {}, {}
    for line in res.stdout.splitlines():
        m = re.match(r"@@LWP (\d+) GS (0x[0-9a-f]+)", line)
        if m:
            gs[int(m.group(1))] = int(m.group(2), 16)
            continue
        m = re.match(r"@@BT (\d+) (.*)", line)
        if m:
            bts.setdefault(int(m.group(1)), []).append(m.group(2))
    if not gs:
        print("gdb gave no threads:\n" + (res.stdout + res.stderr)[-3000:])
    return gs, bts


def task_info(pid, tid):
    def rd(name):
        try:
            return open(f"/proc/{pid}/task/{tid}/{name}").read().strip()
        except OSError as e:
            return f"? ({e.strerror})"
    stat = rd("stat")
    state = stat.rsplit(")", 1)[1].split()[0] if ")" in stat else stat
    sig = {}
    for line in rd("status").splitlines():
        k, _, v = line.partition(":")
        if k in ("SigBlk", "SigPnd", "ShdPnd"):
            sig[k] = v.strip()
    return {"comm": rd("comm"), "state": state, "wchan": rd("wchan"), "syscall": rd("syscall"), **sig}


SYSCALLS = {0: "read", 1: "write", 7: "poll", 23: "select", 35: "nanosleep", 202: "futex", 232: "epoll_wait",
            281: "epoll_pwait", 270: "pselect6", 271: "ppoll", 47: "recvmsg", 45: "recvfrom", 61: "wait4", 230: "clock_nanosleep"}


def describe_syscall(s):
    try:
        n = int(s.split()[0])
    except (ValueError, IndexError):
        return s
    return f"{SYSCALLS.get(n, n)} ({s})" if n >= 0 else s


def snapshot(out_path, gdb):
    pid = game_pid()
    if not pid:
        raise SystemExit("game not running")
    ga = module_base(pid, "GameAssembly.dll")
    lines = [f"gc_hang_trace {time.strftime('%Y-%m-%d %H:%M:%S')} pid {pid}, GameAssembly at {ga:#x}"]
    mem = Mem(pid)
    table, regs = gc_threads(mem, ga)
    gs, bts = gdb_threads(gdb, pid)  # gdb attach/detach also stops the threads briefly: GC entries were read first
    # Native Linux threads (steamclient.so's) inherit the GS base of the thread that made them, so several LWPs can show
    # one TEB: prefer the one that runs Wine code (its backtrace goes through ntdll.so).
    by_win_tid = {}
    for lwp, base in gs.items():
        try:
            entry = (lwp, mem.q(base + TEB_STACK_BASE), mem.q(base + TEB_STACK_LIMIT))
            tid = mem.d(base + TEB_TID)
        except (OSError, struct.error):
            continue
        wine = any("ntdll.so" in l or "win32u.so" in l for l in bts.get(lwp, []))
        if tid not in by_win_tid or (wine and not by_win_tid[tid][3]):
            by_win_tid[tid] = entry + (wine,)
    # The stop-the-world runs on whichever thread allocated: it's the one suspending (or asking for a context).
    collector = {lwp for lwp, bt in bts.items() if any(f in l for l in bt for f in ("NtSuspendThread", "NtGetContextThread", "NtResumeThread"))}
    lines.append(f"GC thread table at {table:#x}: {len(regs)} registered threads, {sum(1 for r in regs if r['suspended'])} suspended; "
                 f"{len(gs)} Linux threads, {len(by_win_tid)} with a readable TEB")
    culprit = None
    for r in regs:
        live = by_win_tid.get(r["id"])
        skip = r["stack_base"] == 0 or r["blocked"] != 0
        is_collector = live is not None and live[0] in collector
        tag = "skipped" if skip else ("SUSPENDED" if r["suspended"] else "not suspended")
        if is_collector:
            tag += "  (COLLECTOR: this thread is doing the suspending)"
        elif not skip and not r["suspended"] and culprit is None:
            culprit = r
            tag += "  <== first not suspended: the GC is stuck on this one"
        where = f"LWP {live[0]} {task_info(pid, live[0])['comm']}" if live else "NO LIVE THREAD (exited?)"
        lines.append(f"  win tid {r['id']:6d} handle {r['handle']:#x} stack_base {r['stack_base']:#x} blocked {r['blocked']:#x} "
                     f"saved_sp {r['saved_sp']:#x}: {tag}; {where}")
    lines.append("")
    if culprit:
        live = by_win_tid.get(culprit["id"])
        lines.append(f"CULPRIT win tid {culprit['id']}: " + ("no Linux thread has this id -> the thread EXITED but is still in the GC's table"
                                                            if not live else f"live, LWP {live[0]}, TEB stack {live[2]:#x}-{live[1]:#x}"))
        if live:
            ti = task_info(pid, live[0])
            lines.append(f"  comm={ti['comm']} state={ti['state']} wchan={ti['wchan']} syscall={describe_syscall(ti['syscall'])} "
                         f"SigBlk={ti.get('SigBlk')} SigPnd={ti.get('SigPnd')} ShdPnd={ti.get('ShdPnd')}")
            lines += ["  " + l for l in bts.get(live[0], ["(no backtrace)"])]
        lines.append("")
    lines.append("ALL LINUX THREADS")
    win_of = {v[0]: k for k, v in by_win_tid.items()}
    for tid in sorted(int(t) for t in os.listdir(f"/proc/{pid}/task")):
        ti = task_info(pid, tid)
        lines.append(f"LWP {tid} win {win_of.get(tid, '-')} comm={ti['comm']} state={ti['state']} wchan={ti['wchan']} "
                     f"syscall={describe_syscall(ti['syscall'])} SigBlk={ti.get('SigBlk')} SigPnd={ti.get('SigPnd')}")
        lines += ["    " + l for l in bts.get(tid, [])[:12]]
    report = "\n".join(lines)
    with open(out_path, "w") as f:
        f.write(report + "\n")
    print("\n".join(lines[: 8 + len(regs) + 12]))
    print(f"\nfull report: {out_path}")


def watch(out_path, gdb):
    """
    Armed once the mod log gets a PERF line (every 10 s, only in a session: the menu is quiet for minutes) written after
    the watch started. Older lines don't count: a crashed session's log is silent and full of PERF lines (that fired at
    once, before the join).
    """
    print("watching: snapshot when sodcoop.log stops for 20 s during a session (Ctrl+C to stop)")
    def ident():
        try:
            st = os.stat(MOD_LOG)
            return st.st_ino, st.st_size
        except OSError:
            return None, 0
    ino, offset = ident()  # skip what's there now
    armed = False
    while True:
        cur_ino, size = ident()
        if cur_ino != ino or size < offset:  # the game restarted: a new log
            ino, offset, armed = cur_ino, 0, False
        if size > offset:
            with open(MOD_LOG, "rb") as f:
                f.seek(offset)
                new = f.read(size - offset)
            offset = size
            if not armed and b" PERF " in new:
                armed = True
                print(f"{time.strftime('%H:%M:%S')} session running: armed")
        if armed and game_pid() and time.time() - os.path.getmtime(MOD_LOG) > 20:
            print(f"{time.strftime('%H:%M:%S')} log silent for 20 s: snapshot")
            snapshot(out_path, gdb)
            return
        time.sleep(2)


def main():
    a = sys.argv[1:]
    out = a[a.index("--out") + 1] if "--out" in a else os.path.join(GAME_DIR, "BepInEx", f"gc_hang_{time.strftime('%Y%m%d_%H%M%S')}.txt")
    gdb = find_gdb(a[a.index("--gdb") + 1] if "--gdb" in a else None)
    (watch if "--watch" in a else snapshot)(out, gdb)


if __name__ == "__main__":
    main()
