#!/usr/bin/env python3
"""Test of `backup` and `import --file` with MADE-UP incidents only, in the separate database oncall_local_test.
Cycle: import a file -> backup -> wipe the database -> restore from the backup -> compare. Run after
`docker compose up -d --build`. Set ONCALL_CONTAINER=<name> to use another container with the same image. Standard library only."""
import codecs, datetime as dt, json, os, subprocess, tempfile, time

CONTAINER = os.environ.get("ONCALL_CONTAINER", "oncall-local-mcp")
DB = "oncall_local_test"
TOTAL = 30
results = []
def check(name, ok, detail=""):
    results.append(bool(ok)); print(("PASS " if ok else "FAIL ") + name + (f"  [{detail}]" if detail else ""))

def app(*args, env=None):
    e = ["-e", f"MONGO_DATABASE={DB}"] + [x for k, v in (env or {}).items() for x in ("-e", f"{k}={v}")]
    return subprocess.run(["docker", "exec", *e, CONTAINER, "dotnet", "OnCallHelperLocal.dll", *args], capture_output=True, text=True)
def sh(cmd): return subprocess.run(["docker", "exec", CONTAINER, "sh", "-c", cmd], capture_output=True, text=True)
def mongosh(js):
    return subprocess.run(["docker", "exec", "oncall-local-mongodb", "mongosh", "--quiet", "--eval", js], capture_output=True, text=True).stdout.strip()
def count(): return int(mongosh(f"print(db.getSiblingDB('{DB}').incidents.countDocuments())") or -1)
def copy_in(path, dest): subprocess.run(["docker", "cp", path, f"{CONTAINER}:{dest}"], check=True, capture_output=True)
def copy_out(src, dest): subprocess.run(["docker", "cp", f"{CONTAINER}:{src}", dest], check=True, capture_output=True)

base = dt.datetime(2026, 9, 1, 12, 0, 0, tzinfo=dt.timezone.utc)
incidents = []
for i in range(TOTAL):
    created = base - dt.timedelta(minutes=i, milliseconds=(i * 7) % 1000)
    incidents.append({
        "id": f"{0x65b000000000000000000000 + i:024x}", "title": f"widget-service incident #{i} café", "serviceName": "widget-service",
        "environment": "production", "severity": ["sev2", "sev3", "sev4"][i % 3],
        "description": f"The widget queue stalled after a restart and jobs were not picked up again (case {i}). Naïve retries made it worse.",
        "score": None, "createdAt": created.strftime("%Y-%m-%dT%H:%M:%S.") + f"{created.microsecond // 1000:03d}0000Z",
        "resolution": {"rootCause": f"cause {i}", "summary": f"fixed {i}", "stepsTaken": [f"step a {i}", f"step b {i}"], "resolvedBy": "on-call"}})
incidents[3]["resolution"] = None   # unresolved

work = tempfile.mkdtemp()
src = os.path.join(work, "seed.json"); json.dump(incidents, open(src, "w"), ensure_ascii=False)
bom = os.path.join(work, "seed-bom.json")
open(bom, "wb").write(codecs.BOM_UTF8 + open(src, "rb").read())

mongosh(f"db.getSiblingDB('{DB}').dropDatabase()")
sh("rm -rf /tmp/bk /tmp/seed.json /tmp/seed-bom.json")

# ---------- import --file ----------
copy_in(src, "/tmp/seed.json")
r = app("import", "--file", "/tmp/seed.json")
check("import --file loads the incidents", r.returncode == 0 and f"imported {TOTAL} of {TOTAL}" in r.stdout and count() == TOTAL, (r.stdout.strip().splitlines() or [r.stderr[:80]])[0][:80])
r = app("import", "--file", "/tmp/does-not-exist.json")
check("a missing file gives a clear message and a failure code", r.returncode != 0 and "File not found" in r.stderr)
r = app("import", "--file")
check("--file without a path gives a clear message", r.returncode != 0 and "--file needs a path" in r.stderr)
mongosh(f"db.getSiblingDB('{DB}').dropDatabase()")
copy_in(bom, "/tmp/seed-bom.json")
r = app("import", "--dry-run", "--file", "/tmp/seed-bom.json")
check("a file that starts with a byte-order mark (Windows editors) is read", r.returncode == 0 and f"would import {TOTAL} of {TOTAL}" in r.stdout, r.stdout.strip()[:80] or r.stderr[:80])
app("import", "--file", "/tmp/seed.json")

# ---------- backup ----------
r = app("backup", "/tmp/bk")
check("backup reports the count and the file", r.returncode == 0 and f"backed up {TOTAL} incidents" in r.stdout and "incidents-backup-" in r.stdout, r.stdout.strip()[:100])
check("backup output has counts only (no incident text)", "widget queue" not in r.stdout and "café" not in r.stdout)
files = sh("ls /tmp/bk").stdout.split()
check("exactly one backup file, and no leftover temporary file", len(files) == 1 and files[0].startswith("incidents-backup-") and files[0].endswith(".json"), str(files))
mode = sh(f"stat -c %a /tmp/bk/{files[0]}").stdout.strip() if files else ""
check("the backup file is owner-only (600)", mode == "600", mode)
out = os.path.join(work, "backup.json"); copy_out(f"/tmp/bk/{files[0]}", out)
raw = open(out, encoding="utf-8").read(); data = json.loads(raw)
check("the backup has every incident once", len(data) == TOTAL and len({d["id"] for d in data}) == TOTAL)
check("no vectors in the backup (small file, rebuilt on restore)", "embedding" not in raw.lower())
check("same fields as the hosted export", set(data[0]) == {"id", "title", "serviceName", "environment", "severity", "description", "score", "createdAt", "resolution"})
def when(text):   # "2026-09-01T11:49:59.93Z" and "...59.9300000Z" are the same instant
    head, _, frac = text.rstrip("Z").partition(".")
    return dt.datetime.strptime(head, "%Y-%m-%dT%H:%M:%S").replace(tzinfo=dt.timezone.utc) + dt.timedelta(microseconds=int((frac + "000000")[:6]))
byid = {d["id"]: d for d in data}
same = all(byid[i["id"]]["title"] == i["title"] and byid[i["id"]]["description"] == i["description"] and byid[i["id"]]["resolution"] == i["resolution"]
           and when(byid[i["id"]]["createdAt"]) == when(i["createdAt"]) for i in incidents)
check("text (including accents), resolutions, unresolved state and dates are preserved exactly", same)

# ---------- wipe and restore ----------
mongosh(f"db.getSiblingDB('{DB}').dropDatabase()")
check("database is empty before the restore", count() in (0, -1), str(count()))
copy_in(out, "/tmp/restore.json")
t0 = time.time(); r = app("import", "--file", "/tmp/restore.json")
check("restore from the backup loads everything", r.returncode == 0 and f"imported {TOTAL} of {TOTAL}" in r.stdout and count() == TOTAL, f"{time.time() - t0:.0f}s")
sample = incidents[5]
dims = mongosh(f"print(db.getSiblingDB('{DB}').incidents.findOne({{_id: ObjectId('{sample['id']}')}}).Embedding.length)")
check("restored incidents are embedded again (768 dimensions)", dims == "768", dims)
rc = mongosh(f"print(db.getSiblingDB('{DB}').incidents.findOne({{_id: ObjectId('{sample['id']}')}}).Resolution.RootCause)")
check("restored resolution matches", rc == "cause 5", rc)
ms = mongosh(f"print(db.getSiblingDB('{DB}').incidents.findOne({{_id: ObjectId('{sample['id']}')}}).CreatedAt.getTime())")
want_ms = int(when(sample["createdAt"]).timestamp() * 1000)
check("the creation date survives the restore to the millisecond", ms == str(want_ms), f"{ms} vs {want_ms}")
nores = mongosh(f"print(db.getSiblingDB('{DB}').incidents.findOne({{_id: ObjectId('{incidents[3]['id']}')}}).Resolution === null)")
check("an unresolved incident is still unresolved after the restore", nores == "true", nores)
r2 = app("backup", "/tmp/bk2")
files2 = sh("ls /tmp/bk2").stdout.split(); copy_out(f"/tmp/bk2/{files2[0]}", os.path.join(work, "backup2.json"))
data2 = json.load(open(os.path.join(work, "backup2.json"), encoding="utf-8"))
check("backup -> restore -> backup gives the same incidents", sorted((d["id"], d["title"], d["description"]) for d in data2) == sorted((d["id"], d["title"], d["description"]) for d in data))

# ---------- --keep, empty database, errors ----------
for _ in range(3): time.sleep(1.1); app("backup", "/tmp/bk")
before = len(sh("ls /tmp/bk").stdout.split())
time.sleep(1.1); r = app("backup", "/tmp/bk", "--keep", "2")
after = sh("ls /tmp/bk").stdout.split()
check("--keep 2 leaves the newest two backups", before >= 4 and len(after) == 2 and "deleted" in r.stdout, f"{before} -> {len(after)}")
check("the newest file is among the kept ones", max(sh("ls /tmp/bk").stdout.split()) in after and r.stdout.split("to ")[1].split()[0].endswith(max(after)))
r = app("backup", "/tmp/bk", "--keep", "abc")
check("--keep with a non-number is rejected", r.returncode != 0 and "--keep needs" in r.stderr)
r = app("backup", "/proc/not-allowed/here")
check("an unwritable folder gives a clear message and a failure code", r.returncode != 0 and "Could not write the backup" in r.stderr, r.stderr.strip()[:80])
mongosh(f"db.getSiblingDB('{DB}').dropDatabase()")
r = app("backup", "/tmp/bk-empty")
check("an empty database writes no file and does not fail", r.returncode == 0 and "no incidents" in r.stdout and sh("ls /tmp/bk-empty 2>&1").stdout.strip() in ("", "ls: cannot access '/tmp/bk-empty': No such file or directory"))

mongosh(f"db.getSiblingDB('{DB}').dropDatabase()")
sh("rm -rf /tmp/bk /tmp/bk2 /tmp/seed.json /tmp/seed-bom.json /tmp/restore.json")
print(f"\n{sum(results)}/{len(results)} passed")
raise SystemExit(0 if all(results) else 1)
