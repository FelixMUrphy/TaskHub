"""Translations (i18n/*.json + each connector's src/Host/Connectors/<Name>/*.i18n.json, one source for the hub and the panel): every key has all 10 languages and no empty
text, no key is defined twice (in a file or across files), the {n} placeholders match English, plural entries have
no more forms than the language has, the keys the code uses exist, and the hub serves the panel exactly this table."""
import glob, json, os, re, urllib.request

URL = os.environ.get("TASKHUB_URL", "http://127.0.0.1:47899")
ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
LANGS = ["en", "uk", "es", "de", "fr", "it", "pt", "pl", "zh", "ja"]
FORMS = {"uk": 3, "pl": 3, "zh": 1, "ja": 1}   # plural forms per language (others: 2); same rules as L.PluralForm
fails = 0


def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {extra}"))
    if not cond:
        fails += 1


def no_dupes(pairs):
    keys = [k for k, _ in pairs]
    dup = {k for k in keys if keys.count(k) > 1}
    if dup:
        raise ValueError("duplicate keys: " + ", ".join(sorted(dup)))
    return dict(pairs)


tables = {"host": {}, "panel": {}}
bad_json, cross_dupes = [], []
files = sorted(glob.glob(os.path.join(ROOT, "i18n", "*.json"))
               + glob.glob(os.path.join(ROOT, "src", "Host", "Connectors", "**", "*.i18n.json"), recursive=True))
check("i18n files found", len(files) >= 2, files)
for f in files:
    try:
        d = json.load(open(f, encoding="utf-8"), object_pairs_hook=no_dupes)
    except ValueError as e:
        bad_json.append(f"{os.path.basename(f)}: {e}")
        continue
    extra = set(d) - {"_doc", "host", "panel"}
    if extra:
        bad_json.append(f"{os.path.basename(f)}: unknown sections {extra}")
    for ns in ("host", "panel"):
        for k, v in d.get(ns, {}).items():
            if k in tables[ns]:
                cross_dupes.append(f"{ns}.{k} ({os.path.basename(f)})")
            tables[ns][k] = v
check("i18n files parse, no duplicate keys inside a file", not bad_json, bad_json)
check("no key defined in two files", not cross_dupes, cross_dupes)

PH = re.compile(r"\{(\d+)\}")
missing, empty, ph_bad, forms_bad = [], [], [], []
for ns, t in tables.items():
    for k, v in t.items():
        if not isinstance(v, dict) or set(v) != set(LANGS):
            missing.append(f"{ns}.{k}: {sorted(set(LANGS) ^ set(v if isinstance(v, dict) else []))}")
            continue
        en = v["en"]
        for l in LANGS:
            x = v[l]
            if isinstance(en, list):
                if not isinstance(x, list) or len(x) != len(en) or not all(isinstance(i, str) and i.strip() for i in x):
                    empty.append(f"{ns}.{k}.{l}")
                continue
            if not isinstance(x, str) or not x.strip():
                empty.append(f"{ns}.{k}.{l}")
                continue
            if set(PH.findall(x)) != set(PH.findall(en)):
                ph_bad.append(f"{ns}.{k}.{l}: {sorted(set(PH.findall(x)))} vs en {sorted(set(PH.findall(en)))}")
            if "|" in en and x.count("|") + 1 > FORMS.get(l, 2):
                forms_bad.append(f"{ns}.{k}.{l}: {x.count('|') + 1} forms")
check("every key has all 10 languages", not missing, missing[:10])
check("no empty texts", not empty, empty[:10])
check("placeholders {n} match English in every language", not ph_bad, ph_bad[:10])
check("plural entries: no more forms than the language has", not forms_bad, forms_bad[:10])

# keys used in code with a literal name must exist
src = ""
for f in glob.glob(os.path.join(ROOT, "src", "Host", "**", "*.cs"), recursive=True):
    src += open(f, encoding="utf-8").read()
used_host = set(re.findall(r'\bL\.[TN]\("([A-Za-z0-9_]+)"', src))
unknown = sorted(used_host - set(tables["host"]))
# labels of the connectors' number fields (SettingsRows.Field(key, label, ...)) are panel keys
labels = set(re.findall(r'SettingsRows\.Field\("[A-Za-z0-9_]+", "([A-Za-z0-9_]+)"', src))
check(f"connector field labels exist in the panel table ({len(labels)} used)", labels and labels <= set(tables["panel"]),
      sorted(labels - set(tables["panel"])))
check(f"host keys used in code exist ({len(used_host)} used)", not unknown, unknown)
ui = ""
for f in glob.glob(os.path.join(ROOT, "ui", "*")):
    ui += open(f, encoding="utf-8").read()
used_ui = set(re.findall(r'(?<![\w.])T\("([A-Za-z0-9_]+)"', ui)) | set(re.findall(r'data-i="([A-Za-z0-9_]+)"', ui))
unknown = sorted(k for k in used_ui - set(tables["panel"]) if not k.endswith("_"))   # "st_" + state: built at run time
check(f"panel keys used in ui/ exist ({len(used_ui)} used)", not unknown, unknown)

# the hub serves the panel this very table
try:
    op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    page = op.open(urllib.request.Request(URL + "/ui", headers={"X-TaskHub": "1"}), timeout=10).read().decode("utf-8")
    m = re.search(r"^const I18N = (.*);\r?$", page, re.M)
    served = json.loads(m.group(1)) if m else None
    want = {l: {k: v[l] for k, v in tables["panel"].items()} for l in LANGS}
    check("/ui carries the panel table from i18n/*.json", served == want, "differs" if m else "no I18N line")
    check("/ui has no raw __I18N__ slot", "__I18N__" not in page)
except OSError as e:
    check("/ui reachable", False, e)

print("i18n tests:", "OK" if fails == 0 else f"{fails} FAILED")
raise SystemExit(1 if fails else 0)
