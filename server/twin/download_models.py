"""Download the open-source models once (before the demo, while you have good internet):
    python -m twin.download_models
"""
from .local_vision import LocalObserver


def main():
    obs = LocalObserver()
    print("segmentation:", type(obs.seg.model).__name__, "on", obs.seg.device)
    print("detector:", "ready" if obs.det else "disabled (segmentation still works)")


if __name__ == "__main__":
    main()
