#!/usr/bin/env python3
"""Compare warm-cache, forced project rebuilds with XamlX and XamlG on identical inputs."""
import argparse
import hashlib
import json
import platform
from pathlib import Path
import re
import statistics
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
PROJECTS = ["Avalonia.Themes.Simple", "Avalonia.Themes.Fluent", "ControlCatalog"]


def capture(*command):
    return subprocess.check_output(command, cwd=ROOT, text=True).strip()


def run(command, log):
    started = time.perf_counter()
    with log.open("w") as output:
        result = subprocess.run(command, cwd=ROOT, stdout=output, stderr=subprocess.STDOUT)
    elapsed = time.perf_counter() - started
    if result.returncode:
        raise RuntimeError(f"Build failed; see {log}")
    return elapsed


def task_ms(log, name):
    matches = re.findall(r"^\s*(\d+) ms\s+" + re.escape(name) + r"\s+\d+ calls?\s*$", log, re.MULTILINE)
    return sum(map(int, matches))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--iterations", type=int, default=3)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/controlcatalog-benchmark")
    args = parser.parse_args()
    if args.iterations < 3:
        parser.error("Use at least three measured iterations.")
    if capture("git", "status", "--porcelain", "--untracked-files=no"):
        raise RuntimeError("Commit tracked changes before benchmarking so the report identifies exact inputs.")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    manifest = json.loads((ROOT / "samples/controlcatalog-upstream.json").read_text())
    report = {
        "commit": capture("git", "rev-parse", "HEAD"),
        "tree": capture("git", "rev-parse", "HEAD^{tree}"),
        "upstream": manifest["revision"],
        "platform": platform.platform(),
        "architecture": platform.machine(),
        "sdk": capture(args.dotnet, "--version"),
        "iterations": args.iterations,
        "method": "Sequential warm-cache forced Release rebuilds, one project at a time. Dependencies restored/built before timing; BuildProjectReferences=false, UseSharedCompilation=false, MSBuild maxcpucount=1. New dotnet/compiler processes per measurement. No restore, framework build, browser publish or application execution in timed region. Includes project evaluation, resources, C# and XAML compilation, and output copying; not an isolated XAML parser benchmark.",
        "samples": [],
    }
    if platform.system() == "Darwin":
        report["cpu"] = capture("sysctl", "-n", "machdep.cpu.brand_string")
        report["memory_bytes"] = int(capture("sysctl", "-n", "hw.memsize"))
    (output / "dotnet-info.txt").write_text(capture(args.dotnet, "--info") + "\n")
    common = [args.dotnet, "build", "-c", "Release", "-p:AvsSkipBuildingLegacyTargetFrameworks=True",
              "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-m:1", "-nologo"]
    try:
        for compiler in ("XamlX", "XamlG"):
            enabled = "true" if compiler == "XamlG" else "false"
            mode = f"-p:XamlGEnabled={enabled}"
            print(f"Preparing {compiler} dependencies and outputs (not timed)", flush=True)
            run(common + ["samples/ControlCatalog/ControlCatalog.csproj", mode], output / f"{compiler}-prepare.log")
            for project in PROJECTS:
                for iteration in range(1, args.iterations + 1):
                    log = output / f"{compiler}-{project}-{iteration}.log"
                    command = common + [f"samples/{project}/{project}.csproj", mode,
                                        "--no-restore", "-p:BuildProjectReferences=false", "-t:Rebuild",
                                        "-clp:PerformanceSummary;Summary"]
                    elapsed = run(command, log)
                    contents = log.read_text()
                    csc = task_ms(contents, "Csc")
                    xamlx = task_ms(contents, "CompileAvaloniaXamlTask")
                    if csc <= 0 or (compiler == "XamlX" and xamlx <= 0) or (compiler == "XamlG" and xamlx != 0):
                        raise RuntimeError(f"Expected compilation backend did not run; inspect {log}")
                    report["samples"].append({"compiler": compiler, "project": project, "iteration": iteration,
                                              "wall_seconds": elapsed, "csc_ms": csc, "xamlx_ms": xamlx,
                                              "compile_ms": csc + xamlx, "command": command,
                                              "log": log.name, "sha256": hashlib.sha256(log.read_bytes()).hexdigest()})
                    (output / "results.json").write_text(json.dumps(report, indent=2) + "\n")
                    print(f"{compiler} {project} #{iteration}: {elapsed:.3f}s wall, {csc + xamlx}ms compiler tasks", flush=True)
    finally:
        # Leave the normal source-reference defaults usable after the opt-out comparison.
        run(common + ["samples/ControlCatalog/ControlCatalog.csproj"], output / "restore-default-build.log")
    lines = ["| Project | XamlX median wall | XamlG median wall | XamlG / XamlX | XamlX compiler tasks | XamlG compiler tasks |",
             "| --- | ---: | ---: | ---: | ---: | ---: |"]
    for project in PROJECTS:
        values = {compiler: [sample for sample in report["samples"] if sample["project"] == project and sample["compiler"] == compiler]
                  for compiler in ("XamlX", "XamlG")}
        wall = {compiler: statistics.median(sample["wall_seconds"] for sample in samples) for compiler, samples in values.items()}
        compile_time = {compiler: statistics.median(sample["compile_ms"] for sample in samples) / 1000 for compiler, samples in values.items()}
        lines.append(f"| {project} | {wall['XamlX']:.3f}s | {wall['XamlG']:.3f}s | {wall['XamlG'] / wall['XamlX']:.2f}x | {compile_time['XamlX']:.3f}s | {compile_time['XamlG']:.3f}s |")
    (output / "summary.md").write_text("\n".join(lines) + "\n\n" + report["method"] + "\n")
    print("\n".join(lines))


if __name__ == "__main__":
    main()
