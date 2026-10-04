#!/usr/bin/env python3
"""Loopback-only test proxy: fail one summary, forward all other calls to the real model."""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import threading
import time
import urllib.error
import urllib.request
from urllib.parse import urlparse


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--upstream-url", required=True)
    parser.add_argument("--port", type=int, default=18542)
    parser.add_argument("--events", type=Path, required=True)
    args = parser.parse_args()
    if urlparse(args.upstream_url).scheme not in ("http", "https"):
        parser.error("upstream URL must use HTTP or HTTPS")
    lock = threading.Lock()
    state = {"injected": False, "requests": 0}

    def record(value):
        with lock:
            with args.events.open("a") as output:
                output.write(json.dumps({"time": time.time(), **value}) + "\n")

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            if self.path != "/_test/status":
                self.send_error(404)
                return
            with lock:
                body = json.dumps(state).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_POST(self):
            body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
            try:
                payload = json.loads(body)
            except ValueError:
                self.send_error(400, "Expected JSON")
                return
            messages = payload.get("messages", [])
            summary = not payload.get("tools") and any(
                message.get("role") in ("system", "developer")
                and isinstance(message.get("content"), str)
                and "Create a factual checkpoint of the ongoing user task" in message["content"]
                for message in messages)
            with lock:
                state["requests"] += 1
                index = state["requests"]
                inject = summary and not state["injected"]
                if inject:
                    state["injected"] = True
            event = {"request": index, "summary": summary,
                     "model": payload.get("model"), "injected": inject}
            if inject:
                error = {"error": {"code": "e2e_summary_failure",
                                   "message": "Injected one-shot summary stream failure"}}
                data = ("data: " + json.dumps(error) + "\n\n").encode()
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
                record({**event, "status": 200})
                return
            headers = {key: value for key, value in self.headers.items()
                       if key.lower() not in ("host", "content-length", "connection", "accept-encoding")}
            headers["Accept-Encoding"] = "identity"
            request = urllib.request.Request(args.upstream_url.rstrip("/") + self.path,
                                             body, headers, method="POST")
            try:
                response = urllib.request.urlopen(request, timeout=240)
            except urllib.error.HTTPError as error:
                response = error
            except urllib.error.URLError:
                self.send_error(502, "Real upstream connection failed")
                record({**event, "status": 502})
                return
            with response:
                self.send_response(response.status)
                self.send_header("Content-Type", response.headers.get("Content-Type", "application/json"))
                self.send_header("Connection", "close")
                self.end_headers()
                try:
                    while True:
                        chunk = response.read1(65536)
                        if not chunk:
                            break
                        self.wfile.write(chunk)
                        self.wfile.flush()
                except (BrokenPipeError, ConnectionResetError):
                    record({**event, "status": response.status, "client_disconnected": True})
                    return
                record({**event, "status": response.status})

    print(f"Summary fault proxy listening on 127.0.0.1:{args.port}", flush=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
