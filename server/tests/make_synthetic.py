"""Synthetic floor plan for testing: 40 px/m, 10 px walls, 0.9 m (36 px) door gaps, text, door arcs, a diagonal wall."""
import cv2
import numpy as np

PX_PER_M = 40
T = 10  # wall thickness px


def make(path="synthetic_plan.png"):
    img = np.full((620, 860, 3), 255, np.uint8)
    blk = (20, 20, 20)

    def wall(x1, y1, x2, y2):
        cv2.rectangle(img, (x1 - T // 2, y1 - T // 2), (x2 + T // 2, y2 + T // 2), blk, -1)

    # Outer shell 20 m x 14 m at (30,30)
    wall(30, 30, 830, 30)
    wall(30, 590, 375, 590); wall(421, 590, 830, 590)          # entrance: 36 px clear gap (x 380..416)
    wall(30, 30, 30, 590); wall(830, 30, 830, 590)
    # Interior: corridor wall y=310 with two doors
    wall(30, 310, 195, 310); wall(241, 310, 515, 310); wall(561, 310, 830, 310)  # 36 px clear doors
    # Room divider x=430 in top half with one door
    wall(430, 30, 430, 145); wall(430, 191, 430, 310)
    # Diagonal wall in bottom-right
    cv2.line(img, (600, 590), (830, 400), blk, T)  # joins bottom and right walls
    # Clutter that must be ignored: text, dimension line, door arcs
    cv2.putText(img, "OFFICE 101", (120, 170), cv2.FONT_HERSHEY_SIMPLEX, 0.8, blk, 1)
    cv2.putText(img, "LAB", (600, 170), cv2.FONT_HERSHEY_SIMPLEX, 0.8, blk, 1)
    cv2.line(img, (30, 610), (830, 610), blk, 1)
    cv2.ellipse(img, (200, 310), (36, 36), 0, 270, 360, blk, 1)
    cv2.ellipse(img, (430, 150), (36, 36), 0, 0, 90, blk, 1)
    cv2.imwrite(path, img)
    return path


if __name__ == "__main__":
    print(make())
