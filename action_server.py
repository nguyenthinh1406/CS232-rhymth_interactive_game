"""
Action Server (MediaPipe Pose) - gửi 2 loại dữ liệu sang Unity mỗi frame:
  1. kpts   : 12 khớp 3D (mét, gốc ở hông) để PoseRetargeter dựng dáng người.
  2. move_dx/move_dz : ĐỘ DỊCH CHUYỂN THỰC (mét) của cơ thể trong frame này,
     suy ra từ độ thay đổi vị trí + kích thước vai trên ảnh. Nhân vật dịch
     ĐÚNG BẰNG độ dịch này mỗi frame -> bước 1 bước ngoài đời = nhân vật đi
     đúng 1 bước, đứng yên thì dx/dz = 0, không còn trôi vô hạn như bản cũ
     (bản cũ dùng "vùng trái/phải" nên hễ đứng lệch là cộng dồn vận tốc mãi).
  3. events : jump/punch/kick - vẫn là sự kiện rời rạc (bắn 1 lần, có cooldown).
  4. crouch : trạng thái liên tục (đang cúi hay không).

QUAN TRỌNG: MediaPipe chưa hỗ trợ Python 3.13. Chạy trong venv Python 3.11/3.12.

Cài đặt:
    pip install -r requirements.txt

Chạy:
    python action_server.py --cam 0 --host 127.0.0.1 --port 5065 --variant full
"""

import argparse
import json
import math
import os
import socket
import time
import urllib.request
from collections import deque

import cv2
import mediapipe as mp
from mediapipe.tasks import python as mp_python
from mediapipe.tasks.python import vision as mp_vision

MODEL_URLS = {
    "lite": "https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_lite/float16/latest/pose_landmarker_lite.task",
    "full": "https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_full/float16/latest/pose_landmarker_full.task",
    "heavy": "https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_heavy/float16/latest/pose_landmarker_heavy.task",
}

# ------- Chỉ số landmark của MediaPipe Pose (33 điểm) -------
L_SHOULDER, R_SHOULDER = 11, 12
L_ELBOW, R_ELBOW = 13, 14
L_WRIST, R_WRIST = 15, 16
L_HIP, R_HIP = 23, 24
L_KNEE, R_KNEE = 25, 26
L_ANKLE, R_ANKLE = 27, 28

# Thứ tự 12 khớp gửi sang Unity để dựng dáng người (PHẢI khớp PoseRetargeter.cs)
RETARGET_ORDER = [
    L_SHOULDER, R_SHOULDER, L_ELBOW, R_ELBOW, L_WRIST, R_WRIST,
    L_HIP, R_HIP, L_KNEE, R_KNEE, L_ANKLE, R_ANKLE,
]

ASSUMED_SHOULDER_WIDTH_M = 0.40  # bề rộng vai trung bình 1 người trưởng thành, dùng để quy đổi mét


def ensure_model(variant):
    path = os.path.join(os.path.dirname(__file__), f"pose_landmarker_{variant}.task")
    if os.path.exists(path):
        return path
    print(f"Đang tải model MediaPipe Pose ({variant}) về {path} ...")
    urllib.request.urlretrieve(MODEL_URLS[variant], path)
    print("Tải xong.")
    return path


class OneEuroFilter:
    """Lọc rung thời gian thực: đứng yên thì mượt, chuyển động nhanh thì không bị trễ."""

    def __init__(self, mincutoff=1.0, beta=0.4, dcutoff=1.0):
        self.mincutoff, self.beta, self.dcutoff = mincutoff, beta, dcutoff
        self.x_prev, self.dx_prev, self.t_prev = None, 0.0, None

    @staticmethod
    def _alpha(cutoff, dt):
        tau = 1.0 / (2 * math.pi * cutoff)
        return 1.0 / (1.0 + tau / dt)

    def __call__(self, x, t):
        if self.t_prev is None:
            self.t_prev, self.x_prev = t, x
            return x
        dt = max(t - self.t_prev, 1e-3)
        dx = (x - self.x_prev) / dt
        a_d = self._alpha(self.dcutoff, dt)
        dx_hat = a_d * dx + (1 - a_d) * self.dx_prev
        cutoff = self.mincutoff + self.beta * abs(dx_hat)
        a = self._alpha(cutoff, dt)
        x_hat = a * x + (1 - a) * self.x_prev
        self.x_prev, self.dx_prev, self.t_prev = x_hat, dx_hat, t
        return x_hat


class MotionTracker:
    """
    Suy ra độ dịch chuyển thực (mét) của cơ thể trong frame này từ toạ độ ẢNH:
    - Trái/phải: độ thay đổi vị trí ngang của tâm vai.
    - Tiến/lùi : độ thay đổi "kích thước biểu kiến" của vai (vai to ra = lại gần camera).
    Đây là ước lượng gần đúng (không phải đo bằng cảm biến thật), đủ dùng để
    nhân vật đi theo đúng nhịp bước của người chơi thay vì trôi vô hạn.
    """

    def __init__(self, scale=1.0, deadzone=0.0015, max_step=0.06):
        self.scale = scale
        self.deadzone = deadzone
        self.max_step = max_step
        self.prev_center_x = None
        self.prev_width = None

    def update(self, center_x_norm, shoulder_width_norm):
        shoulder_width_norm = max(shoulder_width_norm, 0.05)
        scale_x = ASSUMED_SHOULDER_WIDTH_M / shoulder_width_norm

        dx_m, dz_m = 0.0, 0.0
        if self.prev_center_x is not None:
            dx_norm = center_x_norm - self.prev_center_x
            dx_m = dx_norm * scale_x

            inv_now = 1.0 / shoulder_width_norm
            inv_prev = 1.0 / max(self.prev_width, 0.05)
            dz_m = -ASSUMED_SHOULDER_WIDTH_M * (inv_now - inv_prev)

        self.prev_center_x = center_x_norm
        self.prev_width = shoulder_width_norm

        # Vùng chết: bỏ qua rung nhiễu cực nhỏ khi đứng yên để không trôi dần
        if abs(dx_m) < self.deadzone:
            dx_m = 0.0
        if abs(dz_m) < self.deadzone:
            dz_m = 0.0

        # Giới hạn mỗi frame để tránh "nhảy vọt" khi mất dấu/bắt lại người
        dx_m = max(-self.max_step, min(self.max_step, dx_m)) * self.scale
        dz_m = max(-self.max_step, min(self.max_step, dz_m)) * self.scale
        return dx_m, dz_m


class EventClassifier:
    """Sự kiện rời rạc (bắn 1 lần + cooldown): jump, punch, kick. Crouch là trạng thái liên tục."""

    def __init__(self, cooldown=0.35):
        self.cooldown = cooldown
        self.last_time = {"jump": 0.0, "punch": 0.0, "kick": 0.0}
        self._hip_y_history = deque(maxlen=5)

    def update(self, img_lm, world_lm, now):
        events = []

        def world_xyz(idx):
            lm = world_lm[idx]
            return lm.x, lm.y, lm.z, lm.visibility

        def dist3(ax, ay, az, bx, by, bz):
            return math.sqrt((ax - bx) ** 2 + (ay - by) ** 2 + (az - bz) ** 2)

            # (hàm phụ, không dùng trực tiếp)

        lsx, lsy, lsz, lsv = world_xyz(L_SHOULDER)
        rsx, rsy, rsz, rsv = world_xyz(R_SHOULDER)
        lhx, lhy, lhz, lhv = world_xyz(L_HIP)
        rhx, rhy, rhz, rhv = world_xyz(R_HIP)
        lkx, lky, lkz, lkv = world_xyz(L_KNEE)
        rkx, rky, rkz, rkv = world_xyz(R_KNEE)

        shoulder_c = ((lsx + rsx) / 2, (lsy + rsy) / 2, (lsz + rsz) / 2)
        hip_c = ((lhx + rhx) / 2, (lhy + rhy) / 2, (lhz + rhz) / 2)
        knee_c = ((lkx + rkx) / 2, (lky + rky) / 2, (lkz + rkz) / 2)
        torso_len = max(dist3(*shoulder_c, *hip_c), 1e-3)

        # ---- CROUCH: trạng thái liên tục ----
        knee_hip_dist = dist3(*knee_c, *hip_c)
        crouch_ratio = knee_hip_dist / torso_len
        crouch = crouch_ratio < 0.55 and lhv > 0.5 and lkv > 0.5

        # ---- PUNCH: cổ tay vươn xa vai (3D thật) ----
        for wrist, shoulder in ((L_WRIST, L_SHOULDER), (R_WRIST, R_SHOULDER)):
            wx, wy, wz, wv = world_xyz(wrist)
            sx, sy, sz, sv = world_xyz(shoulder)
            if wv > 0.5 and sv > 0.5 and (now - self.last_time["punch"]) > self.cooldown:
                reach = dist3(wx, wy, wz, sx, sy, sz) / torso_len
                if reach > 1.3:
                    self.last_time["punch"] = now
                    events.append("punch")
                    break

        # ---- KICK: mắt cá chân nhấc cao/xa so với hông ----
        for ankle in (L_ANKLE, R_ANKLE):
            ax, ay, az, av = world_xyz(ankle)
            if av > 0.5 and (now - self.last_time["kick"]) > self.cooldown:
                lift = dist3(ax, ay, az, *hip_c) / torso_len
                if lift > 1.15:
                    self.last_time["kick"] = now
                    events.append("kick")
                    break

        # ---- JUMP: hông (toạ độ ẢNH) dịch lên nhanh ----
        l_hip_img, r_hip_img = img_lm[L_HIP], img_lm[R_HIP]
        l_sh_img, r_sh_img = img_lm[L_SHOULDER], img_lm[R_SHOULDER]
        hip_y_img = (l_hip_img.y + r_hip_img.y) / 2
        torso_len_img = max(abs(hip_y_img - (l_sh_img.y + r_sh_img.y) / 2), 1e-3)

        self._hip_y_history.append(hip_y_img)
        if len(self._hip_y_history) >= 5 and (now - self.last_time["jump"]) > self.cooldown:
            dy = self._hip_y_history[0] - self._hip_y_history[-1]
            speed = dy / torso_len_img
            if speed > 0.35:
                self.last_time["jump"] = now
                events.append("jump")

        return events, crouch


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cam", type=int, default=0)
    parser.add_argument("--host", type=str, default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5065)
    parser.add_argument("--variant", type=str, default="full", choices=["lite", "full", "heavy"])
    parser.add_argument("--backend", type=str, default="auto", choices=["auto", "dshow", "msmf"])
    parser.add_argument("--mincutoff", type=float, default=0.8)
    parser.add_argument("--beta", type=float, default=0.4)
    parser.add_argument(
        "--move-scale", type=float, default=1.0,
        help="Hệ số nhân độ dịch chuyển (>1 = đi nhanh hơn thực tế, <1 = chậm hơn)",
    )
    parser.add_argument("--show", action="store_true", help="Hiện cửa sổ debug xem pose")
    args, _unknown = parser.parse_known_args()

    model_path = ensure_model(args.variant)
    base_options = mp_python.BaseOptions(model_asset_path=model_path)
    options = mp_vision.PoseLandmarkerOptions(
        base_options=base_options,
        running_mode=mp_vision.RunningMode.VIDEO,
        num_poses=1,
        min_pose_detection_confidence=0.5,
        min_tracking_confidence=0.5,
    )
    landmarker = mp_vision.PoseLandmarker.create_from_options(options)

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    dest = (args.host, args.port)

    backend_map = {"dshow": cv2.CAP_DSHOW, "msmf": cv2.CAP_MSMF, "auto": cv2.CAP_ANY}
    cap = cv2.VideoCapture(args.cam, backend_map[args.backend])
    if not cap.isOpened():
        print(f"Không mở được camera index={args.cam}. Thử --backend dshow hoặc --cam 1.")
        return

    retarget_filters = [OneEuroFilter(args.mincutoff, args.beta) for _ in range(len(RETARGET_ORDER) * 3)]
    center_x_filter = OneEuroFilter(args.mincutoff, args.beta)
    width_filter = OneEuroFilter(args.mincutoff, args.beta)
    tracker = MotionTracker(scale=args.move_scale)
    events_clf = EventClassifier()

    start_time = time.time()
    print(f"Đang gửi dữ liệu tới {args.host}:{args.port} (model={args.variant}, Ctrl+C để dừng)")

    try:
        consecutive_failures = 0
        while True:
            ok, frame = cap.read()
            if not ok:
                consecutive_failures += 1
                if consecutive_failures > 30:
                    print("Camera liên tục không đọc được frame, dừng chương trình.")
                    break
                continue
            consecutive_failures = 0

            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)
            now = time.time()
            timestamp_ms = int((now - start_time) * 1000)
            result = landmarker.detect_for_video(mp_image, timestamp_ms)

            kpts_flat = []
            move_dx, move_dz = 0.0, 0.0
            crouch = False
            events = []

            if result.pose_world_landmarks and result.pose_landmarks:
                world_lm = result.pose_world_landmarks[0]
                img_lm = result.pose_landmarks[0]

                for i, idx in enumerate(RETARGET_ORDER):
                    lm = world_lm[idx]
                    fx = retarget_filters[i * 3 + 0](lm.x, now)
                    fy = retarget_filters[i * 3 + 1](lm.y, now)
                    fz = retarget_filters[i * 3 + 2](lm.z, now)
                    kpts_flat.extend([round(fx, 4), round(fy, 4), round(fz, 4), round(float(lm.visibility), 3)])

                center_x_raw = (img_lm[L_SHOULDER].x + img_lm[R_SHOULDER].x) / 2
                width_raw = abs(img_lm[R_SHOULDER].x - img_lm[L_SHOULDER].x)
                center_x = center_x_filter(center_x_raw, now)
                width = width_filter(width_raw, now)
                move_dx, move_dz = tracker.update(center_x, width)

                events, crouch = events_clf.update(img_lm, world_lm, now)

            payload = json.dumps(
                {
                    "ts": now,
                    "kpts": kpts_flat,
                    "move_dx": round(move_dx, 5),
                    "move_dz": round(move_dz, 5),
                    "crouch": crouch,
                    "events": events,
                }
            ).encode("utf-8")
            sock.sendto(payload, dest)

            if args.show:
                annotated = frame.copy()
                if result.pose_landmarks:
                    h, w = frame.shape[:2]
                    for lm in result.pose_landmarks[0]:
                        cv2.circle(annotated, (int(lm.x * w), int(lm.y * h)), 4, (0, 255, 0), -1)
                label = f"dx={move_dx:+.3f} dz={move_dz:+.3f} crouch={crouch} {events}"
                cv2.putText(annotated, label, (20, 40), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 0), 2)
                cv2.imshow("Action Server Debug", annotated)
                if cv2.waitKey(1) & 0xFF == ord("q"):
                    break
    except KeyboardInterrupt:
        pass
    finally:
        cap.release()
        cv2.destroyAllWindows()
        sock.close()
        landmarker.close()


if __name__ == "__main__":
    main()