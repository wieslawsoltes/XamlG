#!/usr/bin/env python3
"""Profile the real catalog/theme C# compiler, including XamlG and every analyzer.

Requires dotnet-trace 9+ and a prepared upstream checkout. Unix hosts also report
child-process CPU time. Profile timings are diagnostic, not XamlX acceptance data.
"""
import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import statistics
import subprocess
import time
from compiler_tools import dotnet_environment
from generator_timings import read_generator_timings

ROOT = Path(__file__).resolve().parents[1]
PROJECTS = ("Avalonia.Themes.Simple", "Avalonia.Themes.Fluent", "ControlCatalog")


def capture(*command):
    return subprocess.check_output(command, cwd=ROOT, text=True).strip()


def run(command, log, environment, cwd=ROOT):
    try:
        import resource
        before = resource.getrusage(resource.RUSAGE_CHILDREN)
    except ImportError:
        before = None
    started = time.perf_counter()
    with log.open("w") as output:
        result = subprocess.run(command, cwd=cwd, env=environment, stdout=output, stderr=subprocess.STDOUT)
    measurement = {"wall_seconds": time.perf_counter() - started, "log": log.name,
                   "sha256": hashlib.sha256(log.read_bytes()).hexdigest()}
    if before is not None:
        after = resource.getrusage(resource.RUSAGE_CHILDREN)
        measurement.update(user_seconds=after.ru_utime - before.ru_utime,
                           system_seconds=after.ru_stime - before.ru_stime)
    if result.returncode:
        raise RuntimeError(f"Command failed ({result.returncode}); see {log}")
    timings = read_generator_timings(log.read_text())
    if timings["all_generators_seconds"] is not None:
        measurement["generator_timing"] = timings
    return measurement


def timing_summary(report):
    lines = ["| Project | XamlG generation | All generators | Complete Csc | Captured C# + analyzers |",
             "| --- | ---: | ---: | ---: | ---: |"]
    for entry in report["projects"]:
        runs = entry["compiler_runs"]
        def median(key):
            values = [run["generator_timing"][key] for run in runs
                      if run.get("generator_timing", {}).get(key) is not None]
            return f"{statistics.median(values):.3f}s" if values else "Unavailable"
        captured = [run["wall_seconds"] for run in entry.get("phase_isolation", {}).get("runs", [])
                    if run["variant"] == "pregenerated"]
        isolated = f"{statistics.median(captured):.3f}s" if captured else "Not measured"
        whole = statistics.median(run["wall_seconds"] for run in runs)
        lines.append(f"| {entry['project']} | {median('xamlg_seconds')} | {median('all_generators_seconds')} | {whole:.3f}s | {isolated} |")
    return "\n".join(lines) + ("\n\nMedians of fresh compiler processes. Generation uses Roslyn's reported elapsed "
        "times from the actual full compiler run. Captured C# is a separate run replacing XamlG with its "
        "generated sources, retaining other generators and analyzers. It includes C# parsing, binding, "
        "analysis, emission and compiler startup. These columns are not additive phases: generated and "
        "ordinary syntax trees can behave differently in Roslyn. Analyzer elapsed times overlap and "
        "must not be subtracted from Csc wall time. Use the full XamlX benchmark for acceptance.\n")


def response_file(build_log, output, project, generator_directory):
    # Keep the compiler's quoting verbatim: response files have different quoting
    # rules from a shell, including comma-containing framework reference paths.
    matches = []
    for line in build_log.read_text().splitlines():
        match = re.search(r'(?:"([^"\n]+[/\\]Roslyn[/\\]bincore[/\\]csc(?:\.dll)?)"|'
                          r'(\S+[/\\]Roslyn[/\\]bincore[/\\]csc(?:\.dll)?)) /noconfig (.*)', line)
        if match:
            matches.append(match)
    if len(matches) != 1:
        raise RuntimeError(f"Expected exactly one Csc invocation in {build_log}; found {len(matches)}")
    match = matches[0]
    compiler = Path(match[1] or match[2])
    arguments = match[3]
    generator = re.search(r'/analyzer:(?:"([^"\n]*XamlG\.Generator\.dll)"|(\S*XamlG\.Generator\.dll))', arguments)
    if generator is None:
        raise RuntimeError(f"The XamlG generator is missing from {build_log}")
    if not generator_directory.exists():
        shutil.copytree(Path(generator[1] or generator[2]).parent, generator_directory)
    # MSBuild can pass the generator's dependencies as separate analyzer items.
    # Pin those locations too: Roslyn's loader may prefer a registered dependency
    # over a DLL next to the generator, defeating an otherwise frozen snapshot.
    assemblies = {path.name: path for path in generator_directory.glob("XamlG.*.dll")}
    def snapshot_analyzer(match):
        path = Path(match[1] or match[2])
        return f'/analyzer:"{assemblies[path.name]}"' if path.name in assemblies else match[0]
    arguments = re.sub(r'/analyzer:(?:"([^"\n]+)"|(\S+))', snapshot_analyzer, arguments)
    replacements = {"out": output / f"{project}.dll", "refout": output / "ref" / f"{project}.dll",
                    "generatedfilesout": output / "generated"}
    replacements["refout"].parent.mkdir(parents=True, exist_ok=True)
    for option, path in replacements.items():
        arguments, count = re.subn(r'/' + option + r':(?:"[^"]*"|\S+)',
                                  lambda _: f'/{option}:"{path}"', arguments)
        if count != 1:
            raise RuntimeError(f"Expected one /{option} argument in {build_log}")
    # Preserve the real assembly basename: XAML resource URIs use that identity.
    response = output / "compiler.rsp"
    response.write_text(arguments + " /reportanalyzer\n")
    return compiler, response


def stack_summary(path):
    trace = json.loads(path.read_text())
    names = [frame["name"] for frame in trace["shared"]["frames"]]
    inclusive, exclusive = Counter(), Counter()
    for profile in trace["profiles"]:
        if profile["type"] != "evented" or profile["unit"] != "milliseconds":
            raise RuntimeError(f"Unexpected speedscope representation in {path}")
        stack = []
        previous = profile["startValue"]
        for event in profile["events"]:
            elapsed = event["at"] - previous
            if stack and elapsed > 0:
                # Speedscope appends a synthetic timing marker to sampled stacks.
                # Attribute leaf time to the actual frame immediately above it.
                leaf = next((names[frame] for frame in reversed(stack)
                             if names[frame] not in ("CPU_TIME", "UNMANAGED_CODE_TIME")), None)
                if leaf is not None:
                    exclusive[leaf] += elapsed
                for name in {names[frame] for frame in stack}:
                    inclusive[name] += elapsed
            previous = event["at"]
            if event["type"] == "O":
                stack.append(event["frame"])
            elif not stack or stack.pop() != event["frame"]:
                raise RuntimeError(f"Unbalanced speedscope stack in {path}")
    def rows(counter, predicate=lambda _: True):
        return [{"frame": name, "milliseconds": round(value, 3)}
                for name, value in counter.most_common() if predicate(name)][:60]
    return {"scope": "Sampled managed thread time, summed across threads; includes waits and GC. "
                     "Inclusive rows overlap. This is not on-CPU time or unprofiled build time.",
            "xamlg_inclusive": rows(inclusive, lambda name: "XamlG." in name),
            "exclusive": rows(exclusive), "inclusive": rows(inclusive)}


def isolate_phases(command, response, directory, project, iterations, environment):
    """Controlled diagnostic ablations; never used as the XamlX acceptance result."""
    original = response.read_text()
    generated = sorted((directory / "generated" / "XamlG.Generator").rglob("*.cs"))
    if not generated:
        raise RuntimeError(f"No captured XamlG sources in {directory}")
    pregenerated, count = re.subn(r'/analyzer:(?:"[^"\n]*XamlG\.Generator\.dll"|\S*XamlG\.Generator\.dll)',
                                "", original)
    if count != 1:
        raise RuntimeError(f"Expected one XamlG analyzer in {response}")
    pregenerated += " " + " ".join(f'"{path}"' for path in generated)
    no_trim, trim_count = re.subn(r'/analyzer:(?:"[^"\n]*ILLink\.RoslynAnalyzer\.dll"|\S*ILLink\.RoslynAnalyzer\.dll)',
                                 "", pregenerated)
    variants = {"full": original, "pregenerated": pregenerated}
    if trim_count:
        variants["pregenerated-no-trim"] = no_trim
    variants["pregenerated-no-analyzers"] = pregenerated + " /skipanalyzers+"
    result = {"scope": "Diagnostic phase isolation, not acceptance timings. Pregenerated runs replace only "
                       "the XamlG generator/analyzer with its captured C#; other generators remain enabled. "
                       "No-trim removes ILLink analysis; no-analyzers skips analysis but retains other generators. "
                       "These separately compiled syntax trees can behave differently in Roslyn; differences "
                       "are diagnostic and are not additive phase costs.",
              "generated_sha256": {str(path.relative_to(directory)): hashlib.sha256(path.read_bytes()).hexdigest()
                                   for path in generated}, "runs": []}
    responses = {}
    for name, arguments in variants.items():
        output = directory / "phases" / name
        (output / "ref").mkdir(parents=True)
        for option, path in {"out": output / f"{project}.dll", "refout": output / "ref" / f"{project}.dll"}.items():
            arguments, count = re.subn(r'/' + option + r':(?:"[^"]*"|\S+)', lambda _: f'/{option}:"{path}"', arguments)
            if count != 1:
                raise RuntimeError(f"Expected one /{option} in {response}")
        # Do not overwrite the snapshot consumed by pregenerated runs.
        arguments = re.sub(r'/generatedfilesout:(?:"[^"]*"|\S+)', "", arguments)
        responses[name] = output / "compiler.rsp"
        responses[name].write_text(arguments)
    names = list(variants)
    for iteration in range(iterations):
        order = names[iteration % len(names):] + names[:iteration % len(names)]
        for name in order:
            response_path = responses[name]
            measurement = run(command[:-1] + ["@" + str(response_path)],
                              response_path.parent / f"compiler-{iteration + 1}.log", environment, ROOT / "samples" / project)
            measurement.update(variant=name, iteration=iteration + 1)
            result["runs"].append(measurement)
            print(f"{project} phase {name} #{iteration + 1}: {measurement['wall_seconds']:.3f}s", flush=True)
            (directory / "phases.json").write_text(json.dumps(result, indent=2) + "\n")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--dotnet-trace", default="dotnet-trace")
    parser.add_argument("--iterations", type=int, default=3)
    parser.add_argument("--projects", nargs="+", choices=PROJECTS, default=PROJECTS)
    parser.add_argument("--skip-prepare", action="store_true")
    parser.add_argument("--isolate-phases", action="store_true",
                        help="Also compare generated-source and analyzer ablations for diagnosis, outside acceptance timings.")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/controlcatalog-profile")
    args = parser.parse_args()
    if args.iterations < 1:
        parser.error("Use at least one unprofiled compiler run.")
    dotnet, environment = dotnet_environment(args.dotnet)
    tracer = shutil.which(args.dotnet_trace)
    if not tracer:
        parser.error("Both dotnet and dotnet-trace must be available.")
    dotnet, tracer = str(Path(dotnet).resolve()), str(Path(tracer).resolve())
    output = args.output.resolve()
    if output.exists() and any(output.iterdir()):
        parser.error("Use an empty output directory so old generated files cannot contaminate the profile.")
    output.mkdir(parents=True, exist_ok=True)
    report = {"commit": capture("git", "rev-parse", "HEAD"),
              "tracked_changes": capture("git", "status", "--porcelain", "--untracked-files=no"),
              "sdk": capture(dotnet, "--version"), "trace_tool": capture(tracer, "--version"),
              "platform": platform.platform(), "processor_count_override": os.getenv("DOTNET_PROCESSOR_COUNT"),
              "dotnet_root": environment["DOTNET_ROOT"],
              "method": "Fresh Csc processes using the actual project command, with every generator and analyzer. "
                        "Unprofiled runs exclude MSBuild; trace runs separately collect sampled managed stacks, GC and allocation ticks. "
                        "Use benchmark-controlcatalog.py for the full added-XAML-cost comparison with XamlX.",
              "projects": []}
    build = [dotnet, "build", "-c", "Release", "-p:AvsSkipBuildingLegacyTargetFrameworks=True",
             "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-m:1", "-nologo"]
    if not args.skip_prepare:
        print("Preparing dependencies (not profiled)", flush=True)
        run(build + ["samples/ControlCatalog/ControlCatalog.csproj"], output / "prepare.log", environment)
    for project in args.projects:
        directory = output / project
        directory.mkdir(parents=True, exist_ok=True)
        print(f"Capturing {project} compiler command", flush=True)
        build_log = directory / "build.log"
        build_measurement = run(build + [f"samples/{project}/{project}.csproj", "--no-restore", "-t:Rebuild",
                                        "-p:BuildProjectReferences=false", "-p:EmitCompilerGeneratedFiles=true",
                                        f"-p:CompilerGeneratedFilesOutputPath={directory / 'generated'}",
                                        "-v:normal", "-clp:PerformanceSummary;Summary"], build_log, environment)
        compiler, response = response_file(build_log, directory, project, output / "generator")
        report["generator_assemblies"] = {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                                          for path in sorted((output / "generator").glob("*.dll"))}
        command = ([dotnet, "exec", str(compiler)] if compiler.suffix == ".dll" else [str(compiler)]) + ["/noconfig", "@" + str(response)]
        entry = {"project": project, "build": build_measurement, "compiler_runs": []}
        for iteration in range(args.iterations):
            measurement = run(command, directory / f"compiler-{iteration + 1}.log", environment, ROOT / "samples" / project)
            entry["compiler_runs"].append(measurement)
            print(f"{project} Csc #{iteration + 1}: {measurement['wall_seconds']:.3f}s", flush=True)
        if args.isolate_phases:
            entry["phase_isolation"] = isolate_phases(command, response, directory, project, args.iterations, environment)
        print(f"Tracing {project} (timing includes profiler overhead)", flush=True)
        entry["trace"] = run([tracer, "collect", "--profile", "dotnet-sampled-thread-time,gc-verbose",
                              "--format", "Speedscope", "--output", str(directory / "compiler.nettrace"),
                              "--show-child-io", "--"] + command, directory / "trace.log", environment, ROOT / "samples" / project)
        (directory / "stacks.json").write_text(json.dumps(stack_summary(directory / "compiler.speedscope.json"), indent=2) + "\n")
        generated = sorted((directory / "generated").rglob("*.cs"))
        entry.update(generated_files=len(generated), generated_bytes=sum(path.stat().st_size for path in generated))
        report["projects"].append(entry)
        (output / "results.json").write_text(json.dumps(report, indent=2) + "\n")
        (output / "timings.md").write_text(timing_summary(report))


if __name__ == "__main__":
    main()
