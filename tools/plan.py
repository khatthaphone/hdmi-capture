#!/usr/bin/env python3
"""Keep docs/tasks in sync.

  tools/plan.py status 031 done "message"     set status (+ dated log line), then sync
  tools/plan.py log 031 "message"             append a dated log line
  tools/plan.py check 031 "substring"         tick the acceptance criterion containing substring
  tools/plan.py sync                          rebuild 000.INIT.md table, progress bar, batches
"""
import datetime, glob, os, re, sys

D = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "docs", "tasks")
TODAY = datetime.date.today().isoformat()


def path(task_id):
    return glob.glob(os.path.join(D, f"{task_id}.*.md"))[0]


def append_log(task_id, message):
    p = path(task_id)
    s = open(p).read().rstrip("\n") + f"\n- {TODAY} — {message}\n"
    open(p, "w").write(s)


def set_status(task_id, status):
    p = path(task_id)
    s = re.sub(r"^\*\*Status:\*\* .*$", f"**Status:** {status}", open(p).read(), count=1, flags=re.M)
    open(p, "w").write(s)


def check(task_id, needle):
    p = path(task_id)
    lines = open(p).read().split("\n")
    hits = [i for i, l in enumerate(lines) if l.startswith("- [ ] ") and needle.lower() in l.lower()]
    if not hits:
        sys.exit(f"no unchecked criterion containing {needle!r} in {task_id}")
    for i in hits:
        lines[i] = lines[i].replace("- [ ] ", "- [x] ", 1)
    open(p, "w").write("\n".join(lines))


def load():
    tasks = []
    for p in sorted(glob.glob(os.path.join(D, "[0-9][0-9][0-9].*.md"))):
        if os.path.basename(p).startswith("000"):
            continue
        s = open(p).read()
        deps = re.search(r"^\*\*Depends on:\*\* (.*)$", s, re.M).group(1)
        tasks.append(dict(
            id=os.path.basename(p)[:3], file=os.path.basename(p),
            title=re.search(r"^# \d+ — (.*)$", s, re.M).group(1),
            status=re.search(r"^\*\*Status:\*\* (\S+)", s, re.M).group(1),
            phase=re.search(r"^\*\*Phase:\*\* (.*)$", s, re.M).group(1),
            deps=[d.strip() for d in deps.split(",") if d.strip() not in ("none", "")],
            cx=re.search(r"^\*\*Complexity:\*\* (\w+)", s, re.M).group(1),
            awaiting="awaiting hardware" in s.split("## Log", 1)[-1].split("\n")[-2].lower() if "## Log" in s else False,
        ))
    return tasks


def sync():
    tasks = load()
    done = {t["id"] for t in tasks if t["status"] == "done"}
    total, n = len(tasks), len(done)
    filled = round(20 * n / total)
    bar = "█" * filled + "░" * (20 - filled)

    phases = {}
    for t in tasks:
        phases.setdefault(t["phase"], []).append(t)
    tables = []
    for ph in sorted(phases, key=lambda name: int(re.search(r"Phase (\d+)", name).group(1))):
        rows = "\n".join(
            f"| {t['id']} | [{t['title']}]({t['file']}) | {t['status']} | {t['cx']} | {', '.join(t['deps']) or '—'} |"
            for t in phases[ph])
        tables.append(f"### {ph}\n\n| ID | Task | Status | Complexity | Depends on |\n|----|------|--------|-----------|-----------|\n{rows}")

    ids = {t["id"]: t for t in tasks}
    pending = {i for i, t in ids.items() if t["status"] not in ("done", "blocked")}
    blocked = sorted(i for i, t in ids.items() if t["status"] == "blocked")
    satisfied, batches = set(done), []
    while pending:
        b = sorted(i for i in pending if all(d in satisfied for d in ids[i]["deps"]))
        if not b:
            break
        batches.append(b)
        satisfied |= set(b)
        pending -= set(b)
    lines = [f"- **Batch {k}**{' (unblocked now)' if k == 1 else ''}: {', '.join(b)}" for k, b in enumerate(batches, 1)]
    if pending or blocked:
        lines.append(f"- **Waiting on blocked tasks** ({', '.join(blocked)}): {', '.join(sorted(pending)) or '—'}")
    awaiting = sorted(t["id"] for t in tasks if t["status"] == "in-progress" and t["awaiting"])
    if awaiting:
        lines.append(f"- **Code complete, awaiting hardware check**: {', '.join(awaiting)}")

    p = os.path.join(D, "000.INIT.md")
    s = open(p).read()
    s = re.sub(r"^\*\*Progress:\*\* .*$", f"**Progress:** {n}/{total} tasks complete `[{bar}]` {round(100 * n / total)}%", s, count=1, flags=re.M)
    s = re.sub(r"(## Parallel execution\n\n.*?\n\n).*?(\n## Phases)", lambda m: m.group(1) + "\n".join(lines) + "\n" + m.group(2), s, count=1, flags=re.S)
    s = re.sub(r"## Phases\n.*\Z", "## Phases\n\n" + "\n\n".join(tables) + "\n", s, count=1, flags=re.S)
    open(p, "w").write(s)


if __name__ == "__main__":
    cmd, *args = sys.argv[1:] or ["sync"]
    if cmd == "status":
        set_status(args[0], args[1])
        if len(args) > 2:
            append_log(args[0], args[2])
    elif cmd == "log":
        append_log(args[0], args[1])
    elif cmd == "check":
        check(args[0], args[1])
    sync()
