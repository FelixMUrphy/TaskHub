"""Check (by hand, hidden Edge; not in run_tests): the Messengers tab keeps typed Telegram and Discord tokens apart across a redraw and keeps the focus on the right field.
Isolated hub (47898, temp data) and hidden Edge; only the WebView2 host is a stub. Argument: TaskHubHost.exe."""
import json, os, shutil, subprocess, sys, tempfile, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import panel_render as P

exe = sys.argv[1]
data, home, profile = tempfile.mkdtemp(prefix="taskhub-msg-"), tempfile.mkdtemp(prefix="taskhub-msgh-"), tempfile.mkdtemp(prefix="taskhub-msge-")
json.dump({"toasts": False, "autostart": False, "update_check": False, "language": "uk"}, open(os.path.join(data, "config.json"), "w"))
env = dict(os.environ, TASKHUB_TEST_HOME=home)
env.pop("TASKHUB_URL", None)
hub = subprocess.Popen([exe, "--port", str(P.PORT), "--data", data, "--no-tray"], env=env)
edge = None
try:
    P.check("hub", P.until(lambda: P.call("GET", "/api/health").get("ok")))
    edge = subprocess.Popen([P.EDGE, "--headless=new", "--disable-gpu", f"--remote-debugging-port={P.CDP}", f"--user-data-dir={profile}", "--no-first-run", "--disable-extensions",
                             "--window-size=1000,1400", "about:blank"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    page = P.until(lambda: next((t for t in P.get_json(f"http://127.0.0.1:{P.CDP}/json") if t.get("type") == "page" and t.get("webSocketDebuggerUrl")), None), 45)
    ws = P.WS(page["webSocketDebuggerUrl"])
    ws.send("Runtime.enable")
    ws.send("Page.enable")
    ws.send("Page.addScriptToEvaluateOnNewDocument", {"source": "window.__posted=[];window.chrome=window.chrome||{};window.chrome.webview={postMessage:m=>window.__posted.push(m),addEventListener(){}};"})
    ws.send("Page.navigate", {"url": P.BASE + "/ui#token=" + os.environ["TASKHUB_TOKEN"]})
    P.until(lambda: ws.eval("typeof renderSet==='function' && !!data"), 20)
    texts = {"intro": "steps", "token": "Token", "connect": "Connect"}
    st = json.dumps({"settings_state": {"telegram": {"configured": False, "linked": False, "texts": texts}, "discord": {"configured": False, "linked": False, "texts": texts, "help_url": "https://example.invalid"},
                                        "connectors": [], "agents": [], "labels": {}, "version": "x", "edition": "exe"}})
    ws.eval(f"setTab='messengers'; document.querySelector('#cfg').click(); settingsRoom({{set:700,room:0,over:false}}); settingsReply({st})")
    P.check("both token fields on the Messengers tab", P.until(lambda: ws.eval("!!document.querySelector('input[data-tgtok]') && !!document.querySelector('input[data-dctok]')"), 10))
    ws.eval("document.querySelector('input[data-tgtok]').value='TG-TYPED'; document.querySelector('input[data-dctok]').value='DC-TYPED'; document.querySelector('input[data-dctok]').focus(); reSet()")
    vals = ws.eval("[document.querySelector('input[data-tgtok]').value, document.querySelector('input[data-dctok]').value]")
    P.check("a redraw keeps each typed token in its own field", vals == ["TG-TYPED", "DC-TYPED"], vals)
    P.check("...and the focus on the Discord field", ws.eval("document.activeElement.hasAttribute('data-dctok')"), ws.eval("document.activeElement.outerHTML.slice(0,80)"))
    ws.eval("setTab='look'; renderSet(); setTab='messengers'; renderSet()")
    vals = ws.eval("[document.querySelector('input[data-tgtok]').value, document.querySelector('input[data-dctok]').value]")
    P.check("...and across a visit to another tab", vals == ["TG-TYPED", "DC-TYPED"], vals)
finally:
    hub.kill()
    if edge:
        edge.kill()
    time.sleep(1)
    for d in (data, home, profile):
        shutil.rmtree(d, ignore_errors=True)
print("fails", P.fails)
sys.exit(1 if P.fails else 0)
