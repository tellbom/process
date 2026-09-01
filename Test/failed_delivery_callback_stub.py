import argparse
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class State:
    mode = "success"
    count = 0
    lock = threading.Lock()


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path.startswith("/mode/"):
            mode = self.path.removeprefix("/mode/")
            if mode not in {"success", "unauthorized", "server-error", "timeout"}:
                self._send(400, {"error": "invalid mode"})
                return
            with State.lock:
                State.mode = mode
                State.count = 0
            self._send(200, {"mode": mode})
            return

        if self.path != "/callback":
            self._send(404, {"error": "not found"})
            return

        length = int(self.headers.get("Content-Length", "0"))
        if length:
            self.rfile.read(length)
        with State.lock:
            State.count += 1
            mode = State.mode
        if mode == "timeout":
            time.sleep(5)
            self._send(200, {"mode": mode})
        elif mode == "unauthorized":
            self._send(401, {"mode": mode})
        elif mode == "server-error":
            self._send(500, {"mode": mode})
        else:
            self._send(200, {"mode": mode})

    def do_GET(self):
        if self.path != "/state":
            self._send(404, {"error": "not found"})
            return
        with State.lock:
            body = {"mode": State.mode, "count": State.count}
        self._send(200, body)

    def log_message(self, *_):
        return

    def _send(self, status, body):
        payload = json.dumps(body).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        try:
            self.wfile.write(payload)
        except BrokenPipeError:
            pass


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=18081)
    args = parser.parse_args()
    ThreadingHTTPServer(("0.0.0.0", args.port), Handler).serve_forever()
