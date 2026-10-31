"""Windows Credential Manager helpers for the tests (stdlib ctypes only): the hub's credential names and a probe that
tells whether this logon session has a usable credential set (a CI runner may not: CredWrite can even report success)."""
import ctypes, hashlib, os, secrets
from ctypes import wintypes


class CREDENTIAL(ctypes.Structure):
    _fields_ = [("Flags", wintypes.DWORD), ("Type", wintypes.DWORD), ("TargetName", wintypes.LPWSTR),
                ("Comment", wintypes.LPWSTR), ("LastWritten", wintypes.FILETIME), ("CredentialBlobSize", wintypes.DWORD),
                ("CredentialBlob", ctypes.c_void_p), ("Persist", wintypes.DWORD), ("AttributeCount", wintypes.DWORD),
                ("Attributes", ctypes.c_void_p), ("TargetAlias", wintypes.LPWSTR), ("UserName", wintypes.LPWSTR)]


adv = ctypes.WinDLL("advapi32", use_last_error=True)
adv.CredWriteW.argtypes = [ctypes.POINTER(CREDENTIAL), wintypes.DWORD]
adv.CredWriteW.restype = wintypes.BOOL
adv.CredReadW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.POINTER(ctypes.c_void_p)]
adv.CredReadW.restype = wintypes.BOOL
adv.CredDeleteW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD]
adv.CredDeleteW.restype = wintypes.BOOL
adv.CredFree.argtypes = [ctypes.c_void_p]
adv.CredFree.restype = None
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.GetLongPathNameW.argtypes = [wintypes.LPCWSTR, wintypes.LPWSTR, wintypes.DWORD]
k32.GetLongPathNameW.restype = wintypes.DWORD


def long_path(p):
    """Like .NET's Path.GetFullPath: 8.3 short names (C:\\Users\\RUNNER~1 on CI runners) expanded."""
    p = os.path.abspath(p)
    buf = ctypes.create_unicode_buffer(1024)
    n = k32.GetLongPathNameW(p, buf, len(buf))
    return buf.value if 0 < n < len(buf) else p


def target(data, name):
    """The hub's credential name for a secret of a hub with this data folder (Secrets.Target)."""
    tag = hashlib.sha256(long_path(data).rstrip("\\").lower().encode("utf-8")).hexdigest().upper()[:10]
    return "TaskHub/" + name + "@" + tag


def read(t):
    """The secret's text, None when absent; raises OSError for any other failure."""
    p = ctypes.c_void_p()
    if not adv.CredReadW(t, 1, 0, ctypes.byref(p)):
        err = ctypes.get_last_error()
        if err == 1168:
            return None
        raise OSError(err, f"CredRead error {err}")
    try:
        c = ctypes.cast(p, ctypes.POINTER(CREDENTIAL)).contents
        return ctypes.string_at(c.CredentialBlob, c.CredentialBlobSize).decode("utf-16-le")
    finally:
        adv.CredFree(p)


def exists(t):
    try:
        return read(t) is not None
    except OSError:
        return False


def write(t, secret):
    blob = secret.encode("utf-16-le")
    buf = ctypes.create_string_buffer(blob, max(len(blob), 1))
    c = CREDENTIAL(Type=1, TargetName=t, CredentialBlobSize=len(blob), CredentialBlob=ctypes.cast(buf, ctypes.c_void_p),
                   Persist=2, UserName="taskhub-test")
    if not adv.CredWriteW(ctypes.byref(c), 0):
        err = ctypes.get_last_error()
        raise OSError(err, f"CredWrite error {err}")


def delete(t):
    adv.CredDeleteW(t, 1, 0)


_probe = []


def probe():
    """None when the Credential Manager works here (write, read back, delete a probe credential), else the reason."""
    if not _probe:
        t = "TaskHub/test-probe@" + secrets.token_hex(5)
        value = "probe-" + secrets.token_hex(8)
        try:
            write(t, value)
            back = read(t)
            _probe.append(None if back == value else
                          "error 1168: written, not readable back" if back is None else "read back a different value")
        except OSError as e:
            _probe.append(f"error {e.errno}")
        finally:
            delete(t)
    return _probe[0]
