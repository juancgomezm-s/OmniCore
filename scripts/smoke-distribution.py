"""Validate the real CLI distribution without credentials or provider calls."""
import argparse
import json
import os
from pathlib import Path
import sqlite3
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    parser.add_argument("evidence", type=Path)
    args = parser.parse_args()
    executable = args.executable.resolve(strict=True)
    evidence = args.evidence.resolve()
    evidence.mkdir(parents=True, exist_ok=False)
    repo = Path(__file__).resolve().parents[1]
    results = []

    for scenario in ("multi-item-plan", "taskgraph-nm", "ask-permission", "plan-approval", "crash-resume", "injected-crash"):
        root = evidence / scenario
        workspace = root / "workspace"
        workspace.mkdir(parents=True)
        env = dict(os.environ, OMNICORE_DATA_DIR=str(root / "data"),
                   OMNICORE_CONFIG_DIR=str(root / "config"))
        for name in ("DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64"):
            env.pop(name, None)

        def run(name, command, expected=0):
            process = subprocess.run([str(executable), *command], cwd=workspace, env=env,
                                     stdin=subprocess.DEVNULL, capture_output=True, timeout=60)
            (root / (name + ".stdout")).write_bytes(process.stdout)
            (root / (name + ".stderr")).write_bytes(process.stderr)
            if process.returncode != expected:
                raise RuntimeError(f"{scenario}/{name}: exit {process.returncode}, expected {expected}")
            return process.stdout.decode("utf-8")

        run("help", ["--help"])
        expected = 1 if scenario == "injected-crash" else 0
        source = "--crash" if expected else str(repo / "docs" / "sim" / (scenario + ".yaml"))
        output = run("sim", ["sim", source, "--json"], expected)
        outcome = json.loads(output.splitlines()[-1])
        assert outcome["type"] == "RunOutcome"
        assert outcome["outcome"] == ("Interrupted" if expected else "Completed")
        journal, = (root / "data").rglob("journal.db")
        with sqlite3.connect(journal) as db:
            if scenario in ("crash-resume", "injected-crash"):
                before = db.execute("SELECT seq,event_type,toolcall_id,payload FROM events ORDER BY id").fetchall()
                assert sum(row[1] == "toolcall.started" for row in before) == 1
                run("resume", ["sim", "--resume", "--json"])
                after = db.execute("SELECT seq,event_type,toolcall_id,payload FROM events ORDER BY id").fetchall()
                assert after[:len(before)] == before, "Journal prefix changed"
                started = [row[2] for row in after if row[1] == "toolcall.started"]
                reconciled = [row[2] for row in after if row[1] == "toolcall.reconciled"]
                assert len(started) == 1 and reconciled == started, "Effect duplicated or not reconciled"
                run("resume-again", ["sim", "--resume", "--json"])
                repeated = db.execute("SELECT seq,event_type,toolcall_id,payload FROM events ORDER BY id").fetchall()
                assert repeated == after, "Second resume changed the journal"
            count, = db.execute("SELECT count(*) FROM events").fetchone()
        run("verify", ["verify-journal", "--json"])
        results.append({"scenario": scenario, "events": count, "verified": True})
    (evidence / "summary.json").write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(results))


if __name__ == "__main__":
    main()
