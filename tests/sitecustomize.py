"""Python loads this at start when tests/ is on PYTHONPATH (run_tests.cmd): TASKHUB_TOKEN goes on every urllib request to loopback that has no Authorization of its own, so the tests run against the real token check without each carrying it. Empty or unset, as in token_test.py: nothing is added."""
import os
import urllib.parse
import urllib.request

_open = urllib.request.OpenerDirector.open


def _with_token(self, fullurl, data=None, *args, **kw):
    token = os.environ.get("TASKHUB_TOKEN")
    if token:
        req = urllib.request.Request(fullurl) if isinstance(fullurl, str) else fullurl
        if urllib.parse.urlsplit(req.full_url).hostname in ("127.0.0.1", "localhost", "::1") and not req.has_header("Authorization"):
            req.add_header("Authorization", "Bearer " + token)
        fullurl = req
    return _open(self, fullurl, data, *args, **kw)


urllib.request.OpenerDirector.open = _with_token
