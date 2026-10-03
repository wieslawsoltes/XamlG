#!/usr/bin/env python3
"""Local static host with cross-origin asset reads for the opaque-origin preview frame."""
import argparse
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer


class Handler(SimpleHTTPRequestHandler):
    def end_headers(self):
        self.send_header('Access-Control-Allow-Origin', '*')
        self.send_header('Cross-Origin-Resource-Policy', 'cross-origin')
        self.send_header('Cache-Control', 'no-store')
        super().end_headers()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', required=True)
    parser.add_argument('--port', type=int, default=8765)
    arguments = parser.parse_args()
    server = ThreadingHTTPServer(('127.0.0.1', arguments.port), partial(Handler, directory=arguments.directory))
    server.serve_forever()
