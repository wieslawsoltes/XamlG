"""Keep compiler subprocesses on the explicitly selected dotnet installation."""
import os
from pathlib import Path
import platform
import shutil


def dotnet_environment(executable):
    found = shutil.which(executable)
    if found is None:
        raise FileNotFoundError(f"dotnet executable not found: {executable}")
    dotnet = Path(found).resolve()
    environment = dict(os.environ)
    environment["DOTNET_ROOT"] = str(dotnet.parent)
    architecture = {"aarch64": "ARM64", "arm64": "ARM64", "x86_64": "X64",
                    "amd64": "X64", "i386": "X86", "i686": "X86"}.get(platform.machine().lower())
    if architecture is not None:
        environment["DOTNET_ROOT_" + architecture] = str(dotnet.parent)
    environment["DOTNET_HOST_PATH"] = str(dotnet)
    return str(dotnet), environment
