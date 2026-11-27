#!/usr/bin/env python3
"""A stand-in for the desktop download manager's native messaging host.

It speaks the same protocol (see PROTOCOL.md), accepts every download, and appends each
job to received.jsonl next to this file so you can see what the extension sent.
Chrome starts one copy of this program for every message it sends.
"""
import json
import os
import struct
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
LOG = os.environ.get("ODM_LOG", os.path.join(HERE, "received.jsonl"))
VERSION = "test-host 0.1.0"


def read_message():
    raw_len = sys.stdin.buffer.read(4)
    if len(raw_len) < 4:
        return None
    (length,) = struct.unpack("<I", raw_len)  # native byte order; little-endian on Windows and x86 Linux
    return json.loads(sys.stdin.buffer.read(length).decode("utf-8"))


def write_message(obj):
    data = json.dumps(obj).encode("utf-8")
    sys.stdout.buffer.write(struct.pack("<I", len(data)))
    sys.stdout.buffer.write(data)
    sys.stdout.buffer.flush()


def handle(msg):
    kind = msg.get("type")
    if kind == "ping":
        return {"ok": True, "version": VERSION}
    if kind == "probe":
        # The real app runs yt-dlp here and lists the formats it found.
        return {
            "ok": True,
            "title": "Test video",
            "formats": [
                {"id": "137+140", "label": "1080p", "ext": "mp4", "size": 104857600},
                {"id": "136+140", "label": "720p", "ext": "mp4", "size": 52428800},
                {"id": "140", "label": "Audio only", "ext": "m4a", "size": 4194304},
            ],
        }
    if kind == "download":
        if os.environ.get("ODM_REFUSE"):  # lets the tests check that the browser keeps the download
            return {"ok": False, "error": "Refused by test host"}
        if not str(msg.get("url", "")).startswith(("http://", "https://")):
            return {"ok": False, "error": "Only http and https links are supported"}
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(json.dumps({"receivedAt": time.time(), **msg}) + "\n")
        return {"ok": True, "jobId": f"job-{int(time.time() * 1000)}"}
    return {"ok": False, "error": f"Unknown message type: {kind}"}


def main():
    while True:
        msg = read_message()
        if msg is None:
            return
        try:
            write_message(handle(msg))
        except Exception as e:  # never leave the browser waiting
            write_message({"ok": False, "error": str(e)})


if __name__ == "__main__":
    main()
