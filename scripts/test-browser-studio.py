#!/usr/bin/env python3
"""Run browser acceptance with a real, temporary MCP/agent companion."""
import argparse
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.request
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('tests', nargs='*')
    args = parser.parse_args()
    host_dll = ROOT / 'tools/XamlG.Studio.Host/bin/Release/net10.0/XamlG.Studio.Host.dll'
    if not host_dll.exists():
        parser.error('Build tools/XamlG.Studio.Host in Release first.')
    dotnet = shutil.which('dotnet')
    with socket.socket() as reservation:
        reservation.bind(('127.0.0.1', 0))
        port = reservation.getsockname()[1]
    origin_url = urlsplit(os.environ.get('PLAYGROUND_URL', 'http://127.0.0.1:8765/'))
    origin = f'{origin_url.scheme}://{origin_url.netloc}'
    token, owner_token = secrets.token_hex(32), secrets.token_hex(32)
    environment = dict(os.environ, XAMLG_STUDIO_TOKEN=token, XAMLG_STUDIO_OWNER_TOKEN=owner_token,
        XAMLG_TEST_MCP_TOKEN=token, XAMLG_TEST_OWNER_TOKEN=owner_token,
        XAMLG_TEST_MCP_URL=f'http://127.0.0.1:{port}', XAMLG_TEST_HOST_DLL=str(host_dll), XAMLG_TEST_DOTNET=dotnet)
    # This host tests local transports only. Individual provider fixtures inject synthetic keys.
    for name in ('OPENAI_API_KEY', 'ANTHROPIC_API_KEY', 'GEMINI_API_KEY', 'GOOGLE_API_KEY'):
        environment.pop(name, None)
    with tempfile.TemporaryFile(mode='w+') as log:
        host = subprocess.Popen([dotnet, str(host_dll), f'--port={port}', f'--origins={origin}'], cwd=ROOT, env=environment, stdout=log, stderr=log)
        try:
            ready = False
            for _ in range(100):
                if host.poll() is not None:
                    break
                try:
                    with urllib.request.urlopen(f'http://127.0.0.1:{port}/health', timeout=1) as response:
                        ready = response.status == 200
                    if ready:
                        break
                except OSError:
                    time.sleep(0.1)
            if not ready:
                log.seek(0)
                raise RuntimeError('Companion did not start: ' + log.read().replace(token, '[MCP test token]').replace(owner_token, '[owner test token]'))
            subprocess.run([shutil.which('npx'), 'playwright', 'test', *args.tests], cwd=ROOT / 'tools/XamlG.Playground', env=environment, check=True)
        finally:
            host.terminate()
            try:
                host.wait(timeout=5)
            except subprocess.TimeoutExpired:
                host.kill()
                host.wait()


if __name__ == '__main__':
    main()
