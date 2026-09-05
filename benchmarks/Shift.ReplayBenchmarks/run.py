"""Run simulator processes sequentially so they do not contend with each other."""

import argparse
import json
import subprocess
import sys
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sample", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--process-runs", type=int, default=3)
    args = parser.parse_args()
    if args.process_runs < 1:
        parser.error("--process-runs must be positive")
    directory = Path(__file__).resolve().parent
    assembly = directory / "bin/Release/net10.0/Shift.ReplayBenchmarks.dll"
    hashes = set()
    for run in range(1, args.process_runs + 1):
        output = args.output / f"run-{run}"
        output.mkdir(parents=True, exist_ok=True)
        commands = [("shift", ["dotnet", str(assembly), str(args.sample), str(output)])]
        for repeat_days in (1, 20):
            for engine in ("hftbacktest", "nautilus"):
                name = f"{engine}-{repeat_days}"
                commands.append((name, [sys.executable, str(directory / "peers.py"),
                                        str(output / "common-tape.csv"), str(output / f"{name}.json"),
                                        "--engine", engine, "--repeat-days", str(repeat_days)]))
        # Reverse peer order on alternate runs to reduce a fixed ordering bias.
        if run % 2 == 0:
            commands[1:] = reversed(commands[1:])
        for name, command in commands:
            print(f"Process {run}/{args.process_runs}: {name}", flush=True)
            with (output / f"{name}.log").open("w") as log:
                subprocess.run(command, stdout=log, stderr=log, check=True)
        report = json.loads((output / "shift.json").read_text())
        hashes.add(report["SourceOutcome"]["OutcomeSha256"])
    if len(hashes) != 1:
        raise RuntimeError(f"SHIFT outcomes differed across processes: {hashes}")
    print(f"Verified one SHIFT outcome across {args.process_runs} fresh processes: {hashes.pop()}")


if __name__ == "__main__":
    main()
