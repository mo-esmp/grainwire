"""The chess rules service: POST /decide for the GrainWire bridge grain, GET /health for checks.

    python server.py [--host 127.0.0.1] [--port 5310]

It holds no state, so any number of copies can run behind a load balancer and any one can be restarted.
"""

from __future__ import annotations

import argparse
import json
import logging
import sys
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from chess_rules import decide

log = logging.getLogger("chess-rules")


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_POST(self) -> None:
        if self.path != "/decide":
            self.reply(404, {"error": "not found"})
            return
        if "Content-Length" not in self.headers:
            # http.server cannot read chunked bodies; the bridge always sends a Content-Length.
            self.reply(411, {"error": "a Content-Length is required"})
            return
        try:
            request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            decision = decide(request)
        except Exception as error:  # noqa: BLE001 - any failure is an error for the grain, which undoes the operation
            log.error("decide failed: %s\n%s", error, traceback.format_exc())
            self.reply(500, {"error": str(error)})
            return
        self.reply(200, decision)

    def do_GET(self) -> None:
        if self.path == "/health":
            self.reply(200, {"status": "ok"})
        else:
            self.reply(404, {"error": "not found"})

    def reply(self, status: int, body: dict) -> None:
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, format: str, *args) -> None:  # noqa: A002 - the base class's name
        log.debug(format, *args)


class Server(ThreadingHTTPServer):
    daemon_threads = True

    def handle_error(self, request, client_address) -> None:
        # The bridge keeps connections open between calls; when it stops, they are reset. That is not an error.
        if not isinstance(sys.exc_info()[1], ConnectionError):
            super().handle_error(request, client_address)


def main() -> None:
    parser = argparse.ArgumentParser(description="Chess rules for the GrainWire bridge sample.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5310)
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    server = Server((args.host, args.port), Handler)
    log.info("Chess rules listening on http://%s:%d", args.host, args.port)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
