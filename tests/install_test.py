"""Install steps (Start-menu shortcut, autostart self-healing, quit before kill, Store data take-over, purge) through the
hidden `TaskHubHost.exe --selftest-install STEP` with TASKHUB_TEST_HOME: the registry entries go under
HKCU\\Software\\TaskHubTest\\<temp folder name> and the shortcut under the temp folder - never the real install / entries.
The quit test starts its own hub on 47893 (temp data) and stops only that one.
Usage: python install_test.py [path\\to\\TaskHubHost.exe]"""
import json, os, shutil, subprocess, sys, tempfile, time, urllib.request, winreg
import vault

exe = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "app", "TaskHubHost.exe"))
exe_dir = os.path.dirname(exe)
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


home = tempfile.mkdtemp(prefix="taskhub-insthome-")
data = tempfile.mkdtemp(prefix="taskhub-instdata-")
root = "Software\\TaskHubTest\\" + os.path.basename(home)
RUN = root + "\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"
APPROVED = root + "\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run"
UNINST = root + "\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\TaskHub"
PROTO = root + "\\Software\\Classes\\taskhub"
lnk = os.path.join(home, "Start Menu", "Programs", "TaskHub.lnk")


def selftest(*a, d=None):
    env = dict(os.environ, TASKHUB_TEST_HOME=home)
    p = subprocess.run([exe, "--data", d or data, "--selftest-install", *a], env=env, capture_output=True, timeout=60)
    return p.returncode


def reg(path, name):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as k:
            return winreg.QueryValueEx(k, name)[0]
    except OSError:
        return None


def reg_exists(path):
    try:
        winreg.OpenKey(winreg.HKEY_CURRENT_USER, path).Close()
        return True
    except OSError:
        return False


def reg_set(path, name, value, kind=winreg.REG_SZ):
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, path) as k:
        if value is None:
            try:
                winreg.DeleteValue(k, name)
            except OSError:
                pass
        else:
            winreg.SetValueEx(k, name, 0, kind, value)


def reg_delete_tree(path):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path, 0, winreg.KEY_ALL_ACCESS) as k:
            while True:
                try:
                    sub = winreg.EnumKey(k, 0)
                except OSError:
                    break
                reg_delete_tree(path + "\\" + sub)
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)
    except OSError:
        pass


def config():
    try:
        with open(os.path.join(data, "config.json"), encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def shortcut(p):
    ps = f"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{p}'); $s.TargetPath; $s.WorkingDirectory; $s.IconLocation"
    out = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True, timeout=60).stdout
    return [x.strip() for x in out.strip().splitlines()]


# the real entries must not change (read only: the user's own Run value and Start-menu shortcut)
real_lnk = os.path.join(os.environ["APPDATA"], "Microsoft", "Windows", "Start Menu", "Programs", "TaskHub.lnk")
real_run = reg("Software\\Microsoft\\Windows\\CurrentVersion\\Run", "TaskHub")
real_lnk_m = os.path.getmtime(real_lnk) if os.path.exists(real_lnk) else None

try:
    # ---------------------------------------------------------------- register / shortcut
    check("register: exit 0", selftest("register") == 0)
    check("register: Run entry", reg(RUN, "TaskHub") == f'"{exe}"', reg(RUN, "TaskHub"))
    check("register: uninstall entry", (reg(UNINST, "InstallLocation") or "").rstrip("\\").lower() == exe_dir.rstrip("\\").lower(), reg(UNINST, "InstallLocation"))
    check("register: taskhub:// protocol", reg_exists(PROTO))
    path_val = reg(root + "\\Environment", "Path") or ""
    check("register: folder on PATH", any(x.rstrip("\\").lower() == exe_dir.rstrip("\\").lower() for x in path_val.split(";")), path_val)
    check("register: Start-menu shortcut created", os.path.exists(lnk), lnk)
    if os.path.exists(lnk):
        sc = shortcut(lnk)
        check("shortcut: target = installed exe, working dir = its folder, icon exe,0",
              len(sc) == 3 and sc[0].lower() == exe.lower() and sc[1].lower() == exe_dir.lower() and sc[2].lower() == exe.lower() + ",0", sc)

    # ---------------------------------------------------------------- autostart self-healing
    reg_set(RUN, "TaskHub", None)
    check("ensure-autostart (entry removed): exit 0", selftest("ensure-autostart") == 0)
    check("ensure-autostart: removed entry restored", reg(RUN, "TaskHub") == f'"{exe}"', reg(RUN, "TaskHub"))
    reg_set(RUN, "TaskHub", '"C:\\elsewhere\\TaskHubHost.exe"')
    selftest("ensure-autostart")
    check("ensure-autostart: changed entry restored", reg(RUN, "TaskHub") == f'"{exe}"', reg(RUN, "TaskHub"))
    check("ensure-autostart: setting not turned off", config().get("autostart") is not False, config())

    disabled = bytes([3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])
    reg_set(APPROVED, "TaskHub", disabled, winreg.REG_BINARY)
    check("ensure-autostart (off in Task Manager): exit 0", selftest("ensure-autostart") == 0)
    check("off in Task Manager: stays off (StartupApproved kept)", reg(APPROVED, "TaskHub") == disabled, reg(APPROVED, "TaskHub"))
    check("off in Task Manager: TaskHub's own setting follows (autostart false)", config().get("autostart") is False, config())

    check("unknown step: exit 2, nothing done", selftest("bogus") == 2)

    # ---------------------------------------------------------------- unregister
    check("unregister: exit 0", selftest("unregister") == 0)
    check("unregister: Run entry removed", reg(RUN, "TaskHub") is None, reg(RUN, "TaskHub"))
    check("unregister: uninstall entry removed", not reg_exists(UNINST))
    check("unregister: protocol removed", not reg_exists(PROTO))
    path_val = reg(root + "\\Environment", "Path") or ""
    check("unregister: folder off PATH", not any(x.rstrip("\\").lower() == exe_dir.rstrip("\\").lower() for x in path_val.split(";")), path_val)
    check("unregister: Start-menu shortcut removed", not os.path.exists(lnk))

    # ---------------------------------------------------------------- Store data take-over: only missing files
    src, dst = tempfile.mkdtemp(prefix="taskhub-pkgdata-"), tempfile.mkdtemp(prefix="taskhub-realdata-")
    for rel, text in (("state.json", "package"), ("sub\\b.txt", "b"), ("webview\\cache.bin", "w"), ("only-pkg.json", "p")):
        os.makedirs(os.path.dirname(os.path.join(src, rel)), exist_ok=True)
        with open(os.path.join(src, rel), "w") as f:
            f.write(text)
    with open(os.path.join(dst, "state.json"), "w") as f:
        f.write("real, newer")
    check("copy-missing: exit 0", selftest("copy-missing", src, dst) == 0)
    rd = lambda p: open(p).read() if os.path.exists(p) else None
    check("copy-missing: existing real data not overwritten", rd(os.path.join(dst, "state.json")) == "real, newer")
    check("copy-missing: missing files copied", rd(os.path.join(dst, "sub", "b.txt")) == "b" and rd(os.path.join(dst, "only-pkg.json")) == "p")
    check("copy-missing: browser cache skipped", not os.path.exists(os.path.join(dst, "webview")))
    shutil.rmtree(src, True); shutil.rmtree(dst, True)

    # ---------------------------------------------------------------- purge: data folder + Credential Manager tokens
    purge_data = tempfile.mkdtemp(prefix="taskhub-purgedata-")
    with open(os.path.join(purge_data, "config.json"), "w") as f:
        f.write("{}")
    with open(os.path.join(purge_data, "guard_log.json"), "w") as f:   # what the balance guard stopped (2.9)
        f.write('{"entries": []}')
    # Secrets.Target: a non-default data folder gets "@" + the first 10 hex of SHA-256(lower-case full path, 8.3 names
    # expanded like .NET's Path.GetFullPath) - test-only names
    targets = [vault.target(purge_data, "github"), vault.target(purge_data, "telegram")]
    no_vault = vault.probe()
    if no_vault:
        print(f"SKIP purge: test tokens written (Credential Manager unavailable: {no_vault})")
    else:
        for t in targets:
            vault.write(t, "test")
        check("purge: test tokens written", all(vault.exists(t) for t in targets))
    check("purge: exit 0", selftest("purge", d=purge_data) == 0)
    if no_vault:
        print(f"SKIP purge: GitHub and Telegram tokens deleted (Credential Manager unavailable: {no_vault})")
    else:
        check("purge: GitHub and Telegram tokens deleted", not any(vault.exists(t) for t in targets))
    check("purge: data folder deleted (also guard_log.json)", not os.path.exists(purge_data))
    for t in targets:
        vault.delete(t)
    shutil.rmtree(purge_data, True)

    # ---------------------------------------------------------------- stop a hub: quit event first, no kill needed
    hub_data = tempfile.mkdtemp(prefix="taskhub-quitdata-")
    with open(os.path.join(hub_data, "config.json"), "w") as f:
        json.dump({"toasts": False, "autostart": False}, f)
    url = "http://127.0.0.1:47893"
    hub = subprocess.Popen([exe, "--port", "47893", "--data", hub_data, "--no-tray"])
    try:
        up = False
        for _ in range(60):
            try:
                urllib.request.urlopen(url + "/api/health", timeout=1).read()
                up = True
                break
            except OSError:
                time.sleep(0.5)
        check("quit: own hub on 47893 started", up)
        req = urllib.request.Request(url + "/api/tasks", data=json.dumps({"name": "quit-test-task"}).encode(),
                                     headers={"Content-Type": "application/json", "X-TaskHub": "1"}, method="POST")
        urllib.request.urlopen(req, timeout=5).read()
        t0 = time.time()
        check("stop <pid>: exit 0", selftest("stop", str(hub.pid)) == 0)
        try:
            rc = hub.wait(10)
        except subprocess.TimeoutExpired:
            rc = None
        check("stop: hub quit by itself (exit code 0, not killed)", rc == 0, rc)
        check("stop: within the 5 s grace time", time.time() - t0 < 8, time.time() - t0)
        state = os.path.join(hub_data, "state.json")
        check("stop: tasks saved on quit", os.path.exists(state) and "quit-test-task" in open(state, encoding="utf-8").read())
    finally:
        if hub.poll() is None:
            hub.kill()
            hub.wait(10)
        shutil.rmtree(hub_data, True)

finally:
    reg_delete_tree(root)
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, "Software\\TaskHubTest")   # only if empty
    except OSError:
        pass
    shutil.rmtree(home, True)
    shutil.rmtree(data, True)

check("real Run entry untouched", reg("Software\\Microsoft\\Windows\\CurrentVersion\\Run", "TaskHub") == real_run)
check("real Start-menu shortcut untouched", (os.path.getmtime(real_lnk) if os.path.exists(real_lnk) else None) == real_lnk_m)

print(f"\n{'ALL PASSED' if fails == 0 else f'{fails} FAILED'}")
sys.exit(1 if fails else 0)
