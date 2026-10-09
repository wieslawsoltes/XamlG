#!/usr/bin/env python3
"""Compare two XamlG implementations with alternating fresh Csc processes on identical project inputs."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import re
import shutil
import statistics
import subprocess
import tempfile

from compiler_tools import dotnet_environment

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("profile_controlcatalog", ROOT / "scripts/profile-controlcatalog.py")
profile = importlib.util.module_from_spec(spec)
spec.loader.exec_module(profile)


def capture(*command):
    return subprocess.check_output(command, cwd=ROOT, text=True).strip()


def hashes(directory):
    return {str(path.relative_to(directory)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(directory.rglob("*.dll"))}


def baseline_generator(commit, dotnet, output, environment):
    """Build only the old compiler. Both variants consume the current runtime and sample references."""
    with tempfile.TemporaryDirectory(prefix="xamlg-compiler-baseline-") as temporary:
        checkout = Path(temporary) / "checkout"
        subprocess.run(["git", "worktree", "add", "--detach", str(checkout), commit], cwd=ROOT, check=True)
        try:
            profile.run([dotnet, "build", "src/XamlG.Generator/XamlG.Generator.csproj", "-c", "Release",
                         "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-m:1", "-warnaserror"],
                        output / "baseline-build.log", environment, checkout)
            built = checkout / "src/XamlG.Generator/bin/Release/netstandard2.0"
            if not (built / "XamlG.Generator.dll").is_file():
                raise RuntimeError("The baseline did not produce the expected generator assembly.")
            snapshot = output / "generators/before"
            snapshot.mkdir(parents=True)
            for source in built.glob("*.dll"):
                shutil.copy2(source, snapshot / source.name)
        finally:
            subprocess.run(["git", "worktree", "remove", "--force", str(checkout)], cwd=ROOT, check=True)
    return snapshot


def replace_option(arguments, name, value):
    result, count = re.subn(r"/" + re.escape(name) + r':(?:"[^"\n]*"|\S+)',
                            lambda _: '/' + name + ':"' + str(value) + '"', arguments)
    if count != 1:
        raise RuntimeError(f"Expected one /{name} in the captured compiler command; found {count}.")
    return result


def replace_generator(arguments, snapshot):
    replaced = []
    def analyzer(match):
        path = Path(match[1] or match[2])
        if not path.name.startswith("XamlG."):
            return match[0]
        frozen = snapshot / path.name
        if not frozen.is_file():
            raise RuntimeError(f"The {snapshot.name} snapshot is missing registered dependency {path.name}.")
        replaced.append(path.name)
        return '/analyzer:"' + str(frozen) + '"'
    result = re.sub(r'/analyzer:(?:"([^"\n]+)"|(\S+))', analyzer, arguments)
    if replaced.count("XamlG.Generator.dll") != 1:
        raise RuntimeError("Expected exactly one XamlG generator in the compiler command.")
    return result


def prepare_variants(project, compiler, response, directory, snapshots, environment):
    original = response.read_text()
    command = ([environment["DOTNET_HOST_PATH"], "exec", str(compiler)] if compiler.suffix == ".dll" else [str(compiler)]) + ["/noconfig"]
    responses, sources = {}, {}
    for variant, snapshot in snapshots.items():
        target = directory / variant
        (target / "ref").mkdir(parents=True)
        generated = target / "generated"
        generated.mkdir()
        # Roslyn's file output needs the same directory layout as the MSBuild capture.
        for source in (directory / "capture/generated").rglob("*.cs"):
            (generated / source.parent.relative_to(directory / "capture/generated")).mkdir(parents=True, exist_ok=True)
        arguments = replace_generator(original, snapshot)
        for name, value in {"out": target / (project + ".dll"), "refout": target / "ref" / (project + ".dll"),
                            "generatedfilesout": generated}.items():
            arguments = replace_option(arguments, name, value)
        capture_response = target / "capture.rsp"
        capture_response.write_text(arguments)
        # Untimed captures warm the filesystem and collect each variant's actual output.
        profile.run(command + ["@" + str(capture_response)], target / "capture.log", environment, ROOT / "samples" / project)
        files = sorted((generated / "XamlG.Generator").rglob("*.cs"))
        if not files:
            raise RuntimeError(f"No XamlG sources captured for {project}/{variant}.")
        sources[variant] = {"files": len(files), "bytes": sum(path.stat().st_size for path in files),
                            "sha256": {str(path.relative_to(generated)): hashlib.sha256(path.read_bytes()).hexdigest() for path in files}}
        full = re.sub(r'/generatedfilesout:(?:"[^"\n]*"|\S+)', "", arguments)
        captured, count = re.subn(r'/analyzer:(?:"[^"\n]*XamlG\.Generator\.dll"|\S*XamlG\.Generator\.dll)', "", full)
        if count != 1:
            raise RuntimeError("Could not isolate generated C# from the XamlG generator.")
        captured += " " + " ".join('"' + str(path) + '"' for path in files)
        for phase, text in {"full": full, "captured": captured}.items():
            phase_directory = target / phase
            (phase_directory / "ref").mkdir(parents=True)
            text = replace_option(text, "out", phase_directory / (project + ".dll"))
            text = replace_option(text, "refout", phase_directory / "ref" / (project + ".dll"))
            path = phase_directory / "compiler.rsp"
            path.write_text(text)
            responses[(variant, phase)] = path
    return command, responses, sources


def summaries(report):
    lines = ["| Project | Generated bytes before → after | Generation before → after | Full Csc CPU before → after | Captured C# CPU before → after |",
             "| --- | ---: | ---: | ---: | ---: |"]
    walls = ["| Project | Full Csc wall before → after | Captured C# wall before → after |",
             "| --- | ---: | ---: |"]
    def seconds(value):
        return "Unavailable" if value is None else f"{value:.3f}s"
    for project in report["projects"]:
        medians = {}
        pairs = []
        for phase in ("full", "captured"):
            for variant in ("before", "after"):
                rows = [row for row in project["runs"] if row["variant"] == variant and row["phase"] == phase]
                cpu = [row["user_seconds"] + row["system_seconds"] for row in rows if "user_seconds" in row]
                generation = [row.get("generator_timing", {}).get("xamlg_seconds") for row in rows]
                generation = [value for value in generation if value is not None]
                medians[variant + "-" + phase] = {
                    "wall_seconds": statistics.median(row["wall_seconds"] for row in rows),
                    "cpu_seconds": statistics.median(cpu) if cpu else None,
                    "generation_seconds": statistics.median(generation) if generation else None}
            for iteration in range(1, report["iterations"] + 1):
                rows = {row["variant"]: row for row in project["runs"] if row["phase"] == phase and row["iteration"] == iteration}
                pair = {"phase": phase, "iteration": iteration,
                        "wall_ratio": rows["after"]["wall_seconds"] / rows["before"]["wall_seconds"]}
                if all("user_seconds" in row for row in rows.values()):
                    pair["cpu_ratio"] = (rows["after"]["user_seconds"] + rows["after"]["system_seconds"]) / (rows["before"]["user_seconds"] + rows["before"]["system_seconds"])
                pairs.append(pair)
        project["medians"] = medians
        project["pairs"] = pairs
        before, after = medians["before-full"], medians["after-full"]
        captured_before, captured_after = medians["before-captured"], medians["after-captured"]
        sources = project["sources"]
        lines.append(f"| {project['project']} | {sources['before']['bytes']:,} → {sources['after']['bytes']:,} | "
                     f"{seconds(before['generation_seconds'])} → {seconds(after['generation_seconds'])} | "
                     f"{seconds(before['cpu_seconds'])} → {seconds(after['cpu_seconds'])} | "
                     f"{seconds(captured_before['cpu_seconds'])} → {seconds(captured_after['cpu_seconds'])} |")
        walls.append(f"| {project['project']} | {seconds(before['wall_seconds'])} → {seconds(after['wall_seconds'])} | "
                     f"{seconds(captured_before['wall_seconds'])} → {seconds(captured_after['wall_seconds'])} |")
    return "\n".join(lines) + "\n\n" + "\n".join(walls) + "\n\n" + report["method"] + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-ref", required=True, help="A commit present in this checkout; the report records its resolved full SHA.")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--iterations", type=int, default=6)
    parser.add_argument("--projects", nargs="+", choices=profile.PROJECTS, default=profile.PROJECTS)
    parser.add_argument("--skip-prepare", action="store_true")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/controlcatalog-comparison")
    args = parser.parse_args()
    if args.iterations < 3:
        parser.error("Use at least three measured pairs.")
    if args.baseline_ref.startswith("-"):
        parser.error("The baseline must be a commit or reference, not a Git option.")
    commit = capture("git", "rev-parse", "--verify", args.baseline_ref + "^{commit}")
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        parser.error("The baseline did not resolve to one full commit SHA.")
    output = args.output.resolve()
    if output.exists() and any(output.iterdir()):
        parser.error("Use an empty output directory to prevent stale generated sources from contaminating measurements.")
    output.mkdir(parents=True, exist_ok=True)
    dotnet, environment = dotnet_environment(args.dotnet)
    report = {"before_commit": commit, "after_commit": capture("git", "rev-parse", "HEAD"),
              "tracked_changes": capture("git", "status", "--porcelain", "--untracked-files=no"),
              "harness_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "sdk": capture(dotnet, "--version"), "platform": platform.platform(),
              "dotnet_root": environment["DOTNET_ROOT"], "processor_count_override": os.getenv("DOTNET_PROCESSOR_COUNT"),
              "iterations": args.iterations,
              "method": "Alternating adjacent before/after pairs of fresh Csc processes on identical current project/runtime references. "
                        "Baseline compiler built in a temporary worktree; both generators and their dependencies are frozen. "
                        "All builds, restores, captures and warmups are outside timed runs. Normal analyzers remain enabled. "
                        "Captured C# replaces only XamlG with that variant's generated sources; other generators/analyzers remain. "
                        "Generation is Roslyn-reported elapsed time; compiler CPU is user plus system time. "
                        "Phases are independent and nonadditive. This diagnoses changes between XamlG revisions, not the 2× XamlX acceptance target.",
              "projects": []}
    (output / "candidate.patch").write_bytes(subprocess.check_output(["git", "diff", "--binary", "HEAD"], cwd=ROOT))
    (output / "dotnet-info.txt").write_text(capture(dotnet, "--info") + "\n")
    (output / "results.json").write_text(json.dumps(report, indent=2) + "\n")
    before = baseline_generator(commit, dotnet, output, environment)
    common = [dotnet, "build", "-c", "Release", "-p:AvsSkipBuildingLegacyTargetFrameworks=True",
              "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-m:1", "-nologo", "-warnaserror"]
    if not args.skip_prepare:
        print("Preparing current dependencies (not timed)", flush=True)
        profile.run(common + ["samples/ControlCatalog/ControlCatalog.csproj"], output / "prepare.log", environment)
    for project in args.projects:
        directory = output / project
        captured = directory / "capture"
        captured.mkdir(parents=True)
        print(f"Capturing current {project} compiler command (not timed)", flush=True)
        log = captured / "build.log"
        profile.run(common + [f"samples/{project}/{project}.csproj", "--no-restore", "-t:Rebuild",
                              "-p:BuildProjectReferences=false", "-p:EmitCompilerGeneratedFiles=true",
                              f"-p:CompilerGeneratedFilesOutputPath={captured / 'generated'}", "-v:normal"], log, environment)
        after = output / "generators/after"
        compiler, response = profile.response_file(log, captured, project, after)
        snapshots = {"before": before, "after": after}
        report["generator_assemblies"] = {name: hashes(path) for name, path in snapshots.items()}
        command, responses, sources = prepare_variants(project, compiler, response, directory, snapshots, environment)
        entry = {"project": project, "sources": sources, "runs": []}
        report["projects"].append(entry)
        order = [("before", "full"), ("after", "full"), ("after", "captured"), ("before", "captured")]
        for iteration in range(args.iterations):
            for variant, phase in (order if iteration % 2 == 0 else list(reversed(order))):
                path = responses[(variant, phase)]
                measurement = profile.run(command + ["@" + str(path)], path.parent / f"compiler-{iteration + 1}.log",
                                          environment, ROOT / "samples" / project)
                measurement.update(variant=variant, phase=phase, iteration=iteration + 1)
                entry["runs"].append(measurement)
                print(f"{project} {variant}/{phase} #{iteration + 1}: {measurement['wall_seconds']:.3f}s", flush=True)
                (output / "results.json").write_text(json.dumps(report, indent=2) + "\n")
        # Keep summaries valid even if a subsequent project's preparation fails.
        summary = summaries(report)
        (output / "results.json").write_text(json.dumps(report, indent=2) + "\n")
        (output / "summary.md").write_text(summary)
    print(summary)


if __name__ == "__main__":
    main()
