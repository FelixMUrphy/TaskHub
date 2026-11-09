"""A stand-in for ssh.exe (tests/ssh_test.py, through TASKHUB_TEST_SSH). Records its arguments and what it is sent, answers from <FAKE_SSH_DIR>/<host>.json,
read again each tick, like a host: {"mode": "ok" | "hostkey" | "changed" | "auth" | "refused" | "hang" | "force" | "drop" | "exit1" | "ok_exit", "sections": {name: [lines]}, "noise": bool}."""
import json, os, re, sys, time

folder = os.environ["FAKE_SSH_DIR"]
args = sys.argv[1:]
host = args[args.index("--") + 1]
with open(os.path.join(folder, host + ".argv"), "w", encoding="utf-8") as f:
    json.dump(args, f)
with open(os.path.join(folder, host + ".count"), "a") as f:
    f.write("x")


def spec():
    try:
        with open(os.path.join(folder, host + ".json"), encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {"mode": "ok", "sections": {}}


s = spec()
mode = s.get("mode", "ok")
if mode == "hostkey":
    sys.stderr.write(f"No ED25519 host key is known for {host} and you have requested strict checking.\r\nHost key verification failed.\r\n")
    sys.exit(255)
if mode == "changed":
    sys.stderr.write("@@@@@@@@\r\n@    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @\r\nHost key verification failed.\r\n")
    sys.exit(255)
if mode == "auth":
    sys.stderr.write(f"user@{host}: Permission denied (publickey).\r\n")
    sys.exit(255)
if mode == "refused":
    sys.stderr.write(f"ssh: connect to host {host} port 22: Connection refused\r\n")
    sys.exit(255)
if mode == "force":
    sys.stdout.write("This account is restricted.\n")
    sys.stdout.flush()
    sys.exit(0)
if mode == "hang":
    time.sleep(3600)

first = True
script = []
for line in iter(sys.stdin.buffer.readline, b""):
    text = line.decode("utf-8")
    script.append(text)
    m = re.match(r"echo TH-([0-9a-f]+)-end\s*$", text)
    if not m:
        continue
    with open(os.path.join(folder, host + ".script"), "w", encoding="utf-8", newline="") as f:
        f.write("".join(script))
    script = []
    with open(os.path.join(folder, host + ".ticks"), "a") as f:
        f.write("x")
    s = spec()
    if s.get("mode") == "exit1":
        sys.exit(1)
    if s.get("mode") in ("drop", "refused", "hostkey", "auth"):
        sys.stderr.write("Connection to host closed by remote host.\r\n")
        sys.exit(255)
    if s.get("mode") == "hang":
        time.sleep(3600)
    out = sys.stdout
    if first and s.get("noise"):
        out.write("Welcome to Ubuntu\x1b[2J\n")   # a greeting, before any marker
        out.write(f"TH-0000000000000000-end\n")   # a marker from someone who doesn't know the nonce
    first = False
    nonce = m.group(1)
    for name, lines in s.get("sections", {}).items():
        out.write(f"TH-{nonce}-{name}\n")
        for l in lines:
            out.write(l + "\n")
    out.write(f"TH-{nonce}-end\n")
    out.flush()
    if s.get("mode") == "ok_exit":
        sys.stderr.write("Connection to host closed by remote host.\r\n")
        sys.exit(255)
