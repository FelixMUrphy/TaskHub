"""AWS connector against a mock of EC2 (127.0.0.1:47889; the test hub's config.json points connectors.aws.api there). Every request
is checked by the mock's own SigV4 signer. Instances become tasks (aws-<id>) with paging (also an empty page with a token), Regions from
DescribeRegions or the user's list, per-Region states (no access, throttling, 500, unreadable answers) that never declare instances gone,
stopped / terminated -> done, "forgotten?" with its button, the two-part key kept in the Credential Manager and nowhere else, temporary and
malformed keys refused, a bad key changing nothing, a revoked key, a wrong clock not called a bad key, nothing polled when off."""
import json, os, shutil, subprocess, tempfile, threading, time, urllib.error, urllib.parse, urllib.request
import vault
from aws_mock import Mock

URL = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47899")
DATA = os.environ.get("TASKHUB_DATA", os.path.join(os.environ.get("TEMP", "."), "taskhub-tests"))
PORT = 47889
LOG = "taskhub.log"
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
# jobs: Region -> SageMaker jobs; (Region, queue) -> Batch jobs; service -> fault (status, error type) or "dns"-free stand-ins
training = {}
describe = {}
batch = {}
queues = {}
job_faults = {}
describe_faults = {}          # job name -> (status, error type)
sm_queues = set()             # Batch queues of type SAGEMAKER_TRAINING: ListJobs refuses them
sm_page = {"n": 1}            # ListTrainingJobs reads a page, then filters by status (as the docs warn), so pages can be empty
batch_page = {"n": 100}
regions_delay = {"s": 0}
money = {"month": "12.3456", "forecast": "30.1", "budgets": []}
ACCOUNT = "123456789012"
ce_delay = {"s": 0}


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


def jr(code, obj):
    return code, "application/x-amz-json-1.1", json.dumps(obj).encode()


def job_handler(rec):
    region, svc = rec["region"], rec["service"]
    if svc == "ce" and ce_delay["s"]:
        time.sleep(ce_delay["s"])
    body = json.loads(rec["body"] or "{}") if svc != "sts" else {}   # STS is a form
    with lock:
        f = job_faults.get((region, svc)) or job_faults.get(svc)
        target = rec["headers"].get("x-amz-target", [""])[0]
        if svc == "ce" and target.endswith("Forecast") and job_faults.get("forecast"):
            f = job_faults["forecast"]
        if f == "drop":   # the connection breaks: nothing reached AWS
            raise ConnectionAbortedError("mock: dropped")
        if f and svc == "sts":   # the Query protocol answers in XML
            return f[0], "text/xml", f'<ErrorResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/"><Error><Type>Sender</Type><Code>{f[1]}</Code><Message>secret detail</Message></Error><RequestId>r</RequestId></ErrorResponse>'.encode()
        if f and f[1].startswith("header:"):
            return f[0], "application/json", b'{"message": "secret detail"}', {"X-Amzn-Errortype": f[1][7:]}
        if f:
            return jr(f[0], {"__type": f"com.amazon#{f[1]}", "message": f[2] if len(f) > 2 else "secret detail"})
        if svc == "sts":
            return 200, "text/xml", f'<GetCallerIdentityResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/"><GetCallerIdentityResult><Arn>arn:aws:iam::{ACCOUNT}:user/t</Arn><UserId>U</UserId><Account>{ACCOUNT}</Account></GetCallerIdentityResult></GetCallerIdentityResponse>'.encode()
        if svc == "ce" and target == "AWSInsightsIndexService.GetCostAndUsage":
            return jr(200, {"ResultsByTime": [{"TimePeriod": body["TimePeriod"], "Total": {"UnblendedCost": {"Amount": money["month"], "Unit": "USD"}}, "Estimated": True}]})
        if svc == "ce" and target == "AWSInsightsIndexService.GetCostForecast":
            return jr(200, {"Total": {"Amount": money["forecast"], "Unit": "USD"}, "ForecastResultsByTime": []})
        if svc == "budgets" and target == "AWSBudgetServiceGateway.DescribeBudgets":
            if body.get("AccountId") != ACCOUNT:
                return jr(400, {"__type": "InvalidParameterException"})
            return jr(200, {"Budgets": money["budgets"]})
        if svc == "sagemaker":
            action = rec["headers"].get("x-amz-target", [""])[0].replace("SageMaker.", "")
            if action == "DescribeTrainingJob":
                name = body["TrainingJobName"]
                if name in describe_faults:
                    return jr(describe_faults[name][0], {"__type": describe_faults[name][1]})
                d = describe.get((region, name)) or next((i for i in training.get(region, []) if i["TrainingJobName"] == name), None)
                return jr(200, d) if d else jr(400, {"__type": "ResourceNotFound", "message": "no job"})
            if action != "ListTrainingJobs":
                return jr(400, {"__type": "UnknownOperationException"})
            items = sorted(training.get(region, []), key=lambda i: -i["CreationTime"])
            if "LastModifiedTimeAfter" in body:
                items = [i for i in items if i["LastModifiedTime"] > body["LastModifiedTimeAfter"]]
            start = int(body.get("NextToken") or 0)
            page = items[start:start + sm_page["n"]]
            if "StatusEquals" in body:   # MaxResults first, then the status filter
                page = [i for i in page if i["TrainingJobStatus"] == body["StatusEquals"]]
            more = start + sm_page["n"] < len(items)
            return jr(200, {"TrainingJobSummaries": page, **({"NextToken": str(start + sm_page["n"])} if more else {})})
        if rec["path"] == "/v1/describejobqueues":
            return jr(200, {"jobQueues": [{"jobQueueName": n, "status": "VALID", "state": "ENABLED", "jobQueueType": "SAGEMAKER_TRAINING" if n in sm_queues else "ECS"} for n in queues.get(region, [])]})
        if rec["path"] == "/v1/describejobs":
            everything = [i for (r, _), js in batch.items() if r == region for i in js]
            return jr(200, {"jobs": [i for i in everything if i["jobId"] in body["jobs"]]})
        if rec["path"] == "/v1/listjobs":
            if body["jobQueue"] in sm_queues:
                return jr(400, {"__type": "ClientException", "message": "use ListServiceJobs for this queue"})
            items = sorted(batch.get((region, body["jobQueue"]), []), key=lambda i: -i["createdAt"])
            if body.get("filters"):   # with a filter jobStatus is ignored and any status comes back
                t = int(body["filters"][0]["values"][0])
                items = [i for i in items if i["createdAt"] >= t]
            else:
                items = [i for i in items if i["status"] == body.get("jobStatus", "RUNNING")]
            start = int(body.get("nextToken") or 0)
            page = items[start:start + batch_page["n"]]
            more = start + batch_page["n"] < len(items)
            return jr(200, {"jobSummaryList": page, **({"nextToken": str(start + batch_page["n"])} if more else {})})
        return jr(404, {"__type": "NotFound"})


def sm_job(name, status="InProgress", phase="Training", age=600, end=None):
    now = time.time()
    return {"TrainingJobName": name, "TrainingJobArn": f"arn:aws:sagemaker:us-east-1:123456789012:training-job/{name}", "CreationTime": now - age, "LastModifiedTime": now,
            "TrainingJobStatus": status, "SecondaryStatus": phase, **({"TrainingEndTime": end} if end else {})}


def b_job(jid, name, status, age=600, stopped=None, **kw):
    ms = lambda t: int(t * 1000)
    now = time.time()
    return {"jobId": jid, "jobName": name, "status": status, "createdAt": ms(now - age), "startedAt": ms(now - age + 5), **({"stoppedAt": ms(stopped)} if stopped else {}), **kw}


def handler(rec):
    if rec["service"] != "ec2":
        return job_handler(rec)
    if "DescribeRegions" in rec["query"] and regions_delay["s"]:
        time.sleep(regions_delay["s"])
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


def toasts():
    """What the hub has sent as notifications so far (its log has a line per toast, also with toasts off in config)."""
    time.sleep(0.6)   # they pass a queue
    try:
        return [l for l in open(os.path.join(DATA, LOG), encoding="utf-8", errors="replace") if "TOAST [" in l]
    except OSError:
        return []


def released(n, before):
    """Exactly one summary for n machines, and no toast per machine, since `before`."""
    new = toasts()[before:]
    return len(new) == 1 and f"{n} machine" in new[0], new


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
        t1 = when("aws-i-0a1", lambda t: t["state"] == "running" and t.get("description"))   # the card is filled just after it is registered
        check("running instance -> active task named from its Name tag", t1 and t1["name"] == "video render" and t1["source"] == "aws" and t1["project"] == "AWS", t1)
        check("task card: Region and type", t1 and "us-east-1" in t1.get("description", "") and "t3.micro" in t1.get("description", ""), t1)
        check("console link: the Region's console, not an unverified link to the machine", t1 and [l["url"] for l in t1.get("links", [])] == ["https://us-east-1.console.aws.amazon.com/ec2/home?region=us-east-1"], t1 and t1.get("links"))
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
        with lock:
            active_us = sum(i["state"] == "running" for i in world["us-east-1"])
        before = len(toasts())
        s, r = call("POST", "/api/connectors/aws", {"regions": ["EU-WEST-1", "eu-west-1"]})
        check("own Regions: normalized, deduplicated", s == 200 and r["regions_chosen"] == ["eu-west-1"], (s, r))
        t = when("aws-i-0a1", lambda t: t["state"] == "done")
        check("a Region no longer watched: let go, not called stopped", t and "No longer watched" in (t.get("message") or ""), t)
        ok, new = released(active_us, before)
        check("many machines let go: one summary, not a toast each", ok, new)
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
        wait(lambda: (task("aws-i-0a1") or {}).get("state") == "running")   # the list follows a saved key a moment later
        before = task("aws-i-0a1")
        warned0 = sum("no longer accepts" in l for l in toasts())
        with lock:
            regions_fault["on"] = (403, "AuthFailure")
        wait(lambda: state()["key_check"] == "revoked")
        check("key revoked later: a warning", wait(lambda: sum("no longer accepts" in l for l in toasts()) == warned0 + 1), warned0)
        st, rw, after = state(), row(), task("aws-i-0a1")
        check("key revoked: said so, how-to link back, tasks not declared gone", st["key_check"] == "revoked" and rw["key_check"] == "revoked" and after["state"] == before["state"],
              (st["key_check"], rw["key_check"], before["state"], after["state"], after.get("status")))
        time.sleep(0.5)
        mock.requests.clear()
        time.sleep(0.8)
        check("revoked: instances aren't asked for", not [q for q in mock.requests if "DescribeInstances" in q["query"]])
        with lock:
            regions_fault["on"] = None

        # ------------------------------------------------------------ long jobs
        mock.requests.clear()
        time.sleep(0.8)
        check("job services off by default: never asked", not [q for q in mock.requests if q["service"] != "ec2"], {q["service"] for q in mock.requests})
        rw = row()
        check("settings row: four switches, off", [s["key"] for s in rw["switches"]] == ["sagemaker", "batch", "cost", "budgets"] and not any(s["value"] for s in rw["switches"]), rw["switches"])
        now = time.time()
        with lock:
            training["us-east-1"] = [sm_job("classifier-train"), sm_job("old-finished", "Completed", "Completed", 90000, now - 86000) | {"LastModifiedTime": now - 86000}]
            training["eu-west-1"] = [sm_job("classifier-train", age=300)]   # the same name in another Region is another job
            describe[("us-east-1", "classifier-train")] = sm_job("classifier-train") | {"StoppingCondition": {"MaxRuntimeInSeconds": 14400}, "TrainingStartTime": now - 500}
            queues["us-east-1"] = ["render-q"]
            batch[("us-east-1", "render-q")] = [b_job("11111111-aaaa", "video render array", "PENDING", arrayProperties={"size": 10, "statusSummary": {"SUCCEEDED": 3, "FAILED": 1, "RUNNING": 6}}),
                                                b_job("22222222-bbbb", "waiting", "RUNNABLE"), b_job("33333333-cccc", "ancient", "SUCCEEDED", 90000, now - 86000)]
        s, r = call("POST", "/api/connectors/aws", {"sagemaker": True, "batch": True, "job_interval": 0.2})
        check("switches on", s == 200 and r["sagemaker"] is True and r["batch"] is True, (s, r))
        jid = lambda name, region: next((j["task"] for j in state()["jobs"] if j["id"] == name and j["region"] == region), None)
        wait(lambda: jid("classifier-train", "us-east-1") and jid("classifier-train", "eu-west-1"))
        a, b = jid("classifier-train", "us-east-1"), jid("classifier-train", "eu-west-1")
        check("same name in two Regions: two tasks", a and b and a != b, (a, b))
        t = when(a, lambda t: "Time limit" in json.dumps(t.get("widgets", [])))
        check("training job -> task: phase, time limit, Region, no invented percent", t and t["state"] == "running" and t.get("status") == "Training" and t.get("progress") is None
              and "SageMaker" in t["description"] and "us-east-1" in t["description"] and "4h" in json.dumps(t["widgets"]), t)
        check("an empty first page didn't end the list (MaxResults before the filter)", any('"NextToken"' in q["body"] for q in mock.requests if q["service"] == "sagemaker"))
        check("signed for sagemaker, JSON 1.1, target signed", any(q["service"] == "sagemaker" and q.get("verified") and "x-amz-target" in q.get("signed", []) and q["headers"]["content-type"] == ["application/x-amz-json-1.1"] for q in mock.requests))
        check("finished before the switch was on: not shown", not any(j["id"] == "old-finished" for j in state()["jobs"]), state()["jobs"])
        ta = when("aws-b-11111111-aaaa", lambda t: t.get("progress") is not None)
        check("array job: real progress from its children; a parent PENDING with running children reads RUNNING", ta and abs(ta["progress"] - 0.4) < 0.001 and "render-q" in ta["description"] and ta.get("status") == "RUNNING", ta)
        tw = when("aws-b-22222222-bbbb", lambda t: t.get("status") == "RUNNABLE")
        check("a queued job shows its state", tw is not None and tw["state"] == "running", task("aws-b-22222222-bbbb"))
        check("old finished Batch job: not shown", task("aws-b-33333333-cccc") is None)
        check("Batch requests signed", any(q["service"] == "batch" and q.get("verified") and '"jobStatus": "RUNNING"' in q["body"].replace('":"', '": "') for q in mock.requests))
        dump = json.dumps(state()) + json.dumps(call("GET", "/api/tasks")[1])
        check("no account id from the job ARN on tasks or state", "123456789012" not in dump)

        # finish: failed with the reason, stopped -> cancelled, done, a job born and finished between reads
        with lock:
            now = time.time()
            training["us-east-1"][0] = sm_job("classifier-train", "Failed", "Failed", 600, now)
            describe[("us-east-1", "classifier-train")] = training["us-east-1"][0] | {"FailureReason": "AlgorithmError:\n  out of memory", "TrainingStartTime": now - 500}
            training["eu-west-1"][0] = sm_job("classifier-train", "Stopped", "Stopped", 300, now)
            training["us-east-1"].append(sm_job("quick-job", "Completed", "Completed", 30, now))
            q = batch[("us-east-1", "render-q")]
            q[0]["status"] = "FAILED"; q[0]["statusReason"] = "Essential container exited"; q[0]["stoppedAt"] = int(now * 1000)
            q[1]["status"] = "SUCCEEDED"; q[1]["stoppedAt"] = int(now * 1000)
        t = when(a, lambda t: t["state"] == "failed")
        check("failed training: failed, with the reason on one line", t and t.get("message") == "AlgorithmError: out of memory", t and t.get("message"))
        t = when(b, lambda t: t["state"] == "cancelled")
        check("stopped training: cancelled", t is not None, task(b))
        qj = wait(lambda: jid("quick-job", "us-east-1"))
        t = when(qj, lambda t: t["state"] == "done") if qj else None
        check("a job born and finished between two reads still shows, with its toast", t and t.get("notify", {}).get("finish") is True, t)
        t = when("aws-b-11111111-aaaa", lambda t: t["state"] == "failed")
        check("failed Batch job: its reason", t and t.get("message") == "Essential container exited", t and t.get("message"))
        t = when("aws-b-22222222-bbbb", lambda t: t["state"] == "done")
        check("succeeded Batch job: done", t is not None, task("aws-b-22222222-bbbb"))
        with lock:
            batch[("us-east-1", "render-q")].append(b_job("44444444-dddd", "stopped by hand", "RUNNING"))
        when("aws-b-44444444-dddd", lambda t: t["state"] == "running")
        with lock:
            j = batch[("us-east-1", "render-q")][-1]
            j["status"] = "FAILED"; j["isCancelled"] = True; j["stoppedAt"] = int(time.time() * 1000)
        check("cancelled Batch job: cancelled, not failed", when("aws-b-44444444-dddd", lambda t: t["state"] == "cancelled") is not None, task("aws-b-44444444-dddd"))

        # a service that refuses says so, and declares nothing finished
        with lock:
            batch[("us-east-1", "render-q")].append(b_job("55555555-eeee", "still running", "RUNNING"))
            job_faults["batch"] = (400, "ClientException", "User is not authorized to perform: batch:ListJobs")
        got = wait(lambda: state()["job_polls"].get("us-east-1/batch", {}).get("state") == "no_access")
        with lock:
            job_faults.clear()
        when("aws-b-55555555-eeee", lambda t: t["state"] == "running")
        with lock:
            job_faults["batch"] = (403, "AccessDeniedException")
        got2 = wait(lambda: all(v["state"] == "no_access" for k, v in state()["job_polls"].items() if k.endswith("/batch")))
        time.sleep(0.5)
        t = task("aws-b-55555555-eeee")
        rw = row()
        check("Batch's ClientException 'not authorized' = no access", got, state()["job_polls"])
        check("no access: job not declared finished, EC2 and key unaffected", got2 and t["state"] == "running" and state()["key_check"] == "ok", (t["state"], state()["key_check"]))
        check("no access everywhere: the settings say what to do and link the list", any("AWS Batch" in n and "no access" in n for n in rw["notes"]) and rw.get("help_needed") is True, rw["notes"])
        dump = json.dumps(state()) + json.dumps(rw)
        check("the service's error text isn't repeated", "not authorized" not in dump and "secret detail" not in dump, dump[:200])
        with lock:
            job_faults.clear()
            job_faults["sagemaker"] = (400, "ThrottlingException")
        check("throttling: that poll's state", wait(lambda: state()["job_polls"].get("us-east-1/sagemaker", {}).get("state") == "throttled"), state()["job_polls"])
        with lock:
            job_faults.clear()

        # switch off: one summary, nothing asked
        wait(lambda: state()["job_polls"].get("us-east-1/batch", {}).get("state") == "ok")
        before = len(toasts())
        s, r = call("POST", "/api/connectors/aws", {"batch": False})
        t = task("aws-b-55555555-eeee")
        new = toasts()[before:]
        check("Batch switched off: its jobs let go with one summary", s == 200 and t["state"] == "done" and "No longer watched" in (t.get("message") or "") and len(new) == 1 and "job" in new[0], (t["state"], new))
        check("...machines keep being followed", task("aws-i-0a1")["state"] == "running", task("aws-i-0a1")["state"])
        mock.requests.clear()
        time.sleep(0.8)
        check("...and Batch isn't asked any more", not [q for q in mock.requests if q["service"] == "batch"], [q["path"] for q in mock.requests if q["service"] == "batch"][:2])
        call("POST", "/api/connectors/aws", {"sagemaker": False, "job_interval": 300})

        # ------------------------------------------------------------ a restart of the loop, another key
        s, r = call("POST", "/api/connectors/aws", {"key": f"{KID}:{SECRET}"})
        when("aws-i-0a1", lambda t: t["state"] == "running")
        s, r = call("POST", "/api/connectors/aws", {"key": f"AKIABAD0BAD0BAD0BAD0:{SECRET}"})
        check("a rejected replacement keeps the old key and tasks", s == 400 and vault.read(TARGET) == f"{KID}:{SECRET}" and task("aws-i-0a1")["state"] == "running", (s, r))
        with lock:
            faults["us-east-1"] = (500, "InternalError")   # the new key's first read can't bring the task back before it is looked at
            faults["eu-west-1"] = (500, "InternalError")
        before = len(toasts())
        s, r = call("POST", "/api/connectors/aws", {"key": f"{KID2}:{SECRET2}"})
        t = task("aws-i-0a1")
        new = toasts()[before:]
        with lock:
            faults.clear()
        check("another key: one summary", len(new) == 1 and "machine" in new[0], new)
        check("another key (maybe another account): what was followed is let go first", s == 200 and t["state"] == "done" and "No longer watched" in (t.get("message") or ""), t)
        check("...then the same instances are followed under the new key", when("aws-i-0a1", lambda t: t["state"] == "running") is not None, task("aws-i-0a1"))

        # ------------------------------------------------------------ long jobs: long histories, partial answers, finishing states
        with lock:
            for x in (training, describe, queues, batch, job_faults, describe_faults):
                x.clear()
            sm_page["n"], batch_page["n"] = 1, 1
        s, r = call("POST", "/api/connectors/aws", {"job_interval": "abc"})
        check("job_interval must be a number", s == 400, (s, r))
        call("POST", "/api/connectors/aws", {"sagemaker": True, "batch": True, "job_interval": 0.2})
        with lock:
            training["us-east-1"] = [sm_job(n, age=a) for n, a in (("deep", 90000), ("limit-run", 80000), ("del-failed", 70000), ("denied-run", 60000), ("gone-run", 50000))]
            queues["us-east-1"] = ["sm-q", "render-q"]
            sm_queues.add("sm-q")
            batch[("us-east-1", "render-q")] = [b_job("66666666-aaaa", "deep batch", "RUNNING", 90000), b_job("77777777-bbbb", "deep batch 2", "RUNNING", 90000)]
        sid = lambda name: next((j["task"] for j in state()["jobs"] if j["id"] == name), None)
        wait(lambda: all(sid(n) for n in ("deep", "limit-run", "del-failed", "denied-run", "gone-run")))
        check("a queue that refuses ListJobs (SageMaker type) doesn't lose the others", when("aws-b-66666666-aaaa", lambda t: t["state"] == "running") is not None, state()["job_polls"])
        ids = {n: sid(n) for n in ("deep", "limit-run", "del-failed", "denied-run", "gone-run")}
        with lock:
            now = time.time()
            # a long history comes first in the lists: more pages than may be read
            training["us-east-1"] += [sm_job(f"hist-{i}", "Completed", "Completed", 40000 - i, now - 30000) | {"LastModifiedTime": now - 30000} for i in range(30)]
            batch[("us-east-1", "render-q")] += [b_job(f"h{i:07d}-cccc", "hist", "SUCCEEDED", 40000 - i, now - 30000) for i in range(30)]
            d = {i["TrainingJobName"]: i for i in training["us-east-1"]}
            d["deep"].update(sm_job("deep", "Completed", "Completed", 90000, now))
            d["limit-run"].update(sm_job("limit-run", "Stopped", "MaxRuntimeExceeded", 80000, now))
            d["del-failed"].update(sm_job("del-failed", "Deleting", "Failed", 70000, now))
            describe[("us-east-1", "del-failed")] = d["del-failed"] | {"FailureReason": "Role arn:aws:iam::123456789012:role/x refused for account 123456789012"}
            # no longer in any list: only the job's own answer can tell
            training["us-east-1"] = [i for i in training["us-east-1"] if i["TrainingJobName"] not in ("denied-run", "gone-run")]
            describe_faults["denied-run"] = (403, "AccessDeniedException")
            j = batch[("us-east-1", "render-q")][0]
            j["status"] = "SUCCEEDED"; j["stoppedAt"] = int(now * 1000)
            del batch[("us-east-1", "render-q")][1]
        check("over the page limit: a followed job still finishes (SageMaker)", when(ids["deep"], lambda t: t["state"] == "done") is not None, task(ids["deep"]) and task(ids["deep"])["state"])
        check("over the page limit: a followed job still finishes (Batch)", when("aws-b-66666666-aaaa", lambda t: t["state"] == "done") is not None, (task("aws-b-66666666-aaaa") or {}).get("state"))
        t = when("aws-b-77777777-bbbb", lambda t: t["state"] != "running")
        check("Batch: a job its own answer no longer knows is let go", t and "No longer watched" in (t.get("message") or ""), t and t["state"])
        t = when(ids["limit-run"], lambda t: t["state"] != "running")
        check("stopped by its time limit: failed, and says so", t and t["state"] == "failed" and "time limit" in (t.get("message") or "").lower(), t and (t["state"], t.get("message")))
        t = when(ids["del-failed"], lambda t: t["state"] != "running")
        check("being deleted after a failure: failed", t and t["state"] == "failed", t and t["state"])
        check("an account id in a failure reason goes no further", t and "123456789012" not in json.dumps(t) and "refused" in (t.get("message") or ""), t and t.get("message"))
        t = when(ids["gone-run"], lambda t: t["state"] != "running")
        check("SageMaker: a job that no longer exists is let go", t and "No longer watched" in (t.get("message") or ""), t and t["state"])
        time.sleep(1)
        t = task(ids["denied-run"])
        check("the job's own answer refused: nothing declared finished", t["state"] == "running", t["state"])
        with lock:
            describe_faults.clear()
            training["us-east-1"].append(sm_job("denied-run", "InProgress", "Training", 60000))
            job_faults["sagemaker"] = (400, "ThrottlingException:http://internal.example/#frag")
        check("an error type with a URI part: still throttling", wait(lambda: state()["job_polls"].get("us-east-1/sagemaker", {}).get("state") == "throttled"), state()["job_polls"].get("us-east-1/sagemaker"))
        with lock:
            job_faults.clear()
            job_faults["batch"] = (400, "header:ThrottlingException")
        check("the error type in X-Amzn-Errortype is read", wait(lambda: state()["job_polls"].get("us-east-1/batch", {}).get("state") == "throttled"), state()["job_polls"].get("us-east-1/batch"))
        with lock:
            job_faults.clear()
        # a switch changed while a new key is being checked isn't lost
        with lock:
            regions_delay["s"] = 2
        keyer = threading.Thread(target=lambda: call("POST", "/api/connectors/aws", {"key": f"{KID2}:{SECRET2}"}))
        keyer.start()
        time.sleep(0.5)
        call("POST", "/api/connectors/aws", {"batch": False})
        keyer.join()
        with lock:
            regions_delay["s"] = 0
        check("a switch changed during a key check stays changed", state()["batch"] is False, state()["batch"])
        # connector off, jobs finish, on again: no pile of toasts
        wait(lambda: state()["job_polls"].get("us-east-1/sagemaker", {}).get("state") == "ok")
        offon = None
        with lock:
            training["us-east-1"] = [sm_job("off-on")]
        wait(lambda: sid("off-on"))
        offon = sid("off-on")
        call("POST", "/api/connectors/aws", {"enabled": False})
        with lock:
            now = time.time()
            training["us-east-1"] = [sm_job("off-on", "Completed", "Completed", 600, now), sm_job("born-off", "Completed", "Completed", 5, now)]
        time.sleep(0.3)
        before = len(toasts())
        call("POST", "/api/connectors/aws", {"enabled": True})
        time.sleep(2)
        new = toasts()[before:]
        check("finished while the connector was off: no toasts on turning it on", not any("off-on" in l or "born-off" in l for l in new), new)
        check("...and not shown", not any(x.get("name") == "born-off" for x in call("GET", "/api/tasks")[1].get("tasks", [])), state()["jobs"])
        call("POST", "/api/connectors/aws", {"sagemaker": False, "batch": False, "job_interval": 300})
        with lock:
            for x in (training, describe, queues, batch, job_faults, describe_faults):
                x.clear()
            sm_queues.clear()
            sm_page["n"], batch_page["n"] = 1, 100

        # ------------------------------------------------------------ money: Cost Explorer (paid per request), Budgets
        cost_calls = lambda: [q for q in mock.requests if q["service"] == "ce"]
        cost_state = lambda: state()["cost_read"]
        s, r = call("POST", "/api/connectors/aws", {"cost_interval": "abc"})
        check("cost_interval must be a number", s == 400, (s, r))
        rw = row()
        check("settings row: the cost switch says the price beside it, off", any(x["key"] == "cost" and x["value"] is False and "$0.01" in x["text"] for x in rw["switches"]) and any("$1.80" in (x.get("hint") or "") for x in rw["switches"]), rw["switches"])
        mock.requests.clear()
        call("POST", "/api/connectors/aws", {"cost": True})
        wait(lambda: cost_state().get("read_at"))
        time.sleep(1.2)
        calls = cost_calls()
        today = time.strftime("%Y-%m-%d", time.gmtime())
        first = today[:8] + "01"
        check("two paid requests, then none: at most one read per 8 hours", len(calls) == (2 if today != first else 1), [q["headers"].get("x-amz-target") for q in calls])
        check("signed for ce in us-east-1, target signed", calls and all(q.get("verified") and q["region"] == "us-east-1" and "x-amz-target" in q.get("signed", []) for q in calls), calls[:1])
        fc = json.loads(calls[-1]["body"])
        check("forecast from today to the next month, the right metric name", fc["TimePeriod"]["Start"] == today and fc["TimePeriod"]["End"] > today and fc["TimePeriod"]["End"].endswith("-01") and fc["Metric"] == "UNBLENDED_COST", fc)
        if today != first:
            mt = json.loads(calls[0]["body"])
            check("month so far: 1st to today (End is exclusive), the right metric name", mt["TimePeriod"] == {"Start": first, "End": today} and mt["Metrics"] == ["UnblendedCost"], mt)
            check("settings: the amount, and a forecast including what's spent", any("$12.35" in n and "$42.45" in n for n in row()["notes"]), row()["notes"])
        n1 = row()["notes"]
        time.sleep(2)
        check("the line says when it was read and doesn't tick (a redraw loses the focus)", n1 == row()["notes"] and any("read at" in n for n in n1), n1)
        before = len(cost_calls())
        call("POST", "/api/connectors/aws", {"cost": False})
        call("POST", "/api/connectors/aws", {"cost": True})
        time.sleep(0.8)
        check("off and on again pays nothing more", len(cost_calls()) == before, len(cost_calls()))
        # the promised price holds when something fails
        with lock:
            job_faults["forecast"] = (500, "InternalServerError")
        s, _ = call("POST", "/api/connectors/aws", {"cost_interval": 28800})
        before = len(cost_calls())
        wait(lambda: cost_state().get("state") == "error")
        time.sleep(1.5)
        cs = cost_state()
        check("forecast fails: no retry before the 8 hours (the month's read is paid)", len(cost_calls()) - before <= 2 and cs.get("next_at", 0) - time.time() > 7 * 3600, (len(cost_calls()) - before, cs))
        if today != first:
            check("...and the month's figure is kept", cs.get("month") == 12.3456, cs)
        with lock:
            job_faults.clear()
            job_faults["ce"] = "drop"
        s, _ = call("POST", "/api/connectors/aws", {"cost_interval": 28800})
        check("the network breaks, nothing paid: a sooner retry", wait(lambda: 0 < cost_state().get("next_at", 0) - time.time() < 3700), cost_state())
        with lock:
            job_faults.clear()
        call("POST", "/api/connectors/aws", {"cost": False})
        check("switched off: no amounts in the settings", not any("$12.35" in n for n in row()["notes"]), row()["notes"])
        # what isn't known is never $0
        with lock:
            job_faults["ce"] = (400, "DataUnavailableException")
        s, _ = call("POST", "/api/connectors/aws", {"cost": True, "cost_interval": 0.3})
        check("mock: interval changed", s == 200)
        check("no data yet: says so", wait(lambda: cost_state().get("state") == "not_ready"), cost_state())
        check("...with what to do, never $0", any("no data yet" in n for n in row()["notes"]) and not any("$0.00" in n for n in row()["notes"]), row()["notes"])
        with lock:
            job_faults["ce"] = (403, "AccessDeniedException")
        check("refused: no_access", wait(lambda: cost_state().get("state") == "no_access"), cost_state())
        rw = row()
        check("...says what to do, links the how-to", any("no access" in n for n in rw["notes"]) and rw.get("help_needed") is True, rw["notes"])
        # the permission added, off and on again: asked at once, the warning doesn't outlive the fix
        call("POST", "/api/connectors/aws", {"cost_interval": 28800})
        wait(lambda: cost_state().get("state") == "no_access" and cost_state().get("next_at", 0) - time.time() > 7 * 3600)
        time.sleep(0.5)
        with lock:
            job_faults.clear()
        call("POST", "/api/connectors/aws", {"cost": False})
        before = len(cost_calls())
        call("POST", "/api/connectors/aws", {"cost": True})
        check("permission fixed: turning it off and on asks again", wait(lambda: cost_state().get("state") == "ok" and len(cost_calls()) > before), (cost_state(), "requests that arrived:", len(cost_calls()) - before, state()["status"]))
        call("POST", "/api/connectors/aws", {"cost_interval": 0.3})
        with lock:
            job_faults.clear()
            job_faults["forecast"] = (400, "DataUnavailableException")
            money["month"] = "7"
        got = wait(lambda: cost_state().get("state") == "ok" and cost_state().get("month") == 7 and cost_state().get("forecast") is None) if today != first else True
        check("too little history for a forecast: the month without one", got, cost_state())
        with lock:
            job_faults.clear()
            money["month"] = "not a number"
        if today != first:
            check("an unreadable amount: an error, not $0", wait(lambda: cost_state().get("state") == "error"), cost_state())
        with lock:
            money["month"] = "12.3456"
        # a setting changed while a read is on its way restarts the loop: the read, maybe paid for, isn't lost
        with lock:
            ce_delay["s"] = 1
        call("POST", "/api/connectors/aws", {"cost_interval": 28800})
        wait(lambda: cost_state().get("next_at", 0) - time.time() > 7 * 3600)
        call("POST", "/api/connectors/aws", {"forgotten_hours": 72})
        with lock:
            ce_delay["s"] = 0
        got = wait(lambda: cost_state().get("state") == "ok", 5)
        check("a setting changed during a paid read doesn't lose it", got and cost_state().get("month") == 12.3456, cost_state())
        call("POST", "/api/connectors/aws", {"cost": False})
        # budgets
        bud = lambda name, actual, limit: {"BudgetName": name, "BudgetType": "COST", "TimeUnit": "MONTHLY", "BudgetLimit": {"Amount": limit, "Unit": "USD"}, "CalculatedSpend": {"ActualSpend": {"Amount": actual, "Unit": "USD"}}}
        with lock:
            money["budgets"] = [bud("build farm", "60", "50"), bud("render", "10", "100")]
        mock.requests.clear()
        before = len(toasts())
        call("POST", "/api/connectors/aws", {"budgets": True, "budget_interval": 0.3})
        wait(lambda: state()["budgets_read"]["state"] == "ok")
        time.sleep(1.5)
        st = state()
        check("budgets read", [b["name"] for b in st["budgets_read"]["items"]] == ["build farm", "render"] and st["budgets_read"]["items"][0]["actual"] == 60, st["budgets_read"])
        calls = [q for q in mock.requests if q["service"] == "budgets"]
        check("budgets signed for us-east-1, with the account id from STS", calls and calls[0].get("verified") and calls[0]["region"] == "us-east-1" and ACCOUNT in calls[0]["body"], calls[:1])
        check("the account is asked once", len([q for q in mock.requests if q["service"] == "sts"]) == 1, len([q for q in mock.requests if q["service"] == "sts"]))
        rw = row()
        check("settings: over the limit stands out", any("⚠" in n and "build farm" in n and "$60.00" in n and "$50.00" in n for n in rw["notes"]) and any("render" in n and "⚠" not in n for n in rw["notes"]), rw["notes"])
        new = [l for l in toasts()[before:] if "budget" in l.lower()]
        check("over the limit: one notification, not repeated", len(new) == 1 and "build farm" in new[0], new)
        with lock:
            money["budgets"].append(bud("backup", "9", "5"))
            money["budgets"].append(bud("video", "9", "5"))
        wait(lambda: len(state()["budgets_read"]["items"]) == 4)
        time.sleep(0.8)
        new = [l for l in toasts()[before:] if "budget" in l.lower()]
        check("two more at once: one summary with the count", len(new) == 2 and "2 budgets" in new[1] and "build farm" not in new[1], new)
        dump = json.dumps(state()) + json.dumps(row()) + open(os.path.join(DATA, LOG), encoding="utf-8", errors="replace").read()
        check("the account id stays out of the state, the settings and the log", ACCOUNT not in dump)
        # one a month, also when others come and go
        with lock:
            money["budgets"] = [bud("alpha", "9", "5"), bud("beta", "1", "5")]
        wait(lambda: [b["name"] for b in state()["budgets_read"]["items"]] == ["alpha", "beta"])
        time.sleep(0.6)
        with lock:
            money["budgets"] = [bud("alpha", "1", "5"), bud("beta", "9", "5")]
        wait(lambda: state()["budgets_read"]["items"][1]["actual"] == 9)
        time.sleep(0.6)
        with lock:
            money["budgets"] = [bud("alpha", "9", "5"), bud("beta", "9", "5")]
        wait(lambda: state()["budgets_read"]["items"][0]["actual"] == 9)
        time.sleep(0.8)
        new = [l for l in toasts() if "over" in l and "alpha" in l]
        check("over, under, over again in one month: one notification", len(new) == 1, new)
        # what isn't a plain limit isn't read as one
        ri = bud("ri cover", "85", "100") | {"BudgetType": "RI_UTILIZATION"}
        with lock:
            money["budgets"] = [ri]
        wait(lambda: state()["budgets_read"]["items"] == [])
        rw = row()
        check("a utilisation budget isn't called over or under, nor 'no budgets'", not any("ri cover" in n or "has no budgets" in n for n in rw["notes"]), rw["notes"])
        with lock:
            money["budgets"] = [bud(f"b{i}", "1", "9") for i in range(7)]
            money["budgets"][0]["BudgetLimit"]["Unit"] = "GB-Mo"
            money["budgets"][0]["BudgetType"] = "USAGE"
            money["budgets"][0]["CalculatedSpend"]["ActualSpend"]["Unit"] = "GB-Mo"
            money["budgets"][0]["CalculatedSpend"]["ActualSpend"]["Amount"] = "8"
        wait(lambda: len(state()["budgets_read"]["items"]) == 7)
        rw = row()
        check("usage budgets keep their unit; a long list says how many more", any("8.00 GB-Mo of 9.00 GB-Mo" in n for n in rw["notes"]) and any("and 2 more" in n for n in rw["notes"]), rw["notes"])
        with lock:
            job_faults["budgets"] = (400, "AccessDeniedException")
        check("Budgets refused: says so, links the how-to", wait(lambda: state()["budgets_read"]["state"] == "no_access") and row().get("help_needed") is True and any("AWS Budgets" in n and "no access" in n for n in row()["notes"]), row()["notes"])
        call("POST", "/api/connectors/aws", {"budget_interval": 28800})
        time.sleep(0.8)   # the read due now sets the long wait, and still fails
        with lock:
            job_faults.clear()
        time.sleep(0.8)
        check("...stays refused without being asked", state()["budgets_read"]["state"] == "no_access", state()["budgets_read"])
        call("POST", "/api/connectors/aws", {"budgets": False})
        call("POST", "/api/connectors/aws", {"budgets": True})
        check("permission fixed: Budgets asked again at once", wait(lambda: state()["budgets_read"]["state"] == "ok"), state()["budgets_read"])
        call("POST", "/api/connectors/aws", {"budget_interval": 0.3})
        # another key: another account's figures go
        call("POST", "/api/connectors/aws", {"cost": True})
        wait(lambda: cost_state().get("state") == "ok")
        call("POST", "/api/connectors/aws", {"cost_interval": 28800})
        wait(lambda: cost_state().get("next_at", 0) - time.time() > 7 * 3600)
        time.sleep(1)   # the read just begun has finished: only what the swaps cause is counted
        sts_before =len([q for q in mock.requests if q["service"] == "sts"])
        cost_before = len(cost_calls())
        other = lambda: f"{KID}:{SECRET}" if vault.read(TARGET) == f"{KID2}:{SECRET2}" else f"{KID2}:{SECRET2}"
        call("POST", "/api/connectors/aws", {"key": other()})
        check("another key: its own account number is asked", wait(lambda: len([q for q in mock.requests if q["service"] == "sts"]) > sts_before), sts_before)
        cs = cost_state()
        check("another key: the old account's figures are gone", cs.get("month") is None and cs.get("forecast") is None, cs)
        check("...the settings say when the next read is", any("next read" in n for n in row()["notes"]), row()["notes"])
        call("POST", "/api/connectors/aws", {"key": other()})
        call("POST", "/api/connectors/aws", {"key": other()})
        time.sleep(1)
        check("swapping keys pays nothing: the 8 hours are kept", len(cost_calls()) == cost_before, (cost_before, len(cost_calls())))
        # a read still on its way when the key changes doesn't land on the new key
        with lock:
            ce_delay["s"] = 1
        t0 = time.time()
        s, _ = call("POST", "/api/connectors/aws", {"cost_interval": 28800})
        wait(lambda: cost_state().get("next_at", 0) > t0 + 7 * 3600)   # it has begun
        call("POST", "/api/connectors/aws", {"key": other()})
        with lock:
            ce_delay["s"] = 0
        time.sleep(2.5)
        cs = cost_state()
        check("the old key's read arriving late isn't kept", cs.get("month") is None and cs.get("forecast") is None, cs)
        # STS answers errors in XML
        with lock:
            job_faults["sts"] = (403, "InvalidClientTokenId")
        call("POST", "/api/connectors/aws", {"key": other()})
        check("an STS error is read: no access, not a general error", wait(lambda: state()["budgets_read"]["state"] == "no_access"), state()["budgets_read"])
        with lock:
            job_faults.clear()
        call("POST", "/api/connectors/aws", {"cost": False, "budgets": False})
        with lock:
            money["budgets"] = []

        # ------------------------------------------------------------ long jobs across a real restart (a hub of its own)
        data2 = tempfile.mkdtemp(prefix="taskhub-aws2-")
        url2 = "http://127.0.0.1:47893"
        exe = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "app", "TaskHubHost.exe")
        with open(os.path.join(data2, "config.json"), "w", encoding="utf-8") as f:
            json.dump({"log_level": "debug", "toasts": False, "autostart": False, "language": "en", "toast_rate": 0, "toast_task_gap": 0, "connectors": {"aws": {"api": f"http://127.0.0.1:{PORT}"}}}, f)

        def call2(method, path, body=None):
            rq = urllib.request.Request(url2 + path, method=method, data=json.dumps(body).encode() if body is not None else None, headers={"X-TaskHub": "1", "Content-Type": "application/json"})
            with op.open(rq, timeout=10) as r:
                return json.load(r)

        def task2(tid):
            try:
                return call2("GET", f"/api/tasks/{tid}")
            except urllib.error.HTTPError:
                return None

        def start2():
            h = subprocess.Popen([exe, "--port", "47893", "--data", data2, "--no-tray"])
            def up():
                try:
                    return call2("GET", "/api/health") is not None
                except OSError:
                    return False
            wait(up, 30)
            return h

        def tid2(name):
            for j in call2("GET", "/api/connectors/aws").get("jobs", []):
                if j["id"] == name:
                    return j["task"]

        def toasts2():
            time.sleep(0.6)
            try:
                return [l for l in open(os.path.join(data2, LOG), encoding="utf-8", errors="replace") if "TOAST [" in l]
            except OSError:
                return []

        hub2 = None
        try:
            with lock:
                job_faults.clear()
                training["us-east-1"] = []
                queues["us-east-1"] = []
            hub2 = start2()
            with lock:
                job_faults[("eu-west-1", "sagemaker")] = (403, "AccessDeniedException")   # one Region refusing mustn't hold "since" back
            call2("POST", "/api/connectors/aws", {"key": f"{KID}:{SECRET}", "enabled": True, "regions": ["us-east-1", "eu-west-1"], "sagemaker": True, "job_interval": 0.2})
            with lock:
                training["us-east-1"] = [sm_job("runs-on"), sm_job("cleared", "Completed", "Completed", 60, time.time())]
            wait(lambda: tid2("runs-on") and tid2("cleared"))
            run_t, cleared_t = tid2("runs-on"), tid2("cleared")
            wait(lambda: (task2(cleared_t) or {}).get("state") == "done")
            call2("DELETE", f"/api/tasks/{cleared_t}?force=1")
            time.sleep(4)   # "since" moves past the cleared job
            check("a finished job past \"since\" is forgotten", tid2("cleared") is None, call2("GET", "/api/connectors/aws").get("jobs"))
            hub2.kill(); hub2.wait(10)
            before = len(toasts2())
            with lock:
                now = time.time()
                training["us-east-1"][0] = sm_job("runs-on", "Completed", "Completed", 600, now)
                training["us-east-1"].append(sm_job("born-while-off", "Completed", "Completed", 5, now))
                # "cleared" keeps its old LastModifiedTime: a real read wouldn't list it any more
            hub2 = start2()
            wait(lambda: (task2(run_t) or {}).get("state") == "done")
            t = task2(run_t)
            check("restart: a followed job that ended meanwhile finishes, once, with its toast", t and t["state"] == "done" and sum("runs-on" in l for l in toasts2()[before:]) == 1, (t and t["state"], toasts2()[before:]))
            named = lambda n: next((x["id"] for x in call2("GET", "/api/tasks").get("tasks", []) if x.get("name") == n), None)
            wait(lambda: named("born-while-off"))
            b2 = named("born-while-off")
            check("restart: a job that started and ended while off shows, without a toast", b2 and (task2(b2) or {}).get("state") == "done" and not any("born-while-off" in l for l in toasts2()[before:]), toasts2()[before:])
            check("restart: a finished job the user cleared doesn't come back", task2(cleared_t) is None, task2(cleared_t))
            # a paid read survives a restart: no second payment
            call2("POST", "/api/connectors/aws", {"cost": True})
            wait(lambda: call2("GET", "/api/connectors/aws")["cost_read"].get("read_at"))
            time.sleep(0.5)
            paid = len([q for q in mock.requests if q["service"] == "ce"])
            hub2.kill(); hub2.wait(10)
            hub2 = start2()
            time.sleep(2.5)
            now_paid = len([q for q in mock.requests if q["service"] == "ce"])
            check("restart: Cost Explorer isn't asked again, and its figures are still there", paid > 0 and now_paid == paid and call2("GET", "/api/connectors/aws")["cost_read"].get("read_at"), (paid, now_paid))
            # the limit can't be saved: no paid read
            hub2.kill(); hub2.wait(10)
            with open(os.path.join(data2, "config.json"), encoding="utf-8") as f:
                cfg2 = json.load(f)
            cfg2["connectors"]["aws"]["cost_cache"]["next"] = 0
            with open(os.path.join(data2, "config.json"), "w", encoding="utf-8") as f:
                json.dump(cfg2, f)
            blocked = os.path.join(data2, "config.json.tmp")
            os.mkdir(blocked)
            try:
                hub2 = start2()
                time.sleep(2.5)
                check("the limit can't be saved: nothing is paid", len([q for q in mock.requests if q["service"] == "ce"]) == now_paid, (now_paid, len([q for q in mock.requests if q["service"] == "ce"])))
            finally:
                hub2.kill(); hub2.wait(10)
                os.rmdir(blocked)
            hub2 = start2()
            call2("POST", "/api/connectors/aws", {"forget_key": True, "enabled": False})
        finally:
            if hub2:
                hub2.kill(); hub2.wait(10)
            with lock:
                training.clear(); describe.clear(); queues.clear(); batch.clear(); job_faults.clear()
            vault.delete(vault.target(data2, "aws"))
            shutil.rmtree(data2, ignore_errors=True)

        # ------------------------------------------------------------ off, forget
        time.sleep(0.6)
        before = len(toasts())
        s, r = call("POST", "/api/connectors/aws", {"enabled": False})
        new = toasts()[before:]
        check("turning it off: one summary", len(new) == 1 and "machine" in new[0], new)
        t = task("aws-i-0a1")
        check("off keeps the key, lets the tasks go", s == 200 and r["key_set"] is True and vault.exists(TARGET) and t["state"] == "done" and "No longer watched" in (t.get("message") or ""), (r, t and t.get("message")))
        time.sleep(0.4)
        mock.requests.clear()
        time.sleep(1.2)
        check("nothing polled when off", not mock.requests, mock.requests[:1])
        s, r = call("POST", "/api/connectors/aws", {"forget_key": True})
        check("forget deletes the key", s == 200 and r["key_set"] is False and not vault.exists(TARGET), r)
        before = len(toasts())
        call("POST", "/api/connectors/aws", {"enabled": False})
        check("nothing let go: no notification", len(toasts()) == before, toasts()[before:])
        clean()
        call("POST", "/api/connectors/aws", {"list_interval": 600, "forgotten_hours": 72})

print("AWS TESTS " + ("PASSED" if not fails else f"FAILED ({fails})"))
raise SystemExit(1 if fails else 0)
