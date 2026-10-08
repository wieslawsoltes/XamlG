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
import subprocess
import time
from compiler_tools import dotnet_environment

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
    return measurement


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
    arguments = arguments[:generator.start()] + f'/analyzer:"{generator_directory / "XamlG.Generator.dll"}"' + arguments[generator.end():]
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--dotnet-trace", default="dotnet-trace")
    parser.add_argument("--iterations", type=int, default=3)
    parser.add_argument("--projects", nargs="+", choices=PROJECTS, default=PROJECTS)
    parser.add_argument("--skip-prepare", action="store_true")
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
        print(f"Tracing {project} (timing includes profiler overhead)", flush=True)
        entry["trace"] = run([tracer, "collect", "--profile", "dotnet-sampled-thread-time,gc-verbose",
                              "--format", "Speedscope", "--output", str(directory / "compiler.nettrace"),
                              "--show-child-io", "--"] + command, directory / "trace.log", environment, ROOT / "samples" / project)
        (directory / "stacks.json").write_text(json.dumps(stack_summary(directory / "compiler.speedscope.json"), indent=2) + "\n")
        generated = sorted((directory / "generated").rglob("*.cs"))
        entry.update(generated_files=len(generated), generated_bytes=sum(path.stat().st_size for path in generated))
        report["projects"].append(entry)
        (output / "results.json").write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
