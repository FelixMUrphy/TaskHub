"""The paid AWS read can't be turned on over the local HTTP API, only on the settings page: a script or an agent mustn't start
spending the user's money unasked. Turning it off and reading the state stay open. A real connector, not the simulator; nothing
reaches AWS because the connector is off and has no key. Own port and temporary folders: never the real hub."""
import json, os, shutil, subprocess, sys, tempfile, time, urllib.error, urllib.request

EXE = sys.argv[1]
PORT = 47878
URL = f"http://127.0.0.1:{PORT}"
data = tempfile.mkdtemp(prefix="taskhub-awshttp-data-")
home = tempfile.mkdtemp(prefix="taskhub-awshttp-home-")
ENV = dict(os.environ, TASKHUB_TEST_HOME=home, TASKHUB_NO_AUTOSTART="1", TASKHUB_URL=URL, APPDATA=data)
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
fails = 0


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


with open(os.path.join(data, "config.json"), "w", encoding="utf-8") as f:
    json.dump({"toasts": False, "autostart": False, "update_check": False}, f)
hub = subprocess.Popen([EXE, "--port", str(PORT), "--data", data, "--no-tray"], env=ENV)
try:
    end = time.time() + 30
    while time.time() < end:
        try:
            if call("GET", "/api/health")[0] == 200:
                break
        except OSError:
            pass
        time.sleep(0.2)
    code, _ = call("POST", "/api/connectors/aws", {"cost": True})
    check("cost can't be turned on over HTTP", code == 403, code)
    code, r = call("GET", "/api/connectors/aws")
    check("...the state can be read and it stays off", code == 200 and r.get("cost") is False, r)
    code, _ = call("POST", "/api/connectors/aws", {"cost": True, "budgets": True})
    check("...a refused request changes nothing else", code == 403 and call("GET", "/api/connectors/aws")[1].get("budgets") is False, code)
    code, r = call("POST", "/api/connectors/aws", {"cost": False})
    check("it can be turned off over HTTP", code == 200 and r.get("cost") is False, (code, r))
    code, r = call("POST", "/api/connectors/aws", {"budgets": True})
    check("other switches still work over HTTP", code == 200 and r.get("budgets") is True, (code, r))
finally:
    hub.kill()
    hub.wait(10)
    shutil.rmtree(data, ignore_errors=True)
    shutil.rmtree(home, ignore_errors=True)

print("AWS HTTP TESTS " + ("PASSED" if not fails else f"FAILED ({fails})"))
raise SystemExit(1 if fails else 0)
