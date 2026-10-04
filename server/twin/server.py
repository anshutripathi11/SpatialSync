"""
Live twin server. The phone uploads captures; a worker thread runs the model on each one in order and
merges the result; the Unity viewer polls /state and rebuilds only walls whose `rev` changed.

    uvicorn twin.server:app --host 0.0.0.0 --port 8000
      FLOORTWIN_OBSERVER=local   open-source models only (default when no ANTHROPIC_API_KEY)
      FLOORTWIN_OBSERVER=hybrid  local first, Claude for unsure walls (default when a key is set)
      FLOORTWIN_OBSERVER=claude  Claude for everything
      FLOORTWIN_OBSERVER=mock    fake observations, for testing the plumbing
"""
from __future__ import annotations

import json
import os
import queue
import threading
import uuid

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import FileResponse, JSONResponse

from .pipeline import load_plan, process_capture, register_capture
from .state import TwinState
from .local_vision import make_observer

DATA = os.path.abspath(os.environ.get("FLOORTWIN_DATA", "twin_data"))
PHOTOS = os.path.join(DATA, "photos")
STATE_PATH = os.path.join(DATA, "state.json")
os.makedirs(PHOTOS, exist_ok=True)

state = TwinState.load(STATE_PATH) if os.path.exists(STATE_PATH) else TwinState()
observer = make_observer()   # FLOORTWIN_OBSERVER = local | hybrid | claude | mock
print(f"[twin] vision observer: {type(observer).__name__}")
jobs: "queue.Queue[str]" = queue.Queue()
app = FastAPI(title="FloorTwin")


def _worker():
    while True:
        pid = jobs.get()
        ps = state.photos.get(pid)
        if ps is not None:
            changed = process_capture(state, os.path.join(PHOTOS, ps.file), ps, observer)
            print(f"[twin] {pid}: saw {ps.walls_seen} changed {changed} {ps.error}")
            state.save(STATE_PATH)
        jobs.task_done()


threading.Thread(target=_worker, daemon=True).start()
for p in state.photos.values():           # resume unfinished work after a restart
    if p.status in ("queued", "processing"):
        jobs.put(p.id)


@app.post("/plan")
async def post_plan(file: UploadFile = File(...),
                    px_per_m: float | None = Form(None),
                    entrance_x: float = Form(0.0), entrance_y: float = Form(0.0),
                    entrance_heading_deg: float = Form(0.0)):
    """Upload the SAME floor plan image the app uses (pixel coordinates must match)."""
    path = os.path.join(DATA, "plan" + os.path.splitext(file.filename or ".png")[1])
    with open(path, "wb") as f:
        f.write(await file.read())
    try:
        info = load_plan(state, path, px_per_m, (entrance_x, entrance_y), entrance_heading_deg)
    except ValueError as e:
        raise HTTPException(400, str(e))
    state.save(STATE_PATH)
    return info


@app.post("/capture")
async def post_capture(photo: UploadFile = File(...), meta: str = Form(...)):
    """meta = PinRecord JSON from the app."""
    if not state.walls:
        raise HTTPException(409, "upload a floor plan first (POST /plan)")
    m = json.loads(meta)
    pid = m.get("id") or uuid.uuid4().hex[:8]
    path = os.path.join(PHOTOS, f"{pid}.jpg")
    with open(path, "wb") as f:
        f.write(await photo.read())
    ps = register_capture(state, path, m, pid)
    jobs.put(ps.id)
    return {"id": ps.id, "queued": jobs.qsize()}


@app.get("/state")
def get_state(since: int = -1):
    if since >= state.version:
        return JSONResponse({"version": state.version, "unchanged": True})
    return state.to_dict()


@app.get("/photo/{pid}")
def get_photo(pid: str):
    ps = state.photos.get(pid)
    if ps is None:
        raise HTTPException(404)
    return FileResponse(os.path.join(PHOTOS, ps.file), media_type="image/jpeg")


@app.get("/plan_image")
def get_plan_image():
    if not state.plan_file:
        raise HTTPException(404)
    return FileResponse(os.path.join(DATA, state.plan_file))
