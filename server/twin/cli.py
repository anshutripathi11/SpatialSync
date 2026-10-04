"""
Offline batch mode: process a folder the app already saved (pins.json + photos) without the server.

    python -m twin.cli walls  --plan plan.png                         # preview wall extraction + scale
    python -m twin.cli serve  --plan plan.png --entrance-x 398 --entrance-y 575 [--px-per-m 40]
                                                                      # load plan + start the live server
    python -m twin.cli build  --plan plan.png --pins <folder>/pins.json --out twin_data [--mock]
"""
from __future__ import annotations

import argparse
import json
import os
import shutil

import cv2

from .pipeline import load_plan, process_capture, register_capture
from .state import TwinState
from .walls import extract_walls, estimate_pixels_per_meter, read_image


def cmd_walls(a):
    img = read_image(a.plan)
    walls = extract_walls(img, a.min_thickness, a.min_length)
    est, votes = estimate_pixels_per_meter(walls)
    vis = img.copy()
    for w in walls:
        cv2.line(vis, tuple(map(int, w.a)), tuple(map(int, w.b)), (0, 0, 255), 3)
        mid = (int((w.a[0] + w.b[0]) / 2) + 4, int((w.a[1] + w.b[1]) / 2) - 4)
        cv2.putText(vis, w.id, mid, cv2.FONT_HERSHEY_SIMPLEX, 0.4, (200, 0, 0), 1)
    out = os.path.splitext(a.plan)[0] + "_walls.png"
    cv2.imencode('.png', vis)[1].tofile(out)
    print(f"{len(walls)} walls -> {out}")
    print(f"scale estimate from doorways: {est and round(est, 2)} px/m ({votes} doorways agree)")


def cmd_build(a):
    os.makedirs(os.path.join(a.out, "photos"), exist_ok=True)
    state = TwinState()
    info = load_plan(state, a.plan, a.px_per_m, (a.entrance_x, a.entrance_y), a.entrance_heading,
                     a.min_thickness, a.min_length)
    shutil.copy(a.plan, os.path.join(a.out, os.path.basename(a.plan)))
    print("plan:", info)

    from .local_vision import make_observer
    observer = make_observer("mock" if a.mock else a.observer)
    print("observer:", type(observer).__name__)

    if a.pins:
        folder = os.path.dirname(os.path.abspath(a.pins))
        with open(a.pins) as f:
            pins = json.load(f)["pins"]
        for m in pins:
            src = os.path.join(folder, m["photoFileName"])
            dst = os.path.join(a.out, "photos", os.path.basename(src))
            shutil.copy(src, dst)
            ps = register_capture(state, dst, m)
            changed = process_capture(state, dst, ps, observer)
            print(f"{ps.id}: saw {ps.walls_seen} -> changed {changed} {ps.error}")
    state.save(os.path.join(a.out, "state.json"))
    print("wrote", os.path.join(a.out, "state.json"))


def _lan_ip() -> str:
    import socket
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sk:
            sk.connect(("10.255.255.255", 1))   # no packet is sent; just picks the outgoing interface
            return sk.getsockname()[0]
    except OSError:
        return "127.0.0.1"


def cmd_serve(a):
    if a.observer:
        os.environ["FLOORTWIN_OBSERVER"] = a.observer
    os.environ.setdefault("FLOORTWIN_DATA", a.out)
    import uvicorn
    from . import server   # creates the state + observer + worker
    if a.plan and (a.reset or not server.state.walls):
        try:
            info = load_plan(server.state, a.plan, a.px_per_m, (a.entrance_x, a.entrance_y), a.entrance_heading,
                             a.min_thickness, a.min_length)
        except ValueError as e:
            raise SystemExit(f"\n{e}\n")
        shutil.copy(a.plan, os.path.join(server.DATA, os.path.basename(a.plan)))
        server.state.save(server.STATE_PATH)
        print("plan:", info)
    ip = _lan_ip()
    print(f"\n  Phone app  -> Server Url = http://{ip}:{a.port}")
    print(f"  Viewer     -> Server Url = http://127.0.0.1:{a.port}\n")
    uvicorn.run(server.app, host="0.0.0.0", port=a.port)


def main():
    p = argparse.ArgumentParser(prog="twin")
    sub = p.add_subparsers(dest="cmd", required=True)
    for name in ("walls", "build", "serve"):
        s = sub.add_parser(name)
        s.add_argument("--plan", required=True)
        s.add_argument("--min-thickness", type=int, default=5, help="thinnest wall stroke in px")
        s.add_argument("--min-length", type=int, default=20, help="shortest wall in px")
        if name == "serve":
            s.add_argument("--port", type=int, default=8000)
            s.add_argument("--reset", action="store_true", help="re-load the plan even if a session exists (wipes walls)")
        if name in ("build", "serve"):
            s.add_argument("--out", default="twin_data", help="session folder")
        if name == "build":
            s.add_argument("--pins", help="pins.json written by the app")
        if name in ("build", "serve"):
            s.add_argument("--px-per-m", type=float)
            s.add_argument("--entrance-x", type=float, default=0.0)
            s.add_argument("--entrance-y", type=float, default=0.0)
            s.add_argument("--entrance-heading", type=float, default=0.0)
            s.add_argument("--mock", action="store_true", help="fake observations (plumbing test)")
            s.add_argument("--observer", choices=["local", "hybrid", "claude", "mock"],
                           help="default: hybrid if ANTHROPIC_API_KEY is set, else local")
    a = p.parse_args()
    if a.cmd == "serve" and a.mock:
        a.observer = "mock"
    {"walls": cmd_walls, "build": cmd_build, "serve": cmd_serve}[a.cmd](a)


if __name__ == "__main__":
    main()
