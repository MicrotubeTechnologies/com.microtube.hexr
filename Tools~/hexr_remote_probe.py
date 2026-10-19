#!/usr/bin/env python3
"""
HexR Remote probe: talk to a HexR headset from a computer on the same Wi-Fi.

Standard library only. It speaks the same protocol as the HexR Tester's Headsets tab
(Documentation~/RemoteProtocol.md), so it is the quickest way to tell "the headset is not
announcing" from "the phone is not listening" at an event, and how this package is tested
without a phone.

    python hexr_remote_probe.py discover                 # list headsets announcing on the LAN
    python hexr_remote_probe.py status 192.168.1.12      # print status lines for a few seconds
    python hexr_remote_probe.py glove 192.168.1.12 connect left
    python hexr_remote_probe.py send 192.168.1.12 '{"type":"release"}'
    python hexr_remote_probe.py stream 192.168.1.12 --seconds 15 --out view.h264
        # asks the headset to share its view (accept the prompt in the headset), records the
        # H.264 it sends, checks its framing, then stops sharing. VLC plays the .h264 file.
        # When the app shares its sound too, that goes in view.aac beside it.
"""

import argparse
import json
import os
import socket
import struct
import sys
import threading
import time

DISCOVERY_PORT = 47800
CONTROL_PORT = 47801


def discover(seconds):
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind(("", DISCOVERY_PORT))
    sock.settimeout(0.5)
    seen = {}
    end = time.time() + seconds
    print(f"Listening for HexR headsets on UDP {DISCOVERY_PORT} for {seconds} s...")
    while time.time() < end:
        try:
            data, (host, _) = sock.recvfrom(4096)
        except socket.timeout:
            continue
        try:
            msg = json.loads(data.decode("utf-8"))
        except ValueError:
            continue
        if msg.get("type") != "announce":
            continue
        key = (msg.get("id"), msg.get("startedAt"))
        if key not in seen:
            seen[key] = msg
            print(f"  {host:15}  {msg.get('name')} ({msg.get('platform')}, {msg.get('model')})  "
                  f"app={msg.get('app')!r}  control={msg.get('control')}  video={msg.get('video')}  "
                  f"hexr={msg.get('hexrVersion')}")
    if not seen:
        print("Nothing heard. Same Wi-Fi? Some venue networks block devices from seeing each other.")
    return seen


class Control:
    def __init__(self, host, port):
        self.sock = socket.create_connection((host, port), timeout=5)
        self.sock.settimeout(0.5)
        self.buf = b""
        self.send({"type": "hello", "protocol": 1, "client": "hexr_remote_probe"})

    def send(self, obj):
        line = obj if isinstance(obj, str) else json.dumps(obj, separators=(",", ":"))
        self.sock.sendall(line.encode("utf-8") + b"\n")

    def lines(self, seconds):
        end = time.time() + seconds
        last_ping = time.time()
        while time.time() < end:
            if time.time() - last_ping > 5:
                self.send({"type": "ping"})
                last_ping = time.time()
            try:
                chunk = self.sock.recv(65536)
            except socket.timeout:
                continue
            if not chunk:
                raise ConnectionError("the headset closed the connection")
            self.buf += chunk
            while b"\n" in self.buf:
                raw, self.buf = self.buf.split(b"\n", 1)
                if raw.strip():
                    yield json.loads(raw.decode("utf-8"))


def describe(status):
    gloves = "  ".join(
        f"{g['hand']}={g['state']}"
        + (f" {g['batteryPct']}%" if g.get("batteryPct", -1) >= 0 else "")
        + (f" [{','.join(str(p / 10) for p in g['pressureDeciKPa'][:6])}] kPa" if g.get("pressureDeciKPa") else "")
        + (f" ({g['message']})" if g.get("message") else "")
        for g in status.get("gloves", []))
    s = status.get("stream", {})
    stream = f"view={s.get('state')}" + (f" {s.get('width')}x{s.get('height')} :{s.get('port')} watching={s.get('viewers')}"
                                         if s.get("state") == "live" else "") + (f" ({s['message']})" if s.get("message") else "")
    a = s.get("audio")
    if a:
        stream += f"  sound={a.get('state')}" + (f" :{a.get('port')}" if a.get("state") == "live" else "") + \
            (f" ({a['message']})" if a.get("message") else "")
    paused = "  PAUSED" if status.get("paused") else ""
    scene = status.get("scene", {}).get("name", "")
    return f"{status.get('app')} / {scene}  {gloves}  {stream}  phones={status.get('controllers')}{paused}"


def print_status(host, port, seconds):
    c = Control(host, port)
    last = None
    for msg in c.lines(seconds):
        if msg.get("type") == "status":
            text = describe(msg)
            if text != last:
                print(time.strftime("%H:%M:%S"), text)
                last = text
        else:
            print(time.strftime("%H:%M:%S"), msg)


def send_and_watch(host, port, request, seconds):
    c = Control(host, port)
    c.send(request)
    print("sent", request if isinstance(request, str) else json.dumps(request))
    last = None
    for msg in c.lines(seconds):
        if msg.get("type") == "status":
            text = describe(msg)
            if text != last:
                print(time.strftime("%H:%M:%S"), text)
                last = text
        else:
            print(time.strftime("%H:%M:%S"), msg)


def read_exact(sock, n):
    data = b""
    while len(data) < n:
        chunk = sock.recv(n - len(data))
        if not chunk:
            raise ConnectionError("video connection closed")
        data += chunk
    return data


def nal_types(payload):
    """NAL unit types in an Annex-B payload (7 SPS, 8 PPS, 5 IDR, 1 non-IDR)."""
    types = []
    i = 0
    while True:
        j = payload.find(b"\x00\x00\x01", i)
        if j < 0 or j + 3 >= len(payload):
            break
        types.append(payload[j + 3] & 0x1F)
        i = j + 3
    return types


def record_video(host, port, seconds, out_path):
    sock = socket.create_connection((host, port), timeout=10)
    header = read_exact(sock, 9)
    magic, width, height, codec = header[:4], *struct.unpack(">HHB", header[4:])
    if magic != b"HXV1":
        raise ValueError(f"not a HexR video stream: {header!r}")
    print(f"Video header: {width}x{height}, codec {codec}")
    packets = keys = configs = total = 0
    first_key = None
    started = time.time()
    with open(out_path, "wb") as out:
        while time.time() - started < seconds:
            length, flags, pts = struct.unpack(">IBQ", read_exact(sock, 13))
            payload = read_exact(sock, length)
            if packets == 0 and not (flags & 3):
                raise ValueError("first packet is neither config nor keyframe; a decoder could not start here")
            packets += 1
            total += length
            if flags & 1:
                configs += 1
            if flags & 2:
                keys += 1
                if first_key is None:
                    first_key = time.time() - started
                    print(f"First keyframe after {first_key:.2f} s, NAL types {nal_types(payload)}")
            out.write(payload)
    elapsed = time.time() - started
    sock.close()
    print(f"{packets} packets in {elapsed:.1f} s ({packets / elapsed:.1f}/s), {keys} keyframes, {configs} config, "
          f"{total * 8 / elapsed / 1000:.0f} kbit/s. Saved {out_path}")
    return packets, keys


def adts(asc, length):
    """The 7-byte ADTS header that makes a raw AAC frame of `length` bytes playable on its own."""
    profile = (asc[0] >> 3) - 1
    rate = ((asc[0] & 7) << 1) | (asc[1] >> 7)
    channels = (asc[1] >> 3) & 0xF
    n = length + 7
    return bytes([0xFF, 0xF1, (profile << 6) | (rate << 2) | (channels >> 2),
                  ((channels & 3) << 6) | (n >> 11), (n >> 3) & 0xFF, ((n & 7) << 5) | 0x1F, 0xFC])


def record_audio(host, port, seconds, out_path, result):
    """The sound, as .aac (ADTS) that VLC plays. `result` gets what was seen, for the summary."""
    sock = socket.create_connection((host, port), timeout=10)
    header = read_exact(sock, 10)
    magic, rate, channels, codec = header[:4], *struct.unpack(">IBB", header[4:])
    if magic != b"HXA1":
        raise ValueError(f"not a HexR sound stream: {header!r}")
    print(f"Sound header: {rate} Hz, {channels} channels, codec {codec}")
    asc = None
    frames = total = 0
    first = last = None
    gaps = 0
    started = time.time()
    with open(out_path, "wb") as out:
        while time.time() - started < seconds:
            length, flags, pts = struct.unpack(">IBQ", read_exact(sock, 13))
            payload = read_exact(sock, length)
            if flags & 1:
                asc = payload
                print(f"AudioSpecificConfig {payload.hex()}")
                continue
            if asc is None:
                raise ValueError("a sound frame came before the AudioSpecificConfig")
            # 1024 samples a frame: anything much further on is a gap in what played.
            if last is not None and pts - last > 1.5 * 1024 * 1e6 / rate:
                gaps += 1
            first = pts if first is None else first
            last = pts
            frames += 1
            total += length
            out.write(adts(asc, length) + payload)
    sock.close()
    result.update(frames=frames, first=first, last=last, gaps=gaps, kbps=total * 8 / max(1e-3, seconds) / 1000)


def stream(host, port, seconds, out_path):
    c = Control(host, port)
    c.send({"type": "stream", "action": "start"})
    print("Asked the headset to share its view. Accept the prompt in the headset...")
    live = None
    for msg in c.lines(60):
        if msg.get("type") == "error":
            print("headset said:", msg.get("message"))
            return 1
        if msg.get("type") != "status":
            continue
        s = msg.get("stream", {})
        if s.get("state") == "live" and s.get("port"):
            live = s
            break
        if s.get("state") in ("denied", "error", "unsupported"):
            print("view sharing:", s.get("state"), s.get("message"))
            return 1
    if not live:
        print("The headset never started sharing (prompt not accepted within 60 s?)")
        return 1
    print(f"Live on port {live['port']} at {live['width']}x{live['height']}")
    # The sound starts just after the view does.
    sound = live.get("audio") or {}
    for msg in c.lines(1.5):
        if msg.get("type") == "status":
            sound = msg.get("stream", {}).get("audio") or {}
            if sound.get("state") != "off":
                break
    heard, listener = {}, None
    if sound.get("state") == "live" and sound.get("port"):
        audio_path = os.path.splitext(out_path)[0] + ".aac"
        listener = threading.Thread(target=record_audio, daemon=True,
                                    args=(host, sound["port"], seconds, audio_path, heard))
        listener.start()
    else:
        print("No sound:", sound.get("state", "this headset does not send it"), sound.get("message", ""))
    try:
        record_video(host, live["port"], seconds, out_path)
        if listener:
            listener.join(seconds + 5)
            if heard:
                span = (heard["last"] - heard["first"]) / 1e6 if heard["frames"] > 1 else 0
                print(f"{heard['frames']} sound frames over {span:.1f} s, {heard['gaps']} gaps, "
                      f"{heard['kbps']:.0f} kbit/s. Saved {audio_path}")
    finally:
        c.send({"type": "stream", "action": "stop"})
        for msg in c.lines(2):
            if msg.get("type") == "status" and msg.get("stream", {}).get("state") != "live":
                print("Stopped sharing:", msg["stream"].get("state"))
                break
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    d = sub.add_parser("discover")
    d.add_argument("--seconds", type=float, default=6)
    for name in ("status", "glove", "send", "stream", "video"):
        p = sub.add_parser(name)
        p.add_argument("host")
        p.add_argument("--port", type=int, default=CONTROL_PORT)
        p.add_argument("--seconds", type=float, default=8)
        if name == "glove":
            p.add_argument("action", choices=["connect", "disconnect"])
            p.add_argument("hand", choices=["left", "right"])
        if name == "send":
            p.add_argument("json")
        if name in ("stream", "video"):
            p.add_argument("--out", default="hexr_view.h264")
    a = ap.parse_args()

    if a.cmd == "discover":
        return 0 if discover(a.seconds) else 1
    if a.cmd == "status":
        print_status(a.host, a.port, a.seconds)
    elif a.cmd == "glove":
        send_and_watch(a.host, a.port, {"type": "glove", "action": a.action, "hand": a.hand}, a.seconds)
    elif a.cmd == "send":
        send_and_watch(a.host, a.port, a.json, a.seconds)
    elif a.cmd == "stream":
        return stream(a.host, a.port, a.seconds, a.out)
    elif a.cmd == "video":
        record_video(a.host, a.port, a.seconds, a.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
