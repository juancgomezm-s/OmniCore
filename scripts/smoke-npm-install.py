"""Exercise an npm-installed CLI and verify a real persisted journal, without providers."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from smoke_webp import smoke_webp


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("prefix", type=Path)
    parser.add_argument("evidence", type=Path)
    parser.add_argument("--after-update", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    prefix = args.prefix.resolve(strict=True)
    modules = prefix / ("node_modules" if os.name == "nt" else "lib/node_modules")
    package = modules / "omnicore-cli"
    launcher = package / "bin/omni.js"
    if not launcher.is_file():
        raise RuntimeError(f"Missing installed launcher: {launcher}")
    command = [shutil.which("node"), str(launcher)]
    dll = package / "dist/npm/omni.dll"
    digest = lambda file: hashlib.sha256(file.read_bytes()).hexdigest()
    if digest(dll) != digest(root / "dist/npm/omni.dll"):
        raise RuntimeError("The installed CLI differs from the freshly built CLI")
    evidence = args.evidence.resolve()
    evidence.mkdir(parents=True, exist_ok=True)
    snapshot = evidence / "snapshot.json"
    if args.after_update:
        before = json.loads(snapshot.read_text(encoding="utf8"))
        for name, expected in before.items():
            if digest(evidence / name) != expected:
                raise RuntimeError(f"Update modified user state: {name}")
    else:
        if snapshot.exists():
            raise RuntimeError("Use a fresh evidence directory for the initial installation")
        (evidence / "config").mkdir()
        (evidence / "config/settings-sentinel.txt").write_text("preserve preferences\n", encoding="utf8")
        (evidence / "workspace").mkdir()
        (evidence / "workspace/user-file.txt").write_text("preserve workspace\n", encoding="utf8")
    env = dict(os.environ, OMNICORE_CONFIG_DIR=str(evidence / "config"),
               OMNICORE_DATA_DIR=str(evidence / "data"))
    def run(name, arguments):
        result = subprocess.run([*command, *arguments], cwd=evidence / "workspace", env=env,
                                stdin=subprocess.DEVNULL, capture_output=True, timeout=90)
        (evidence / f"{name}.stdout").write_bytes(result.stdout)
        (evidence / f"{name}.stderr").write_bytes(result.stderr)
        if result.returncode:
            raise RuntimeError(f"{name}: exit {result.returncode}; see evidence stderr")
        return result.stdout.decode("utf8")
    suffix = "updated" if args.after_update else "installed"
    run(f"help-{suffix}", ["--help"])
    if not args.after_update:
        output = run("sim", ["sim", str(root / "docs/sim/multi-item-plan.yaml"), "--json"])
        outcome = json.loads(output.splitlines()[-1])
        assert outcome["type"] == "RunOutcome" and outcome["outcome"] == "Completed", outcome
    run(f"verify-{suffix}", ["verify-journal", "--json"])
    smoke_webp(command, Path(tempfile.mkdtemp(prefix=f"webp-{suffix}-", dir=evidence)))
    if not args.after_update:
        files = [evidence / "config/settings-sentinel.txt", evidence / "workspace/user-file.txt"]
        files.extend((evidence / "data").rglob("journal.db"))
        if len(files) < 3:
            raise RuntimeError("The simulation did not persist its journal")
        snapshot.write_text(json.dumps({str(file.relative_to(evidence)): digest(file) for file in files},
                                       indent=2) + "\n", encoding="utf8")
    print(f"npm {suffix}: CLI SHA256={digest(dll)}, help and real journal verified; user state preserved")


if __name__ == "__main__":
    main()
