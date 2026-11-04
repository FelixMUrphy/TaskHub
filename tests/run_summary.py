"""The end of a test run: totals, what was skipped, and two things that fail a run although every script passed.
- An ERR line in the test hub's log that no test caused on purpose (ALLOWED below).
- Under CI, a check skipped for lack of a Credential Manager: the money suites would then prove nothing.
Usage: run_summary.py TOTALS_FOLDER HUB_DATA_FOLDER. Exit 1 when the run must fail."""
import glob, os, re, sys

# ERR lines a test causes on purpose: (what, regular expression)
ALLOWED = [("guard_test: a stop that keeps failing", r"ERR runpod +guard: retry #\w+: still failing")]


def read_lines(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return [x.rstrip("\n") for x in f]
    except OSError:
        return []


def main(folder, data, ci):
    bad = 0
    totals = [x.split(" ", 4) for x in read_lines(os.path.join(folder, "totals.txt"))]
    n = [sum(int(t[i]) for t in totals) for i in range(3)]
    print(f"TOTAL {n[0]} PASS, {n[1]} FAIL, {n[2]} SKIP in {len(totals)} scripts")
    if n[1]:
        print(f"SUMMARY BAD {n[1]} checks failed in scripts that may have exited 0")
        bad = 1
    skips = read_lines(os.path.join(folder, "skips.txt"))
    for s in skips:
        print("  " + s)
    if ci:
        lost = [s for s in skips if "Credential Manager" in s or "no usable" in s]
        if lost:
            print(f"SUMMARY BAD {len(lost)} checks skipped under CI for lack of a Credential Manager")
            bad = 1
    errs = []
    for path in glob.glob(os.path.join(data, "taskhub.log*")):
        for line in read_lines(path):
            if re.match(r"\S+ \S+ ERR ", line) and not any(re.search(rx, line) for _, rx in ALLOWED):
                errs.append(line)
    if errs:
        print(f"SUMMARY BAD {len(errs)} ERR lines in the test hub's log that no test expects:")
        for e in errs[:20]:
            print("  " + e[:300])
        bad = 1
    else:
        print("SUMMARY OK no unexpected ERR lines in the test hub's log")
    return bad


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2], bool(os.environ.get("CI"))))
