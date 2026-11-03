"""Borrowing Git's credentials must never pop a window: an askpass program set for Git stays unused, a credential helper still works.
Git runs with its own empty config and a throwaway credential store, so nothing real is read. Own port and temporary folders."""
import json, os, shutil, subprocess, sys, tempfile, time, urllib.error, urllib.request

EXE = sys.argv[1]
PORT = 47875
URL = f"http://127.0.0.1:{PORT}"
fails = 0
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"), flush=True)
    if not cond:
        fails += 1


def call(method, path, body=None):
    r = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(),
                               headers={"X-TaskHub": "1", "Content-Type": "application/json"})
    try:
        with op.open(r, timeout=10) as f:
            return f.status, json.loads(f.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


def run(gitconfig, wait_for):
    """A hub with this Git config and the connector on, borrowing Git's credentials. Returns (asked askpass?, connector's status)."""
    tmp = tempfile.mkdtemp(prefix="taskhub-askpass-")
    data = os.path.join(tmp, "data")
    os.makedirs(data)
    marker = os.path.join(tmp, "asked.txt")
    askpass = os.path.join(tmp, "askpass.cmd")
    with open(askpass, "w") as f:
        f.write(f'@echo asked>"{marker}"\r\n@echo from-askpass\r\n')
    cfg = os.path.join(tmp, "gitconfig")
    with open(cfg, "w") as f:
        f.write(gitconfig)
    with open(os.path.join(data, "config.json"), "w") as f:
        json.dump({"toasts": False, "autostart": False, "update_check": False, "connectors": {"github": {"api": "http://127.0.0.1:47874"}}}, f)
    env = dict(os.environ, TASKHUB_TEST_HOME=tmp, TASKHUB_NO_AUTOSTART="1", HOME=tmp, USERPROFILE=tmp, GIT_ASKPASS=askpass,
               GIT_CONFIG_GLOBAL=cfg, GIT_CONFIG_NOSYSTEM="1", GCM_CREDENTIAL_STORE="plaintext", GCM_PLAINTEXT_STORE_PATH=os.path.join(tmp, "gcm"))
    env.pop("SSH_ASKPASS", None)
    hub = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=env)
    try:
        end = time.time() + 30
        while time.time() < end:
            try:
                if call("GET", "/api/health")[0] == 200:
                    break
            except OSError:
                pass
            time.sleep(0.2)
        code, r = call("POST", "/api/connectors/github", {"enabled": True, "auth": "gcm", "repos": ["o/r"]})
        check("the connector takes Git's credentials as its source", code == 200, (code, r))
        status, end = "", time.time() + 20
        while time.time() < end:
            status = str(call("GET", "/api/connectors/github")[1].get("status") or "")
            if wait_for(status):
                break
            time.sleep(0.3)
        return os.path.exists(marker), status
    finally:
        hub.kill()
        hub.wait(10)
        shutil.rmtree(tmp, ignore_errors=True)


asked, status = run("", lambda s: "no token" in s)
check("nothing configured: no askpass window", not asked)
check("...and it says there is no token", "no token" in status, status)
asked, status = run('[credential]\n\thelper = "!f() { echo username=me; echo password=not-a-real-token; }; f"\n', lambda s: s and "no token" not in s and s != "off")
check("a credential helper still gives the token", "no token" not in status and status, status)
check("...without asking", not asked)
print("GITHUB ASKPASS TESTS " + ("PASSED" if not fails else f"FAILED ({fails})"))
raise SystemExit(1 if fails else 0)
