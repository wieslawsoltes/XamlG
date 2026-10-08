#!/usr/bin/env python3
"""Local static host with cross-origin asset reads for the opaque-origin preview frame."""
import argparse
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import re
from urllib.parse import urlsplit


class Handler(SimpleHTTPRequestHandler):
    def __init__(self, *args, base_path='/', **kwargs):
        self.base_path = base_path
        super().__init__(*args, **kwargs)

    def send_head(self):
        path = urlsplit(self.path).path
        if self.base_path != '/' and path == self.base_path.rstrip('/'):
            self.send_response(301)
            self.send_header('Location', self.base_path)
            self.send_header('Content-Length', '0')
            self.end_headers()
            return None
        if not path.startswith(self.base_path):
            self.send_error(404)
            return None
        return super().send_head()

    def translate_path(self, path):
        return super().translate_path('/' + path[len(self.base_path):])

    def end_headers(self):
        self.send_header('Access-Control-Allow-Origin', '*')
        self.send_header('Cross-Origin-Resource-Policy', 'cross-origin')
        self.send_header('Cache-Control', 'no-store')
        super().end_headers()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', required=True)
    parser.add_argument('--port', type=int, default=8765)
    parser.add_argument('--base-path', default='/')
    arguments = parser.parse_args()
    if not re.fullmatch(r'/(?:[A-Za-z0-9_-]+/)*', arguments.base_path):
        parser.error('--base-path must be / or slash-delimited path segments ending in /.')
    server = ThreadingHTTPServer(('127.0.0.1', arguments.port), partial(Handler, directory=arguments.directory, base_path=arguments.base_path))
    server.serve_forever()
