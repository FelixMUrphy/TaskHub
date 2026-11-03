"""SigV4 signer of the AWS connector: AWS's own test suite (sigv4_vectors.json), the mock's independent signer against the same
suite (so the mock can be trusted), then real requests through SafeHttp checked by the mock on the bytes that arrived.
Usage: aws_sigv4_test.py <TaskHubHost.exe>"""
import hashlib, json, os, subprocess, sys, tempfile
from aws_mock import Mock, signature

EXE = sys.argv[1]
HERE = os.path.dirname(os.path.abspath(__file__))
PORT = 47889
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


def parse_request(text):
    """request.txt of the suite: 'METHOD path?query HTTP/1.1', headers (a line starting with a space continues the previous value)."""
    head, _, body = text.partition("\n\n")
    lines = head.split("\n")
    method, target = lines[0].split(" ", 1)
    target = target.rsplit(" ", 1)[0]
    path, _, query = target.partition("?")
    headers = []
    for ln in lines[1:]:
        if not ln.strip():
            continue
        if ln.startswith((" ", "\t")):
            headers[-1][1] += " " + ln.strip()
        else:
            k, _, v = ln.partition(":")
            headers.append([k, v])
    return method, path, query, headers, body


suite = json.load(open(os.path.join(HERE, "sigv4_vectors.json"), encoding="utf-8"))
vectors = []
for c in suite["cases"]:
    ctx = c["context"]
    method, path, query, headers, body = parse_request(c["request"])
    if ctx.get("sign_body"):  # the suite's option: the body's hash also goes in a signed header
        headers.append(["x-amz-content-sha256", hashlib.sha256(body.encode()).hexdigest()])
    cr = ctx["credentials"]
    vectors.append({"name": c["name"], "method": method, "path": path, "query": query, "headers": headers, "body": body,
                    "access_key": cr["access_key_id"], "secret": cr["secret_access_key"],
                    "token": None if ctx.get("omit_session_token") else cr.get("token"),
                    "region": ctx["region"], "service": ctx["service"], "timestamp": ctx["timestamp"], "normalize": ctx.get("normalize", True)})

K, S = "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY"
base = {"method": "POST", "access_key": K, "secret": S, "region": "us-east-1"}
u = f"http://127.0.0.1:{PORT}"
JSON11 = "application/x-amz-json-1.1"
send = [
    {**base, "method": "GET", "url": u + "/?Action=DescribeInstances&MaxResults=10&Version=2016-11-15", "service": "ec2"},
    {**base, "method": "GET", "url": u + "/?Action=DescribeInstances&NextToken=a%2Fb%2Bc%3D%3D%20d&Version=2016-11-15", "service": "ec2"},
    {**base, "url": u + "/", "service": "ce", "target": "AWSInsightsIndexService.GetCostAndUsage", "content_type": JSON11,
     "body": '{"Granularity":"MONTHLY","Metrics":["UnblendedCost"],"Note":"кирилиця 日本"}'},
    {**base, "url": u + "/v1/listjobs", "service": "batch", "content_type": "application/json", "body": '{"jobQueue":"q","jobStatus":"FAILED"}'},
    {**base, "url": u + "/", "service": "sagemaker", "target": "SageMaker.ListTrainingJobs", "content_type": JSON11, "body": "{}", "token": "SESSION/token+with=chars"},
    {**base, "url": u + "/", "service": "ce", "secret": "wrong-secret", "content_type": JSON11, "body": "{}"},
    {**base, "url": u + "/", "service": "ce", "access_key": "AKIAUNKNOWN", "content_type": JSON11, "body": "{}"},
]

tmp = tempfile.mkdtemp(prefix="taskhub-sigv4-")
inp, out = os.path.join(tmp, "in.json"), os.path.join(tmp, "out.json")
json.dump({"vectors": vectors, "send": send}, open(inp, "w", encoding="utf-8"))
with Mock(PORT, {K: S}) as mock:
    rc = subprocess.run([EXE, "--data", tmp, "--selftest-sigv4", inp, out], env=dict(os.environ, TASKHUB_TEST_HOME=tmp), timeout=120).returncode
res = json.load(open(out, encoding="utf-8"))
check("selftest ran", rc == 0, res.get("error"))
got = {r["name"]: r for r in res.get("vectors", [])}
check(f"all {len(suite['cases'])} AWS vectors answered", len(got) == len(suite["cases"]) and len(got) >= 38, len(got))

for c, v in zip(suite["cases"], vectors):
    g = got.get(c["name"], {})
    check(f"vector {c['name']}: canonical request", g.get("canonical") == c["canonical"], (g.get("canonical"), c["canonical"]))
    check(f"vector {c['name']}: string to sign", g.get("string_to_sign") == c["string_to_sign"], g.get("string_to_sign"))
    check(f"vector {c['name']}: signature", g.get("signature") == c["signature"], g.get("signature"))

# the mock's own signer must agree with AWS's suite, or its verdicts below mean nothing (unnormalized paths: the mock never sees them)
for c, v in zip(suite["cases"], vectors):
    if not v["normalize"] or c["name"] in ("get-header-key-duplicate", "get-header-value-multiline", "get-header-value-order") or any(x in c["name"] for x in ("relative", "slash", "space")):
        continue
    h = {}
    for k, val in v["headers"]:
        h.setdefault(k.lower(), []).append(val)
    amz = v["timestamp"].replace("-", "").replace(":", "")
    h["x-amz-date"] = [amz]
    if v["token"]:
        h["x-amz-security-token"] = [v["token"]]
    _, sig = signature(v["method"], v["path"], v["query"], h, sorted(h), v["body"].encode(), v["secret"], amz, v["region"], v["service"])
    check(f"mock signer agrees with AWS: {c['name']}", sig == c["signature"], sig)

r = res.get("send", [])
names = ["EC2 query", "EC2 query with a token needing encoding", "JSON-1.1 with non-ASCII body", "REST-JSON", "session token"]
for i, n in enumerate(names):
    check(f"sent: {n} verified by the mock", i < len(r) and r[i]["status"] == 200 and mock.requests[i].get("verified") is True,
          (r[i] if i < len(r) else None, mock.requests[i] if i < len(mock.requests) else None))
check("sent: wrong secret is refused", len(r) > 5 and r[5]["status"] == 403 and "InvalidSignature" in r[5]["body"], r[5:6])
check("sent: unknown key is refused", len(r) > 6 and r[6]["status"] == 403 and "UnrecognizedClient" in r[6]["body"], r[6:7])
if len(mock.requests) >= 5:
    ce = mock.requests[2]
    check("the JSON content type goes unchanged", ce["headers"].get("content-type") == [JSON11], ce["headers"].get("content-type"))
    check("signed headers cover host, content-type and x-amz-*", {"host", "content-type", "x-amz-date", "x-amz-target"} <= set(ce.get("signed", [])), ce.get("signed"))
    check("user-agent is not signed", "user-agent" not in ce.get("signed", []), ce.get("signed"))
    tok = mock.requests[4]
    check("session token is sent and signed", tok["headers"].get("x-amz-security-token") == ["SESSION/token+with=chars"] and "x-amz-security-token" in tok.get("signed", []), tok["headers"])
    check("secret never leaves in a header", not any(S in str(v) for rq in mock.requests for v in rq["headers"].values()))

print("AWS SIGV4 TESTS " + ("PASSED" if not fails else f"FAILED ({fails})"))
sys.exit(1 if fails else 0)
