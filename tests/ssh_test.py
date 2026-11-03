"""The SSH connector against a stand-in for ssh.exe (fake_ssh.py through TASKHUB_TEST_SSH): own hub on 47869, temp data. No real host, no real ssh config.
At the end the real ssh.exe, without a network, checks that the options TaskHub passes parse (-G -F none), and a real sh, when there is one, that the script parses."""
import json, os, shutil, subprocess, sys, tempfile, time, urllib.error, urllib.request

EXE = sys.argv[1]
PORT = 47869
URL = f"http://127.0.0.1:{PORT}"
HERE = os.path.dirname(os.path.abspath(__file__))
tmp = tempfile.mkdtemp(prefix="taskhub-ssh-")
data, fake = os.path.join(tmp, "data"), os.path.join(tmp, "fake")
os.makedirs(data)
os.makedirs(fake)
cmd = os.path.join(tmp, "ssh.cmd")
with open(cmd, "w") as f:
    f.write(f'@"{sys.executable}" "{os.path.join(HERE, "fake_ssh.py")}" %*\r\n')
TOKEN = "ssh-test-token"
ENV = dict(os.environ, TASKHUB_TEST_HOME=tmp, TASKHUB_NO_AUTOSTART="1", TASKHUB_TEST_SSH=cmd, FAKE_SSH_DIR=fake)
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def call(method, path, body=None, url=URL):
    r = urllib.request.Request(url + path, method=method, data=None if body is None else json.dumps(body).encode(),
                               headers={"X-TaskHub": "1", "Content-Type": "application/json", "Authorization": "Bearer " + TOKEN})
    try:
        with op.open(r, timeout=15) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


def until(cond, timeout=20):
    end = time.time() + timeout
    while time.time() < end:
        v = cond()
        if v:
            return v
        time.sleep(0.1)
    return None


def host_file(host, **spec):
    p = os.path.join(fake, host + ".json")
    with open(p + ".tmp", "w", encoding="utf-8") as f:
        json.dump(spec, f)
    os.replace(p + ".tmp", p)


def task(host):
    code, t = call("GET", f"/api/tasks/ssh-{host}")
    return t if code == 200 else None


def host_state(host):
    code, s = call("GET", "/api/connectors/ssh")
    return next((h["state"] for h in s.get("hosts", []) if h["host"] == host), None) if code == 200 else None


def logged(host, text):
    t = task(host)
    return t and any(text in l["msg"] for l in t.get("log", []))


def log_count(host, text):
    t = task(host)
    return sum(text in l["msg"] for l in t.get("log", [])) if t else 0


def start(env=ENV, port=PORT, d=data, cfg=None):
    with open(os.path.join(d, "config.json"), "w") as f:
        json.dump(dict({"token": TOKEN, "toasts": False, "autostart": False, "update_check": False, "language": "en", "toast_rate": 0}, **(cfg or {})), f)
    p = subprocess.Popen([EXE, "--port", str(port), "--data", d, "--no-tray"], env=env)
    u = f"http://127.0.0.1:{port}"
    end = time.time() + 30
    while time.time() < end:
        try:
            if call("GET", "/api/health", url=u)[0] == 200:
                return p
        except OSError:
            pass
        time.sleep(0.2)
    raise RuntimeError("hub did not start")


BASE = {
    "uname": ["Linux"],
    "boot": ["11111111-aaaa"],
    "stat": ["cpu  100 0 100 800 0 0 0 0 0 0"],
    "mem": ["MemTotal:       1000 kB", "MemFree:         100 kB", "MemAvailable:    250 kB"],
    "df": ["Filesystem     1024-blocks      Used Available Capacity Mounted on",
           "/dev/sda1          1000000    610000    390000      61% /",
           "tmpfs               100000    100000         0     100% /run/user/1000",
           "/dev/sdb1          1000000    100000    900000      10% /my data"],
    "gpu": ["3, 1024, 24576", "5, 1024, 24576"],
    "tmux": ["%1:train:0:python", "%2:misc:0:bash"],
}


def sections(**over):
    s = dict(BASE)
    s.update(over)
    return s


host_file("box", mode="ok", noise=True, sections=sections())
hub = start(cfg={"connectors": {"ssh": {"enabled": True, "hosts": ["box"], "interval": 0.3}}})
try:
    # ------------------------------------------------------------ reading a host
    check("the host shows as connected", until(lambda: host_state("box") == "ok"), host_state("box"))
    until(lambda: "CPU" in ((task("box") or {}).get("status") or ""))
    t = task("box")
    st = t.get("status") or ""
    check("a task of its own, from the connector", t and t["source"] == "ssh" and t["name"] == "box", t)
    check("RAM from MemAvailable", "RAM 75%" in st, st)
    check("the fullest real disk, not tmpfs", "61%" in st and "100%" not in st, st)
    check("GPU: mean load, all memory", "GPU 4%, 2/48 GB" in st, st)
    check("tmux sessions", "tmux: misc, train" in st, st)
    check("no fake marker or greeting got through", "Welcome" not in json.dumps(t), t)
    host_file("box", mode="ok", sections=sections(stat=["cpu  200 0 100 900 0 0 0 0 0 0"]))
    check("CPU from the difference of two reads", until(lambda: "CPU 50%" in ((task("box") or {}).get("status") or "")), (task("box") or {}).get("status"))

    # ------------------------------------------------------------ how ssh is called
    argv = json.load(open(os.path.join(fake, "box.argv"), encoding="utf-8"))
    for opt in ("BatchMode=yes", "StrictHostKeyChecking=yes", "UpdateHostKeys=no", "ConnectTimeout=10", "ForwardAgent=no", "ClearAllForwardings=yes", "PermitLocalCommand=no", "ControlMaster=no", "CheckHostIP=no", "ForwardX11=no", "Tunnel=no", "AddKeysToAgent=no"):
        check("option " + opt, opt in argv, argv)
    check("the host comes after --", argv[argv.index("--") + 1] == "box" and argv[-1] == "sh -s", argv)
    check("known_hosts stays the user's, TaskHub names no file of its own", not any("UserKnownHostsFile" in a for a in argv), argv)
    script = open(os.path.join(fake, "box.script"), encoding="utf-8", newline="").read()
    check("the script has only LF", "\r" not in script, repr(script[:80]))
    check("the same connection serves every tick", len(open(os.path.join(fake, "box.count")).read()) == 1, open(os.path.join(fake, "box.count")).read())
    check("no process arguments are read (secrets)", " args" not in script and "cmdline" not in script, script)

    # ------------------------------------------------------------ tmux: what ended
    host_file("box", mode="ok", sections=sections(tmux=["%1:train:0:bash", "%2:misc:0:bash"]))
    check("a command back at its shell: ended, not succeeded", until(lambda: logged("box", "python has ended in tmux session train")), task("box")["log"])
    host_file("box", mode="ok", sections=sections(tmux=["%2:misc:0:bash"]))
    check("a session gone: ended", until(lambda: logged("box", "tmux session train has ended")), task("box")["log"])
    host_file("box", mode="ok", sections=sections(tmux=["%2:misc:0:bash", "%3:\x1b[31mevil\u202e:0:bash"]))
    check("control characters out of remote names", until(lambda: "tmux: " in (task("box").get("status") or "") and "evil" in task("box")["status"]) and "\x1b" not in task("box")["status"] and "\u202e" not in task("box")["status"], repr(task("box").get("status")))
    host_file("box", mode="ok", sections=sections(tmux=["%2:misc:0:bash"]))

    # ------------------------------------------------------------ a disk fills up: one word, and again only after it had room
    full = BASE["df"][:1] + ["/dev/sda1 1000000 950000 50000 95% /"]
    host_file("box", mode="ok", sections=sections(df=full))
    check("a full disk: a word", until(lambda: logged("box", "disk / is 95% full")), task("box")["log"])
    check("...and attention on the task", until(lambda: "/" in (task("box").get("attention") or "")), task("box").get("attention"))
    time.sleep(1.2)
    check("...only once", log_count("box", "disk / is 95% full") == 1, log_count("box", "disk / is 95% full"))
    host_file("box", mode="ok", sections=sections())
    check("room again: attention goes", until(lambda: not task("box").get("attention")), task("box").get("attention"))

    # ------------------------------------------------------------ a process the user names
    code, r = call("POST", "/api/tasks/ssh-box/controls/watch", {"value": "abc"})
    check("watch: not a number -> refused", code == 400, code)
    stat = lambda start_: f"4242 (my (odd) job) S 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 {start_} 20 21"
    host_file("box", mode="ok", sections=sections(**{"proc-4242": [stat(777)]}))
    code, r = call("POST", "/api/tasks/ssh-box/controls/watch", {"value": 4242})
    check("watch: pressed", code == 200, (code, r))
    until(lambda: "/proc/4242/stat" in open(os.path.join(fake, "box.script"), encoding="utf-8").read())
    check("the process is named on the task", until(lambda: "my (odd) job (4242)" in (task("box").get("status") or "")), task("box").get("status"))
    host_file("box", mode="ok", sections=sections(**{"proc-4242": [stat(888)]}))
    check("the same number, another start time: a new process, the watched one ended", until(lambda: logged("box", "process my (odd) job (4242) has ended")), task("box")["log"])
    check("...and it isn't watched any more", until(lambda: "/proc/4242/stat" not in open(os.path.join(fake, "box.script"), encoding="utf-8").read()))
    code, _ = call("POST", "/api/tasks/ssh-box/controls/watch", {"value": 5555})
    check("no such process: says so", until(lambda: logged("box", "No process 5555")), task("box")["log"])

    # a restart ends what was watched, and says so
    host_file("box", mode="ok", sections=sections(**{"proc-4242": [stat(777)]}))
    call("POST", "/api/tasks/ssh-box/controls/watch", {"value": 4242})
    until(lambda: "(4242)" in (task("box").get("status") or ""))
    host_file("box", mode="ok", sections=sections(boot=["22222222-bbbb"], **{"proc-4242": [stat(777)]}))
    check("a restart of the host: said", until(lambda: logged("box", "box has restarted")), task("box")["log"])

    # ------------------------------------------------------------ the connection goes and comes back
    host_file("box", mode="drop", sections=sections())
    check("a lost connection: says so once", until(lambda: logged("box", "box: connection lost")), task("box")["log"])
    check("the host state", until(lambda: host_state("box") == "unreachable"), host_state("box"))
    check("...attention on the task", until(lambda: "No connection" in (task("box").get("attention") or "")), task("box").get("attention"))
    count = len(open(os.path.join(fake, "box.count")).read())
    host_file("box", mode="ok", sections=sections(tmux=[]))
    check("it comes back by itself", until(lambda: host_state("box") == "ok"), host_state("box"))
    check("...and says so", until(lambda: logged("box", "box: connected again")))
    check("what ended meanwhile says it was during the gap", until(lambda: logged("box", "ended while there was no connection")), task("box")["log"])
    check("...attention goes", until(lambda: not task("box").get("attention")), task("box").get("attention"))

    # ------------------------------------------------------------ what only the user can solve stops the attempts
    for host, mode, state, word in (("key1", "hostkey", "hostkey", "ssh key1"), ("key2", "changed", "hostkey_changed", "CHANGED"), ("auth1", "auth", "auth", "ssh-agent"), ("ro1", "force", "restricted", "ForceCommand")):
        host_file(host, mode=mode)
        code, _ = call("POST", "/api/connectors/ssh", {"hosts": ["box", host]})
        check(f"{mode}: taken", code == 200, code)
        check(f"{mode}: the state", until(lambda: host_state(host) == state), host_state(host))
        t = task(host)
        check(f"{mode}: the task says what to do", word in (t.get("status") or ""), t.get("status"))
        n = len(open(os.path.join(fake, host + ".count")).read())
        time.sleep(1.5)
        check(f"{mode}: no more attempts", len(open(os.path.join(fake, host + ".count")).read()) == n)
        check(f"{mode}: a Check again button", any(c["id"] == "recheck" for c in task(host).get("controls", [])), task(host).get("controls"))
        host_file(host, mode="ok", sections=sections())
        code, _ = call("POST", f"/api/tasks/ssh-{host}/controls/recheck", {})
        check(f"{mode}: solved, Check again connects", until(lambda: host_state(host) == "ok"), (code, host_state(host)))
        check(f"{mode}: the button goes", until(lambda: not any(c["id"] == "recheck" for c in task(host).get("controls", []))))

    # not Linux
    host_file("mac1", mode="ok", sections=sections(uname=["Darwin"]))
    call("POST", "/api/connectors/ssh", {"hosts": ["box", "mac1"]})
    check("not Linux: says so", until(lambda: host_state("mac1") == "not_linux"), host_state("mac1"))

    # no answer in time isn't tried here: 35 s; the kill is covered by a hung ssh when the hub stops (below)
    # ------------------------------------------------------------ the list
    for bad in ("-oProxyCommand=calc", "a b", "a;calc", "user@box", "$(calc)", "", "../x", "a\nb", "-"):
        code, _ = call("POST", "/api/connectors/ssh", {"hosts": [bad]})
        check(f"a bad host name is refused: {bad!r}", code == 400, code)
    code, _ = call("POST", "/api/connectors/ssh", {"hosts": [f"h{i}" for i in range(21)]})
    check("too many hosts: refused", code == 400, code)
    for body in ({"interval": 0}, {"interval": "abc"}, {"disk_warn": 101}, {"disk_warn": "abc"}, {"hosts": "box"}):
        code, _ = call("POST", "/api/connectors/ssh", body)
        check(f"bad values refused: {body}", code == 400, code)

    # removed from the list: its task ends quietly, its ssh ends
    code, _ = call("POST", "/api/connectors/ssh", {"hosts": ["box"]})
    t = until(lambda: (lambda x: x if x and x["state"] == "done" else None)(task("key1")))
    check("a removed host's task ends", t is not None, task("key1"))

    # off: every task ends, every ssh ends
    code, _ = call("POST", "/api/connectors/ssh", {"enabled": False})
    check("off: the tasks end", until(lambda: task("box")["state"] == "done"), task("box")["state"])
    time.sleep(0.5)
    n = os.path.getmtime(os.path.join(fake, "box.script"))
    time.sleep(1.2)
    check("off: nothing is read any more", os.path.getmtime(os.path.join(fake, "box.script")) == n)
    # a hung ssh ends with the connector
    host_file("hang1", mode="hang")
    call("POST", "/api/connectors/ssh", {"enabled": True, "hosts": ["hang1"]})
    check("a hung ssh: connecting", until(lambda: os.path.exists(os.path.join(fake, "hang1.count"))))
    call("POST", "/api/connectors/ssh", {"enabled": False})
    time.sleep(1.5)
    left = subprocess.run(["powershell", "-NoProfile", "-Command", "@(Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | Where-Object { $_.CommandLine -like '*fake_ssh*hang1*' }).Count"], capture_output=True, text=True).stdout.strip()
    check("a hung ssh ends with the connector, no orphan", left == "0", left)
finally:
    hub.kill()
    hub.wait(10)

# ------------------------------------------------------------ the real hub's way: no fake, the API can't change it
env = {k: v for k, v in ENV.items() if k != "TASKHUB_TEST_SSH"}
d2 = os.path.join(tmp, "data2")
os.makedirs(d2)
hub = start(env=env, d=d2, cfg={"connectors": {"ssh": {"enabled": False}}})
try:
    for body in ({"enabled": True}, {"hosts": ["x"]}, {"interval": 60}):
        code, _ = call("POST", "/api/connectors/ssh", body)
        check(f"over the API, without the test door: 403 {body}", code == 403, code)
    code, s = call("GET", "/api/connectors/ssh")
    check("reading it works", code == 200 and s.get("enabled") is False and "client" in s, (code, s))
finally:
    hub.kill()
    hub.wait(10)

# ------------------------------------------------------------ what the reviews found
def ticks(host):
    try:
        return len(open(os.path.join(fake, host + ".ticks")).read())
    except OSError:
        return 0


def hub_log(d):
    try:
        return open(os.path.join(d, "taskhub.log"), encoding="utf-8", errors="replace").read()
    except OSError:
        return ""


def toasts(d):
    time.sleep(0.6)   # they pass a queue
    return [l for l in hub_log(d).splitlines() if "TOAST [" in l]


def status(host):
    return (task(host) or {}).get("status") or ""


def saved(d):
    return json.load(open(os.path.join(d, "config.json"), encoding="utf-8"))["connectors"]["ssh"]


d3 = os.path.join(tmp, "data3")
os.makedirs(d3)
gone_proc = {"pid": 77, "start": "777", "comm": "job", "boot": "11111111-aaaa"}
for h in ("wp", "good"):
    host_file(h, mode="ok", sections=sections())
hub = start(d=d3, cfg={"log_level": "debug", "toast_task_gap": 0, "connectors": {"ssh": {"enabled": True, "hosts": ["wp", "good"], "interval": 0.3, "watched": {"wp": [gone_proc]}}}})
try:
    add = lambda *hosts: call("POST", "/api/connectors/ssh", {"hosts": list(hosts)})[0]

    # a watched process that ended while TaskHub itself was closed: the promise of the docs
    check("ended while the hub was closed: said, as a gap", until(lambda: logged("wp", "process job (77) ended while there was no connection")), (task("wp") or {}).get("log"))
    until(lambda: host_state("good") == "ok")

    # a good host doesn't get stuck on one bad answer
    for name, bad in (("a non-255 exit", {"mode": "exit1"}), ("an empty uname", {"mode": "ok", "sections": sections(uname=[])})):
        host_file("good", mode="ok", sections=sections())
        until(lambda: host_state("good") == "ok")
        host_file("good", **bad)
        seen = set()
        def went():
            s = host_state("good")
            seen.add(s)
            return s in ("unreachable", "restricted")
        until(went)
        check(f"{name} after a good connection: not 'restricted'", "restricted" not in seen, seen)
        host_file("good", mode="ok", sections=sections())
        check(f"{name}: comes back by itself", until(lambda: host_state("good") == "ok"), host_state("good"))

    # a monitor that stops says so
    host_file("good", mode="ok", sections=sections())
    until(lambda: host_state("good") == "ok")
    n = len(toasts(d3))
    host_file("good", mode="auth")
    check("connected, then refused: the state", until(lambda: host_state("good") == "auth"), host_state("good"))
    check("...and a notification: it has stopped trying", until(lambda: any("ssh-agent" in l for l in toasts(d3)[n:])), toasts(d3)[n:])
    time.sleep(1)
    check("...only once", sum("ssh-agent" in l for l in toasts(d3)[n:]) == 1, toasts(d3)[n:])
    host_file("good", mode="ok", sections=sections())
    call("POST", "/api/tasks/ssh-good/controls/recheck", {})
    until(lambda: host_state("good") == "ok")

    # a host that never connected: the cause, not "lost"
    host_file("typo", mode="refused")
    check("never connected: the list takes it", add("good", "typo") == 200)
    check("...ssh's own reason on the task", until(lambda: "Connection refused" in status("typo")), status("typo"))
    check("...a notification that it can't connect", until(lambda: logged("typo", "can't connect")), (task("typo") or {}).get("log"))
    check("...not 'connection lost'", not logged("typo", "connection lost"), task("typo")["log"])

    # names the task id can hold
    long_ok, long_bad = "a" * 60, "b" * 61
    host_file(long_ok, mode="ok", sections=sections())
    check("a name of 61 characters: refused", add("good", long_bad) == 400)
    check("a name of 60 characters: taken", add("good", long_ok) == 200)
    check("...and it has a task", until(lambda: task(long_ok) is not None and host_state(long_ok) == "ok"), task(long_ok))
    host_file("my.host_1", mode="ok", sections=sections())
    check("dots and underscores: a task too", add("good", "my.host_1") == 200 and until(lambda: task("my.host_1") is not None and host_state("my.host_1") == "ok"), task("my.host_1"))

    # disk warnings off: nothing left stuck
    full = BASE["df"][:1] + ["/dev/sda1 1000000 950000 50000 95% /"]
    host_file("good", mode="ok", sections=sections(df=full))
    until(lambda: task("good").get("attention"))
    call("POST", "/api/connectors/ssh", {"disk_warn": 0})
    check("disk warnings off: attention goes", until(lambda: not task("good").get("attention")), task("good").get("attention"))
    call("POST", "/api/connectors/ssh", {"disk_warn": 90})

    # docker's mounts aren't disks of the host
    host_file("good", mode="ok", sections=sections(df=BASE["df"][:2] + ["overlay 1000 990 10 99% /var/lib/docker/overlay2/abc/merged", "shm 1000 1000 0 100% /var/lib/docker/containers/abc/mounts/shm"]))
    until(lambda: "61%" in status("good") or "99%" in status("good") or "100%" in status("good"))
    time.sleep(1)
    check("docker's overlay and shm aren't counted", "61%" in status("good") and "99%" not in status("good") and "100%" not in status("good"), status("good"))
    host_file("good", mode="ok", sections=sections(df=BASE["df"][:1] + ["overlay 1000 420 580 42% /"]))
    check("an overlay at / is the host's disk (a container)", until(lambda: "42%" in status("good")), status("good"))

    # tmux notifications can be turned off; the log keeps them
    check("the tmux switch is on by default", call("GET", "/api/connectors/ssh")[1].get("tmux_notify") is True, call("GET", "/api/connectors/ssh")[1])
    code, rows = call("GET", "/api/connectors?view=settings")
    row = next((r for r in rows.get("connectors", rows if isinstance(rows, list) else []) if r.get("id") == "ssh"), {})
    check("...and shows in Settings", any(x.get("key") == "tmux_notify" for x in row.get("switches", [])), row.get("switches"))
    host_file("good", mode="ok", sections=sections(tmux=["%7:quiet:0:vim"]))
    until(lambda: "quiet" in status("good"))
    call("POST", "/api/connectors/ssh", {"tmux_notify": False})
    n = len(toasts(d3))
    host_file("good", mode="ok", sections=sections(tmux=["%7:quiet:0:bash"]))
    check("tmux switch off: still in the task's log", until(lambda: logged("good", "vim has ended")), task("good")["log"])
    check("...without a notification", not any("vim" in l for l in toasts(d3)[n:]), toasts(d3)[n:])
    call("POST", "/api/connectors/ssh", {"tmux_notify": True})
    host_file("good", mode="ok", sections=sections(tmux=["%7:quiet:0:htop"]))
    until(lambda: "quiet" in status("good"))
    time.sleep(0.8)
    n = len(toasts(d3))
    host_file("good", mode="ok", sections=sections(tmux=["%7:quiet:0:bash"]))
    check("tmux switch on: a notification", until(lambda: any("htop" in l for l in toasts(d3)[n:]), 8), toasts(d3)[n:])

    # invisible text out of remote names
    host_file("good", mode="ok", sections=sections(tmux=["%9:a\u200fb\u2028c\ufeffd\u200be\u061cf\x1b]0;evil\x07g:0:bash"]))
    check("format characters and an OSC sequence out of remote names", until(lambda: "tmux: abcdefg" in status("good")), repr(status("good")))

    # the buttons answer
    host_file("good", mode="ok", sections=sections())
    n = len(toasts(d3))
    call("POST", "/api/tasks/ssh-good/controls/watch", {"value": 5555})
    check("watching a process that isn't there: a notification", until(lambda: any("5555" in l for l in toasts(d3)[n:]), 8), toasts(d3)[n:])

    # the host comes from the task id, not from what anyone wrote into the task
    host_file("box2", mode="ok", sections=sections())
    add("good", "box2")
    until(lambda: host_state("box2") == "ok")
    call("POST", "/api/tasks/ssh-good/update", {"meta": {"ssh": {"host": "box2"}}})
    host_file("good", mode="ok", sections=sections(**{"proc-4141": ["4141 (j) S 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 999 20 21"]}))
    code, _ = call("POST", "/api/tasks/ssh-good/controls/watch", {"value": 4141})
    w = saved(d3).get("watched", {})
    check("meta can't redirect a button to another host", code == 200 and any(x["pid"] == 4141 for x in w.get("good", [])) and not any(x["pid"] == 4141 for x in w.get("box2", [])), (code, w))
    call("POST", "/api/tasks/ssh-good/update", {"meta": {"ssh": "x"}})
    code, _ = call("POST", "/api/tasks/ssh-good/controls/watch", {"value": 4141})
    check("meta of the wrong shape can't break a button", code == 200, code)
    # the button is there only while something is watched: without it the hub itself answers 404
    until(lambda: any(c["id"] == "unwatch" for c in task("good").get("controls", [])))
    code, _ = call("POST", "/api/tasks/ssh-good/controls/unwatch", {"value": 6666})
    check("unwatching one that isn't watched: an answer", code == 400, code)
    host_file("good", mode="ok", sections=sections())

    # a removed host leaves nothing in config.json
    call("POST", "/api/tasks/ssh-good/controls/watch", {"value": 4141})
    add("box2")
    check("a removed host's watch list goes", until(lambda: "good" not in saved(d3).get("watched", {})), saved(d3).get("watched"))

    # a task left by an earlier run, its host no longer listed
    code, _ = call("POST", "/api/tasks", {"id": "ssh-old", "name": "old", "source": "ssh"})
    add("box2")
    check("an orphan task of the connector ends", until(lambda: (task("old") or {}).get("state") == "done", 8), task("old"))

    # a connection that ends between ticks shows at once
    host_file("box2", mode="ok", sections=sections())
    call("POST", "/api/connectors/ssh", {"interval": 3600})
    until(lambda: host_state("box2") == "ok")
    time.sleep(1)
    n = ticks("box2")
    for _ in range(5):
        code, _ = call("POST", "/api/connectors/ssh", {})
    time.sleep(1)
    check("an empty body wakes nothing", code == 200 and ticks("box2") == n, (code, n, ticks("box2")))
    host_file("box2", mode="ok_exit", sections=sections())
    call("POST", "/api/connectors/ssh", {"interval": 3600})
    check("ssh gone between ticks: not 'connected' for the rest of the hour", until(lambda: host_state("box2") == "unreachable", 8), host_state("box2"))
    host_file("box2", mode="ok", sections=sections())
    call("POST", "/api/connectors/ssh", {"interval": 0.3})

    # a task the user deleted comes back while the host is listed
    until(lambda: host_state("box2") == "ok")
    code, _ = call("DELETE", "/api/tasks/ssh-box2?force=1")
    check("a deleted task: gone", code == 200, code)
    check("...and back while its host is listed", until(lambda: (task("box2") or {}).get("state") == "running" and "RAM" in status("box2")), task("box2"))

    # a line made to be slow to parse doesn't pin a core
    host_file("flood", mode="ok", sections=sections(df=[f"x {'1' * 3990}" for _ in range(4900)]))
    add("box2", "flood")
    until(lambda: ticks("flood") > 0, 30)
    n, end = ticks("flood"), time.time() + 20
    until(lambda: ticks("flood") >= n + 3, 20)
    check("a slow-to-parse df: ticks go on", ticks("flood") >= n + 3, (n, ticks("flood")))
finally:
    hub.kill()
    hub.wait(10)

# the default log level: a host's name stays out of the log
d4 = os.path.join(tmp, "data4")
os.makedirs(d4)
HIDDEN = "hidden-name-of-a-host"
host_file(HIDDEN, mode="ok", sections=sections())
hub = start(d=d4, cfg={"connectors": {"ssh": {"enabled": True, "hosts": [HIDDEN], "interval": 0.3}}})
try:
    until(lambda: host_state(HIDDEN) == "ok")
    call("DELETE", f"/api/tasks/ssh-{HIDDEN}?force=1")
    time.sleep(1.5)
    host_file(HIDDEN, mode="refused")
    time.sleep(1.5)
    check("a host's name stays out of the log", HIDDEN not in hub_log(d4), [l for l in hub_log(d4).splitlines() if HIDDEN in l][:3])
finally:
    hub.kill()
    hub.wait(10)

# ------------------------------------------------------------ real tools, no network, no real config
real = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "OpenSSH", "ssh.exe")
if os.path.exists(real):
    opts = argv[:argv.index("--")]
    r = subprocess.run([real, "-F", "none", "-G", *opts, "--", "ok.example"], capture_output=True, text=True, timeout=20)
    check("the real ssh takes every option", r.returncode == 0 and "batchmode yes" in r.stdout and "stricthostkeychecking true" in r.stdout, r.stderr or r.stdout[:300])
    own = os.path.join(tmp, "ssh_config")
    with open(own, "w") as f:
        f.write("CheckHostIP yes\nForwardX11 yes\nTunnel yes\nAddKeysToAgent yes\nForwardAgent yes\n")
    r = subprocess.run([real, "-F", own, "-G", *opts, "--", "ok.example"], capture_output=True, text=True, timeout=20)
    for line in ("checkhostip no", "forwardx11 no", "tunnel false", "addkeystoagent false", "forwardagent no"):
        check(f"our options beat the user's config: {line}", line in r.stdout.lower(), [l for l in r.stdout.lower().splitlines() if line.split()[0] in l])
    r = subprocess.run([real, "-F", "none", "-G", *opts, "--", "-oProxyCommand=calc"], capture_output=True, text=True, timeout=20)
    check("after --, an option-like host isn't an option", "proxycommand calc" not in r.stdout.lower(), r.stdout[:300])
else:
    print("SKIP real ssh: not installed")
sh = shutil.which("sh")
if sh:
    r = subprocess.run([sh, "-n"], input=script, capture_output=True, text=True, timeout=20)
    check("the script parses in a real sh", r.returncode == 0, r.stderr)
    # one shell that reads tick after tick: what the connector keeps open. Commands inside can't eat the next tick from stdin.
    p = subprocess.Popen([sh, "-s"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    try:
        nonce = script.split("TH-")[1].split("-")[0]
        for i in range(2):
            p.stdin.write(script.encode())
            p.stdin.flush()
            seen = []
            for line in iter(p.stdout.readline, b""):
                seen.append(line.decode("utf-8", "replace").strip())
                if seen[-1] == f"TH-{nonce}-end":
                    break
            check(f"a real sh answers tick {i + 1} to its end, section by section", seen and seen[-1] == f"TH-{nonce}-end" and f"TH-{nonce}-df" in seen and f"TH-{nonce}-tmux" in seen, seen[:6])
    finally:
        p.kill()
        p.wait(5)
else:
    print("SKIP sh: not found")

shutil.rmtree(tmp, ignore_errors=True)
print("SSH TESTS " + ("PASSED" if not fails else f"FAILED ({fails})"))
raise SystemExit(1 if fails else 0)
