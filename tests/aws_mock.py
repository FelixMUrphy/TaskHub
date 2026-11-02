"""Mock of the AWS endpoints (loopback, stdlib). It checks every request's SigV4 signature with its own signer, written from the
AWS documentation and independent of the hub's, on the bytes that really arrived.
https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_sigv-create-signed-request.html"""
import email.utils, hashlib, hmac, json, re, threading, time, urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

UNRESERVED = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~"


def enc(s):
    return urllib.parse.quote(s, safe=UNRESERVED[-4:]) if s else ""


def signature(method, raw_path, raw_query, headers, signed_names, body, secret, amz_date, region, service):
    """headers: lower-case name -> list of raw values. Returns (canonical request, signature)."""
    pairs = []
    for p in filter(None, raw_query.split("&")):
        k, _, v = p.partition("=")
        pairs.append((enc(urllib.parse.unquote(k)), enc(urllib.parse.unquote(v))))
    query = "&".join(f"{k}={v}" for k, v in sorted(pairs))
    clean = lambda v: " ".join(v.split())
    hlines = "".join(f"{n}:{','.join(clean(v) for v in headers.get(n, []))}\n" for n in signed_names)
    path = "/".join(enc(x) for x in raw_path.split("/")) or "/"
    canonical = "\n".join([method, path, query, hlines, ";".join(signed_names), hashlib.sha256(body).hexdigest()])
    date = amz_date[:8]
    to_sign = "\n".join(["AWS4-HMAC-SHA256", amz_date, f"{date}/{region}/{service}/aws4_request",
                         hashlib.sha256(canonical.encode()).hexdigest()])
    key = ("AWS4" + secret).encode()
    for part in (date, region, service, "aws4_request"):
        key = hmac.new(key, part.encode(), hashlib.sha256).digest()
    return canonical, hmac.new(key, to_sign.encode(), hashlib.sha256).hexdigest()


AUTH_RX = re.compile(r"^AWS4-HMAC-SHA256 Credential=([^/]+)/(\d{8})/([^/]+)/([^/]+)/aws4_request, SignedHeaders=([^,]+), Signature=([0-9a-f]{64})$")


class Mock:
    """`keys`: access key -> secret. `requests`: what arrived and whether its signature held. `handler(rec)`, called for a
    verified request, answers it as the service would: (status, content type, body bytes); none: {"ok": true}.
    `date_offset`: seconds added to the answers' Date header (as seen from a machine whose clock is off)."""

    def __init__(self, port, keys, handler=None):
        self.keys, self.requests, self.handler, self.date_offset = keys, [], handler, 0
        mock = self

        class H(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.0"

            def log_message(self, *a):
                pass

            def date_time_string(self, timestamp=None):
                return email.utils.formatdate((timestamp or time.time()) + mock.date_offset, usegmt=True)

            def raw(self, code, ctype, b, headers=None):
                self.send_response(code)
                self.send_header("Content-Type", ctype)
                for k, v in (headers or {}).items():
                    self.send_header(k, v)
                self.send_header("Content-Length", str(len(b)))
                self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.write(b)

            def reply(self, code, obj):
                b = json.dumps(obj).encode()
                self.send_response(code)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(b)))
                self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.write(b)

            def handle_any(self):
                body = self.rfile.read(int(self.headers.get("Content-Length") or 0))
                u = urllib.parse.urlsplit(self.path)
                headers = {}
                for k, v in self.headers.items():
                    headers.setdefault(k.lower(), []).append(v)
                rec = {"method": self.command, "path": u.path, "query": u.query, "headers": headers, "body": body.decode("utf-8", "replace")}
                mock.requests.append(rec)
                m = AUTH_RX.match(self.headers.get("Authorization") or "")
                if not m:
                    rec["verified"] = False
                    return self.reply(403, {"__type": "MissingAuthenticationTokenException", "message": "no signature"})
                access, _, region, service, signed, sig = m.groups()
                if access not in mock.keys:
                    rec["verified"] = False
                    return self.reply(403, {"__type": "UnrecognizedClientException", "message": "bad key"})
                _, want = signature(self.command, u.path, u.query, headers, signed.split(";"), body, mock.keys[access],
                                    self.headers.get("X-Amz-Date") or "", region, service)
                rec.update(verified=hmac.compare_digest(want, sig), region=region, service=service, signed=signed.split(";"))
                if not rec["verified"]:
                    return self.reply(403, {"__type": "InvalidSignatureException", "message": "signature mismatch"})
                if mock.handler:
                    return self.raw(*mock.handler(rec))
                self.reply(200, {"ok": True})

            do_GET = do_POST = handle_any

        ThreadingHTTPServer.allow_reuse_address = False   # Windows: otherwise two servers share the port silently and a request can hang on the dead one
        self.server = ThreadingHTTPServer(("127.0.0.1", port), H)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *a):
        self.server.shutdown()
        self.server.server_close()
