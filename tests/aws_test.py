"""AWS connector against a mock of EC2 (127.0.0.1:47889; the test hub's config.json points connectors.aws.api there). Every request
is checked by the mock's own SigV4 signer. Instances become tasks (aws-<id>) with paging (also an empty page with a token), Regions from
DescribeRegions or the user's list, per-Region states (no access, throttling, 500, unreadable answers) that never declare instances gone,
stopped / terminated -> done, "forgotten?" with its button, the two-part key kept in the Credential Manager and nowhere else, temporary and
malformed keys refused, a bad key changing nothing, a revoked key, a wrong clock not called a bad key, nothing polled when off."""
import json, os, threading, time, urllib.error, urllib.parse, urllib.request
import vault
from aws_mock import Mock

URL = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47899")
DATA = os.environ.get("TASKHUB_DATA", os.path.join(os.environ.get("TEMP", "."), "taskhub-tests"))
PORT = 47889
KID, SECRET = "AKIAMOCKMOCKMOCK0001", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYMOCKKEY01"
KID2, SECRET2 = "AKIAMOCKMOCKMOCK0002", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYMOCKKEY02"
NS = 'xmlns="http://ec2.amazonaws.com/doc/2016-11-15/"'
XML = "text/xml;charset=UTF-8"
fails = 0
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
lock = threading.RLock()
# what the mock account holds: Region -> instances; faults: Region -> (status, error code) or "garbage"
world = {"us-east-1": [], "eu-west-1": [], "ap-south-1": []}
enabled = ["us-east-1", "eu-west-1", "ap-south-1"]
faults = {}
regions_fault = {"on": None}
page_size = {"n": 2}


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


def inst(iid, state="running", name=None, age=600, itype="t3.micro"):
    return {"id": iid, "state": state, "name": name, "launch": time.strftime("%Y-%m-%dT%H:%M:%S.000Z", time.gmtime(time.time() - age)), "type": itype}


def inst_xml(i):
    # element order differs from the docs' sample on purpose: a real parser must not care
    tags = f"<tagSet><item><key>Other</key><value>x</value></item><item><key>Name</key><value>{i['name']}</value></item></tagSet>" if i["name"] else "<tagSet/>"
    return (f"<item>{tags}<launchTime>{i['launch']}</launchTime><instanceState><code>16</code><name>{i['state']}</name></instanceState>"
            f"<instanceType>{i['type']}</instanceType><instanceId>{i['id']}</instanceId></item>")


def err_xml(code):
    return f"<?xml version=\"1.0\"?><Response><Errors><Error><Code>{code}</Code><Message>secret detail for {code}</Message></Error></Errors><RequestID>r</RequestID></Response>".encode()


def handler(rec):
    q = dict(urllib.parse.parse_qsl(rec["query"]))
    region = rec["region"]
    with lock:
        if q.get("Action") == "DescribeRegions":
            if regions_fault["on"]:
                st, code = regions_fault["on"]
                return st, XML, err_xml(code)
            items = "".join(f"<item><regionName>{r}</regionName><regionEndpoint>ec2.{r}.amazonaws.com</regionEndpoint></item>" for r in enabled)
            return 200, XML, f"<DescribeRegionsResponse {NS}><requestId>r</requestId><regionInfo>{items}</regionInfo></DescribeRegionsResponse>".encode()
        if q.get("Action") != "DescribeInstances":
            return 400, XML, err_xml("InvalidAction")
        f = faults.get(region)
        if f == "garbage":
            return 200, XML, b"<not-closed"
        if f == "wrong_root":
            return 200, XML, b"<SomethingElse/>"
        if f:
            return f[0], XML, err_xml(f[1])
        items = list(world.get(region, []))
        n = page_size["n"]
        # page 0: first n; an empty page in the middle (allowed by the docs: keep going until there is no token)
        tok = q.get("NextToken", "")
        if tok.startswith("empty/"):
            start, page, nxt = int(tok[6:]), [], tok[6:]
        else:
            start = int(tok or 0)
            page = items[start:start + n]
            nxt = str(start + n) if start + n < len(items) else None
            if nxt:
                nxt = "empty/" + nxt   # a value that needs URL encoding, and an empty page next
        body = f"<reservationSet>{''.join('<item><reservationId>r-1</reservationId><ownerId>123456789012</ownerId><instancesSet>' + inst_xml(i) + '</instancesSet></item>' for i in page)}</reservationSet>"
        tk = f"<nextToken>{nxt}</nextToken>" if nxt else ""
        return 200, XML, f"<DescribeInstancesResponse {NS}><requestId>r</requestId>{body}{tk}</DescribeInstancesResponse>".encode()


def call(method, path, body=None):
    req = urllib.request.Request(URL + path, json.dumps(body).encode() if body is not None else None, method=method,
                                 headers={"Content-Type": "application/json", "X-TaskHub": "1"})
    try:
        with op.open(req, timeout=40) as r:
            return r.status, json.loads(r.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"{}")


def task(tid):
    s, r = call("GET", f"/api/tasks/{tid}")
    return r if s == 200 else None


def wait(pred, timeout=15):
    end = time.time() + timeout
    while time.time() < end:
        v = pred()
        if v:
            return v
        time.sleep(0.2)
    return None


def when(tid, cond, timeout=15):
    return wait(lambda: (lambda x: x if x and cond(x) else None)(task(tid)), timeout)


def state():
    return call("GET", "/api/connectors/aws")[1]


def row():
    return next((c for c in call("GET", "/api/connectors?view=settings")[1].get("connectors", []) if c.get("id") == "aws"), None)


def clean():
    for t in call("GET", "/api/tasks")[1].get("tasks", []):
        if str(t.get("id", "")).startswith("aws-"):
            call("DELETE", f"/api/tasks/{t['id']}?force=1")


NO_VAULT = vault.probe()
TARGET = vault.target(DATA, "aws")
mock = Mock(PORT, {KID: SECRET, KID2: SECRET2}, handler)

with mock:
    # ---------------------------------------------------------------- off: nothing leaves
    s, r = call("POST", "/api/connectors/aws", {"enabled": False})
    check("off by default, no key", s == 200 and r["status"] == "off" and r["key_set"] is False, r)
    time.sleep(0.5)
    check("off: no requests", not mock.requests, mock.requests[:1])

    # ---------------------------------------------------------------- keys
    for bad, why in [("nonsense", "no secret"), (KID, "id only"), (f"{KID}:short", "short secret"), (f"lower-case-id-123456:{SECRET}", "id charset")]:
        s, r = call("POST", "/api/connectors/aws", {"key": bad})
        check(f"malformed key refused, nothing sent: {why}", s == 400 and not mock.requests, (s, r))
    s, r = call("POST", "/api/connectors/aws", {"key": f"ASIAMOCKMOCKMOCK0001:{SECRET}"})
    check("temporary key refused with the reason, nothing sent", s == 400 and "temporary" in r.get("error", "") and not mock.requests, (s, r))
    s, r = call("POST", "/api/connectors/aws", {"regions": ["us-east-1.evil.example/#"]})
    check("a Region that isn't a Region name is refused", s == 400, (s, r))
    s, r = call("POST", "/api/connectors/aws", {"forgotten_hours": -1})
    check("bad forgotten_hours refused", s == 400, (s, r))

    if NO_VAULT:
        print(f"SKIP key and task tests: no usable Credential Manager here ({NO_VAULT})")
    else:
        vault.delete(TARGET)
        s, r = call("POST", "/api/connectors/aws", {"enabled": True, "key": f"AKIAUNKNOWNUNKNOWN01:{SECRET}"})
        check("unknown key rejected, not saved, connector stays off", s == 400 and "rejected" in r.get("error", "") and not vault.exists(TARGET)
              and state()["enabled"] is False, (s, r))
        s, r = call("POST", "/api/connectors/aws", {"enabled": True, "key": f"{KID}:wrong-secret-wrong-secret"})
        check("wrong secret rejected, not saved", s == 400 and not vault.exists(TARGET), (s, r))

        with lock:
            world["us-east-1"] = [inst("i-0a1", name="video render"), inst("i-0a2", name="build box"), inst("i-0a3"), inst("i-0a4", state="stopped", name="backup"),
                                  inst("i-0a5", state="terminated", name="old")]
            world["eu-west-1"] = [inst("i-0b1", state="pending", name="classifier")]
        mock.requests.clear()
        s, r = call("POST", "/api/connectors/aws", {"enabled": True, "key": f" {KID}:{SECRET} ", "list_interval": 0.2, "forgotten_hours": 72})
        check("key accepted, trimmed", s == 200 and r["key_set"] is True and r["key_check"] == "ok" and r["key_check_text"] == "Connected", (s, r))
        check("kept in the Credential Manager as id:secret", vault.read(TARGET) == f"{KID}:{SECRET}", TARGET)
        check("the first request is DescribeRegions from us-east-1, signed for ec2", mock.requests and "Action=DescribeRegions" in mock.requests[0]["query"]
              and mock.requests[0]["region"] == "us-east-1" and mock.requests[0]["service"] == "ec2", mock.requests[:1])

        # ------------------------------------------------------------ instances -> tasks
        t1 = when("aws-i-0a1", lambda t: t["state"] == "running")
        check("running instance -> active task named from its Name tag", t1 and t1["name"] == "video render" and t1["source"] == "aws" and t1["project"] == "AWS", t1)
        check("task card: Region and type", t1 and "us-east-1" in t1.get("description", "") and "t3.micro" in t1.get("description", ""), t1)
        t3 = when("aws-i-0a3", lambda t: t["state"] == "running")
        check("paged past an empty page, and no Name tag -> the instance id", t3 and t3["name"] == "i-0a3", t3)
        check("started from launchTime, not from now", t1 and time.time() - t1["started"] > 500, t1 and t1["started"])
        tb = when("aws-i-0b1", lambda t: t["state"] == "running")
        check("pending instance in another Region -> active task", tb is not None, tb)
        t4 = when("aws-i-0a4", lambda t: t["state"] == "done")
        check("first seen stopped -> done, already seen, no toast asked", t4 and t4.get("notify", {}).get("finish") is False, t4)
        check("first seen terminated -> no task", task("aws-i-0a5") is None)
        check("every request's signature held", wait(lambda: len(mock.requests) > 8) and all(q.get("verified") for q in mock.requests), [q for q in mock.requests if not q.get("verified")][:1])
        check("paging token sent URL-encoded", any("NextToken=empty%2F" in q["query"] for q in mock.requests), [q["query"] for q in mock.requests][:6])
        check("every list request is paginated (MaxResults)", all("MaxResults=" in q["query"] for q in mock.requests if "DescribeInstances" in q["query"]))
        st = state()
        check("state: Regions read and running count", st["regions"].get("us-east-1", {}).get("state") == "ok" and st["running"] >= 3 and st["regions"]["us-east-1"]["count"] == 5, st)

        # ------------------------------------------------------------ the secret stays home
        cfg = open(os.path.join(DATA, "config.json"), encoding="utf-8").read()
        check("neither part of the key in config.json", KID not in cfg and SECRET not in cfg)
        rw = row()
        dump = json.dumps(state()) + json.dumps(rw) + json.dumps(call("GET", "/api/tasks")[1])
        check("neither part of the key in API replies", KID not in dump and SECRET not in dump)
        check("account id not on tasks or state", "123456789012" not in dump)
        check("secret not in any request header or URL", not any(SECRET in str(q["headers"]) or SECRET in q["query"] for q in mock.requests))
        check("settings row: two key fields, Regions list, field, notes, help", rw and len(rw.get("key_parts", [])) == 2 and rw["list"]["key"] == "regions"
              and rw["fields"][0]["key"] == "forgotten_hours" and len(rw["notes"]) == 2 and "docs/keys/aws" in rw["help_url"], rw)

        # ------------------------------------------------------------ a Region's trouble is that Region's
        for fault, want, label in [((403, "AuthFailure"), "no_access", "AuthFailure from one Region"), ((403, "UnauthorizedOperation"), "no_access", "denied in one Region"),
                                   ((503, "RequestLimitExceeded"), "throttled", "throttled"), ((500, "InternalError"), "error", "500"), ("garbage", "error", "unreadable 200"),
                                   ("wrong_root", "error", "a 200 that isn't the list")]:
            with lock:
                faults["eu-west-1"] = fault
                world["eu-west-1"] = []   # gone from the world, but the answer can't say so
            got = wait(lambda: state()["regions"].get("eu-west-1", {}).get("state") == want)
            time.sleep(0.8)
            t = task("aws-i-0b1")
            check(f"{label}: Region state {want}, instance not declared gone, key still fine", got and t and t["state"] == "running" and state()["key_check"] == "ok", (state()["regions"].get("eu-west-1"), t and t["state"]))
        check("the Region's trouble is in the summary", "eu-west-1" in state()["summary"], state()["summary"])
        check("the other Regions keep being read", state()["regions"]["us-east-1"]["state"] == "ok")
        dump = json.dumps(state())
        check("a service's error message isn't repeated", "secret detail" not in dump, dump[:300])

        # ------------------------------------------------------------ stopped / gone
        with lock:
            faults.clear()
            world["eu-west-1"] = [inst("i-0b1", name="classifier")]
            next(i for i in world["us-east-1"] if i["id"] == "i-0a2")["state"] = "stopped"
        t = when("aws-i-0a2", lambda t: t["state"] == "done")
        check("running -> stopped: task done, with a toast", t and t.get("notify", {}).get("finish") is True and "topped" in (t.get("message") or ""), t)
        with lock:
            next(i for i in world["us-east-1"] if i["id"] == "i-0a2")["state"] = "running"
        t = when("aws-i-0a2", lambda t: t["state"] == "running")
        check("started again: active again", t is not None, task("aws-i-0a2"))
        with lock:
            world["us-east-1"] = [i for i in world["us-east-1"] if i["id"] != "i-0a2"]
        t = when("aws-i-0a2", lambda t: t["state"] == "done")
        check("gone from the list -> terminated", t and "erminated" in (t.get("message") or ""), t)

        # ------------------------------------------------------------ forgotten
        with lock:
            world["us-east-1"].append(inst("i-0c1", name="forgotten box", age=100 * 3600))
        t = when("aws-i-0c1", lambda t: t.get("attention"))
        check("running longer than forgotten_hours -> needs you", t and "Forgotten" in t["attention"] and "4 " in t["attention"], t and t.get("attention"))
        check("the button offered, not in toasts", t and [c["id"] for c in t["controls"]] == ["keep"] and not t["controls"][0].get("toast"), t and t["controls"])
        log = when("aws-i-0c1", lambda t: any("Forgotten" in str(x) for x in t.get("log", [])))
        check("said once in its log", log and sum("Forgotten" in str(x) for x in log["log"]) == 1, log and log.get("log"))
        s, r = call("POST", "/api/tasks/aws-i-0c1/controls/keep", {})
        t = when("aws-i-0c1", lambda t: not t.get("attention"))
        check("\"needed\": no more asking, can be undone", s == 200 and t and [c["id"] for c in t["controls"]] == ["remind"] and "i-0c1" in state()["forgotten_exempt"], (s, r, t and t["controls"]))
        s, r = call("POST", "/api/tasks/aws-i-0c1/controls/remind", {})
        t = when("aws-i-0c1", lambda t: t.get("attention"))
        check("\"ask again\" brings it back, no second log line", s == 200 and t and sum("Forgotten" in str(x) for x in t.get("log", [])) == 1, t and t.get("log"))
        s, r = call("POST", "/api/tasks/aws-i-0c1/controls/stop", {})
        check("no action on the account exists", s in (400, 404) and not any(q["method"] != "GET" for q in mock.requests), (s, r))
        call("POST", "/api/connectors/aws", {"forgotten_hours": 0})
        t = when("aws-i-0c1", lambda t: not t.get("attention"), 5)
        check("0 hours = never asks", t is not None, task("aws-i-0c1"))
        call("POST", "/api/connectors/aws", {"forgotten_hours": 72})

        # ------------------------------------------------------------ the user's own Regions
        s, r = call("POST", "/api/connectors/aws", {"regions": ["EU-WEST-1", "eu-west-1"]})
        check("own Regions: normalized, deduplicated", s == 200 and r["regions_chosen"] == ["eu-west-1"], (s, r))
        t = when("aws-i-0a1", lambda t: t["state"] == "done")
        check("a Region no longer watched: let go, not called stopped", t and "No longer watched" in (t.get("message") or ""), t)
        mock.requests.clear()   # what the loop asked before the change took effect doesn't count
        time.sleep(0.8)
        n = list(mock.requests)
        check("only the chosen Region is listed", n and all(q["region"] == "eu-west-1" for q in n if "DescribeInstances" in q["query"]), {q["region"] for q in n})
        rw = row()
        check("the list shows the Region with what was read", rw["list"]["items"] and rw["list"]["items"][0]["v"] == "eu-west-1" and "instance" in rw["list"]["items"][0]["note"], rw["list"])
        s, r = call("POST", "/api/connectors/aws", {"regions": []})
        check("empty list: every enabled Region again", s == 200 and r["regions_chosen"] == [], r)
        when("aws-i-0a1", lambda t: t["state"] == "running")

        # ------------------------------------------------------------ settings don't cause a burst
        time.sleep(0.3)
        call("POST", "/api/connectors/aws", {"list_interval": 3600})
        time.sleep(1.0)
        mock.requests.clear()
        for h in (70, 71, 72):
            call("POST", "/api/connectors/aws", {"forgotten_hours": h})
        time.sleep(0.8)
        check("changing a number sends no requests", not [q for q in mock.requests if "DescribeInstances" in q["query"]], [q["query"] for q in mock.requests][:3])
        call("POST", "/api/connectors/aws", {"list_interval": 0.2})
        time.sleep(0.5)

        # ------------------------------------------------------------ DescribeRegions refused
        with lock:
            regions_fault["on"] = (403, "UnauthorizedOperation")
        s, r = call("POST", "/api/connectors/aws", {"key": f"{KID}:{SECRET}"})
        check("real key without DescribeRegions: saved, says what is missing", s == 200 and r["key_check"] == "denied" and "ec2:DescribeRegions" in (r["key_check_text"] or "") and vault.exists(TARGET), (s, r))
        wait(lambda: state()["key_check"] == "denied" and list(state()["regions"]) == ["us-east-1"])
        check("...and only us-east-1 is watched", list(state()["regions"]) == ["us-east-1"], state()["regions"])
        check("...the how-to link stays reachable", row()["key_check"] == "denied", row()["key_check"])
        s, r = call("POST", "/api/connectors/aws", {"regions": ["eu-west-1"]})
        got = wait(lambda: state()["key_check"] == "ok" and list(state()["regions"]) == ["eu-west-1"])
        check("own Regions don't need DescribeRegions", got, state())
        call("POST", "/api/connectors/aws", {"regions": []})
        with lock:
            regions_fault["on"] = None

        # ------------------------------------------------------------ a wrong clock isn't a bad key
        with lock:
            regions_fault["on"] = (403, "RequestExpired")
        s, r = call("POST", "/api/connectors/aws", {"key": f"{KID}:{SECRET}"})
        check("RequestExpired: saved, tells to check the clock", s == 200 and r["key_check"] == "clock" and "clock" in r["key_check_text"], (s, r))
        with lock:
            regions_fault["on"] = (403, "AuthFailure")
        mock.date_offset = 3600
        s, r = call("POST", "/api/connectors/aws", {"key": f"{KID}:{SECRET}"})
        check("AuthFailure with the clock an hour off: not called a bad key", s == 200 and r["key_check"] == "clock", (s, r))
        mock.date_offset = 0
        with lock:
            regions_fault["on"] = None
        check("clock fixed: connected again", wait(lambda: state()["key_check"] == "ok"), state())

        # ------------------------------------------------------------ the service unavailable when saving
        with lock:
            regions_fault["on"] = (503, "Unavailable")
        s, r = call("POST", "/api/connectors/aws", {"key": f"{KID}:{SECRET}"})
        check("503 when checking: saved, checked again later", s == 200 and r["key_check"] == "unavailable" and vault.exists(TARGET), (s, r))
        with lock:
            regions_fault["on"] = None
        check("...and it recovers", wait(lambda: state()["key_check"] == "ok"), state())

        # ------------------------------------------------------------ revoked while in use
        before = task("aws-i-0a1")
        with lock:
            regions_fault["on"] = (403, "AuthFailure")
        wait(lambda: state()["key_check"] == "revoked")
        st = state()
        check("key revoked: said so, how-to link back, tasks not declared gone", st["key_check"] == "revoked" and row()["key_check"] == "revoked" and task("aws-i-0a1")["state"] == before["state"], st)
        time.sleep(0.5)
        mock.requests.clear()
        time.sleep(0.8)
        check("revoked: instances aren't asked for", not [q for q in mock.requests if "DescribeInstances" in q["query"]])
        with lock:
            regions_fault["on"] = None

        # ------------------------------------------------------------ a restart of the loop, another key
        s, r = call("POST", "/api/connectors/aws", {"key": f"{KID}:{SECRET}"})
        when("aws-i-0a1", lambda t: t["state"] == "running")
        s, r = call("POST", "/api/connectors/aws", {"key": f"AKIABAD0BAD0BAD0BAD0:{SECRET}"})
        check("a rejected replacement keeps the old key and tasks", s == 400 and vault.read(TARGET) == f"{KID}:{SECRET}" and task("aws-i-0a1")["state"] == "running", (s, r))
        with lock:
            faults["us-east-1"] = (500, "InternalError")   # the new key's first read can't bring the task back before it is looked at
        s, r = call("POST", "/api/connectors/aws", {"key": f"{KID2}:{SECRET2}"})
        t = task("aws-i-0a1")
        with lock:
            faults.clear()
        check("another key (maybe another account): what was followed is let go first", s == 200 and t["state"] == "done" and "No longer watched" in (t.get("message") or ""), t)
        check("...then the same instances are followed under the new key", when("aws-i-0a1", lambda t: t["state"] == "running") is not None, task("aws-i-0a1"))

        # ------------------------------------------------------------ off, forget
        s, r = call("POST", "/api/connectors/aws", {"enabled": False})
        t = task("aws-i-0a1")
        check("off keeps the key, lets the tasks go", s == 200 and r["key_set"] is True and vault.exists(TARGET) and t["state"] == "done" and "No longer watched" in (t.get("message") or ""), (r, t and t.get("message")))
        time.sleep(0.4)
        mock.requests.clear()
        time.sleep(1.2)
        check("nothing polled when off", not mock.requests, mock.requests[:1])
        s, r = call("POST", "/api/connectors/aws", {"forget_key": True})
        check("forget deletes the key", s == 200 and r["key_set"] is False and not vault.exists(TARGET), r)
        clean()
        call("POST", "/api/connectors/aws", {"list_interval": 600, "forgotten_hours": 72})

print("AWS TESTS " + ("PASSED" if not fails else f"FAILED ({fails})"))
raise SystemExit(1 if fails else 0)
