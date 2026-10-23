"""
Beatmap Generator - phân tích 1 file nhạc bằng librosa, tính nhịp độ (BPM), vị trí
từng nhịp (beat) và đặc trưng âm thanh tại mỗi nhịp, rồi sinh "beatmap" JSON:
danh sách chướng ngại vật kèm thời điểm chúng phải chạm người chơi.

Quy trình xử lý tín hiệu:
  1. Đọc âm thanh, đưa về mono, tần số lấy mẫu 22.05 kHz.
  2. Onset strength envelope (độ tăng năng lượng phổ theo thời gian) -> dò nhịp
     bằng quy hoạch động (librosa.beat.beat_track) -> tempo (BPM) + thời điểm từng beat.
     Dò nhịp dễ bị "nhầm pha" (bám vào tiếng hi-hat trái nhịp thay vì trống bass), nên
     thêm bước chọn pha: so sánh lưới beat với lưới lệch nửa nhịp, giữ lưới nào có
     năng lượng bass (< 150 Hz) tăng đột ngột mạnh hơn, rồi canh từng beat vào đỉnh bass gần nhất.
  3. Trích đặc trưng quanh mỗi beat:
       - RMS (năng lượng)          -> độ "mạnh" của nhịp
       - Spectral centroid (độ sáng) -> nhạc trầm hay chói tại nhịp đó
     rồi đổi sang thứ hạng phần trăm (0..1) để không phụ thuộc âm lượng bản thu.
  4. Nhịp độ vật cản bám theo nhạc: dựng đường cong "cường độ" (độ to kết hợp độ dày của nhịp),
     rồi khoảng cách giữa 2 vật cản co giãn liên tục theo đường cong đó (êm -> thưa, dồn dập ->
     dày), có tính BPM và sàn tối thiểu để người chơi kịp phản ứng. Tốc độ bay của vật cũng
     thay đổi nhẹ theo cường độ (speed_mult).
  5. Loại chướng ngại vật: mỗi loại có "điểm hợp" với âm thanh tại mốc đó (to/nhỏ, chói/trầm,
     dứt khoát/êm), cộng cơ chế đa dạng (phạt dùng lặp, thưởng loại đang thiếu, nhiễu nhỏ có
     hạt giống cố định) để cả bài dùng đủ 5 loại mà không lặp theo chu kỳ cứng nhắc.

Cài đặt (trong venv Python 3.11/3.12):
    pip install librosa soundfile matplotlib

Chạy:
    python beatmap_generator.py song.wav -o song_beatmap.json --difficulty normal --plot song.png
"""

import argparse
import json
import os
import sys
import time
import zlib

import numpy as np
import librosa

# Khi Unity chạy script này và đọc đầu ra qua pipe, Python trên Windows mặc định dùng
# bảng mã cục bộ (cp1252) -> in tiếng Việt bị lỗi. Ép UTF-8 để Unity đọc đúng.
for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8")
    except Exception:
        pass


def stage(msg):
    """Báo tiến trình cho Unity (dòng bắt đầu bằng [STAGE] sẽ được hiển thị trên màn hình chọn bài)."""
    print(f"[STAGE] {msg}", flush=True)

TYPES = ["Jump", "Crouch", "DodgeLeft", "DodgeRight", "PunchWall"]

# difficulty -> (gap_min, gap_max, floor): khoảng cách (giây) giữa 2 vật cản liên tiếp.
#   Đoạn nhạc dồn dập nhất -> gap_min, đoạn êm nhất -> gap_max, nội suy mượt ở giữa.
#   floor là sàn tuyệt đối để người chơi (qua webcam, có độ trễ) kịp làm xong động tác trước vật kế tiếp.
DIFFICULTY = {
    "easy":   (1.8, 4.2, 1.4),
    "normal": (1.2, 3.4, 1.0),
    "hard":   (0.85, 2.2, 0.8),
}


def percentile_rank(x):
    """Đổi mảng giá trị sang thứ hạng 0..1 (không phụ thuộc thang đo/âm lượng)."""
    x = np.asarray(x, dtype=float)
    if len(x) <= 1:
        return np.zeros_like(x)
    return np.argsort(np.argsort(x)) / (len(x) - 1)


def bass_onset(y, hop, sr, n_fft=2048, cutoff_hz=150.0):
    """Độ tăng đột ngột năng lượng dải bass (<150 Hz) theo thời gian - dấu hiệu của tiếng trống bass."""
    S = np.abs(librosa.stft(y, n_fft=n_fft, hop_length=hop))
    freqs = librosa.fft_frequencies(sr=sr, n_fft=n_fft)
    low = S[freqs < cutoff_hz].sum(axis=0)
    return np.maximum(0.0, np.diff(low, prepend=low[0]))


def grid_score(times, env, hop, sr, win_s=0.05):
    """Điểm trung bình của env tại các mốc thời gian (lấy đỉnh trong cửa sổ +-win_s)."""
    frames = librosa.time_to_frames(times, sr=sr, hop_length=hop)
    w = max(1, int(round(win_s * sr / hop)))
    vals = [env[max(0, f - w): f + w + 1].max() for f in frames if 0 <= f < len(env)]
    return float(np.mean(vals)) if vals else 0.0


def align_beats(beat_times, env, hop, sr, duration):
    """Chọn đúng pha (nhịp mạnh, không phải nhịp lơ lửng) rồi canh từng beat vào đỉnh bass gần nhất."""
    if len(beat_times) < 4:
        return beat_times
    period = float(np.median(np.diff(beat_times)))

    shifted = beat_times + period / 2.0
    shifted = shifted[shifted < duration]
    # Chỉ đổi pha khi lưới lệch nửa nhịp rõ ràng mạnh hơn (tránh đổi nhầm ở nhạc không có bass rõ)
    if len(shifted) >= 4 and grid_score(shifted, env, hop, sr) > 1.3 * grid_score(beat_times, env, hop, sr):
        beat_times = shifted

    # Canh từng beat vào đỉnh bass gần nhất trong +-min(60ms, 20% chu kỳ)
    win = min(0.06, 0.2 * period)
    w = max(1, int(round(win * sr / hop)))
    snapped = []
    for t in beat_times:
        f = int(librosa.time_to_frames(t, sr=sr, hop_length=hop))
        lo, hi = max(0, f - w), min(len(env), f + w + 1)
        if hi > lo and env[lo:hi].max() > 0.2 * env.max():
            f = lo + int(np.argmax(env[lo:hi]))
        snapped.append(float(librosa.frames_to_time(f, sr=sr, hop_length=hop)))
    return np.array(sorted(set(snapped)))


def analyze(path, hop=256, sr=22050):
    # hop nhỏ -> ước lượng tempo mịn hơn (hop 512 chỉ phân giải được ~3 BPM ở 120 BPM)
    stage("Đọc và giải mã âm thanh")
    try:
        y, sr = librosa.load(path, sr=sr, mono=True)
    except Exception as e:
        raise RuntimeError(
            f"Không đọc được file âm thanh ({type(e).__name__}). "
            "Thử file .wav hoặc .ogg, hoặc cài ffmpeg nếu là .mp3"
        ) from e
    duration = len(y) / sr

    stage("Dò nhịp (onset + beat tracking)")
    onset_env = librosa.onset.onset_strength(y=y, sr=sr, hop_length=hop)
    # trim=False: không cắt các beat đầu/cuối bài có onset yếu (intro nhẹ vẫn cần có nhịp để chơi)
    tempo, beat_frames = librosa.beat.beat_track(onset_envelope=onset_env, sr=sr, hop_length=hop, trim=False)
    bpm = float(np.atleast_1d(tempo)[0])
    beat_times = librosa.frames_to_time(beat_frames, sr=sr, hop_length=hop)

    stage("Chọn pha và canh beat theo năng lượng bass")
    bass_env = bass_onset(y, hop, sr)
    beat_times = align_beats(beat_times, bass_env, hop, sr, duration)
    beat_frames = librosa.time_to_frames(beat_times, sr=sr, hop_length=hop)
    if len(beat_times) >= 2:
        bpm = 60.0 / float(np.median(np.diff(beat_times)))  # BPM tinh chỉnh từ chính các beat đã canh

    stage("Trích đặc trưng âm thanh (năng lượng, độ chói)")
    rms = librosa.feature.rms(y=y, hop_length=hop)[0]
    centroid = librosa.feature.spectral_centroid(y=y, sr=sr, hop_length=hop)[0]

    # Giá trị đặc trưng quanh mỗi beat (trung bình cửa sổ +-3 frame ~ 35ms mỗi bên)
    def around(feature, frame, w=3):
        lo, hi = max(0, frame - w), min(len(feature), frame + w + 1)
        return float(np.mean(feature[lo:hi]))

    beat_rms = np.array([around(rms, f) for f in beat_frames])
    beat_centroid = np.array([around(centroid, f) for f in beat_frames])

    return {
        "y": y, "sr": sr, "hop": hop, "duration": duration, "bpm": bpm,
        "onset_env": onset_env, "beat_frames": beat_frames, "beat_times": beat_times,
        "rms": rms, "centroid": centroid, "bass_env": bass_env,   # đặc trưng liên tục theo frame
        "energy": percentile_rank(beat_rms),          # 0..1
        "brightness": percentile_rank(beat_centroid),  # 0..1
    }


def intensity_curve(a, smooth_beats=9):
    """
    Đường cong "cường độ" 0..1 tại từng beat = độ to (RMS) kết hợp độ dày/dồn dập của nhịp
    (onset strength trung bình), tính trong cửa sổ +-1 giây rồi làm mượt ~2 ô nhịp.
    Chuẩn hoá theo từng bài (p10..p90); bài ít thay đổi to/nhỏ thì co biên độ lại về giữa,
    nên nhạc đều đều sẽ cho nhịp độ vật cản đều đều chứ không bị phóng đại.
    """
    bt = a["beat_times"]
    if len(bt) == 0:
        return np.zeros(0)
    loud = np.array([window_stat(a["rms"], t, a, 1.0, 1.0, np.mean) for t in bt])
    act = np.array([window_stat(a["onset_env"], t, a, 1.0, 1.0, np.mean) for t in bt])

    def norm01(x):
        p10, p90 = np.percentile(x, [10, 90])
        z = np.clip((x - p10) / (p90 - p10 + 1e-9), 0.0, 1.0)
        spread = (p90 - p10) / (p90 + p10 + 1e-9)              # biên độ động tương đối
        contrast = float(np.clip(spread / 0.30, 0.35, 1.0))
        return 0.5 + (z - 0.5) * contrast

    raw = 0.55 * norm01(loud) + 0.45 * norm01(act)
    k = np.hanning(smooth_beats + 2)[1:-1]
    k = k / k.sum()
    pad = smooth_beats // 2
    sm = np.convolve(np.pad(raw, pad, mode="edge"), k, mode="valid")
    return np.clip(sm[: len(raw)], 0.0, 1.0)


def select_times(a, difficulty, first_time, end_margin, pace=1.0):
    """
    Chọn beat nào có vật cản. Khoảng cách tới vật kế tiếp co giãn LIÊN TỤC theo cường độ nhạc:
        gap = gap_max - (gap_max - gap_min) * cường_độ     (êm -> thưa, dồn dập -> dày)
    nhân thêm hệ số theo BPM (nhạc nhanh -> nhịp độ nhanh hơn một chút), chia cho pace,
    rồi chặn dưới bởi sàn floor. Vật luôn rơi đúng vào 1 beat (lấy beat gần mốc mục tiêu nhất).
    """
    gmin, gmax, floor = DIFFICULTY[difficulty]
    beats, inten = a["beat_times"], a["intensity"]
    n = len(beats)
    tempo_f = float(np.clip((100.0 / max(a["bpm"], 1e-6)) ** 0.5, 0.75, 1.25))

    def gap_at(t):
        i = float(np.interp(t, beats, inten))
        return max(floor, (gmax - (gmax - gmin) * i) * tempo_f / max(pace, 1e-6))

    i0 = int(np.searchsorted(beats, first_time))
    if i0 >= n:
        return []
    chosen, last_t = [i0], float(beats[i0])
    while True:
        g0 = gap_at(last_t)
        g1 = gap_at(last_t + 0.5 * g0)        # nhìn trước nửa khoảng để vào đoạn mới thì đổi nhịp kịp
        target = last_t + 0.5 * (g0 + g1)
        lo = int(np.searchsorted(beats, last_t + floor - 1e-6))
        if lo >= n:
            break
        j = lo + int(np.argmin(np.abs(beats[lo:] - target)))
        if float(beats[j]) > a["duration"] - end_margin:
            break
        chosen.append(j)
        last_t = float(beats[j])
    return chosen


def window_stat(feature, t, a, pre, post, fn):
    """Thống kê (fn) của 1 đặc trưng theo frame trong cửa sổ [t-pre, t+post] giây."""
    f0 = int(librosa.time_to_frames(max(0.0, t - pre), sr=a["sr"], hop_length=a["hop"]))
    f1 = int(librosa.time_to_frames(t + post, sr=a["sr"], hop_length=a["hop"]))
    f0 = min(max(0, f0), len(feature) - 1)
    f1 = min(max(f0 + 1, f1), len(feature))
    return float(fn(feature[f0:f1]))


# Tham số cân bằng giữa "hợp âm thanh" và "đa dạng" khi chọn loại vật cản
TYPE_PARAMS = {
    "aff_scale": 1.5,    # nhân điểm hợp âm thanh (lớn -> bám nhạc hơn, đa dạng giảm)
    "noise": 0.20,       # độ lệch chuẩn nhiễu ngẫu nhiên có hạt giống
    "recent_n": 4,       # số lần chọn gần nhất bị xét "dùng lại"
    "recent_w": 0.30,    # mức phạt cho mỗi lần dùng lại trong recent_n
    "repeat_pen": 0.50,  # phạt khi trùng loại ngay liền trước
    "balance_w": 0.50,   # thưởng cho loại đang dùng ít hơn tỉ lệ mục tiêu
    "cycle_w": 0.45,     # phạt khi tiếp tục 1 chu kỳ lặp đang hình thành (giống k nấc trước, k=2..6)
}
# (Bộ giá trị này chọn bằng cách dò trên 3 bài nhạc thử x 3 mức khó x 3 seed, cân bằng giữa
#  độ đa dạng, mức lặp chu kỳ và việc loại vật cản còn bám theo âm thanh.)


def assign_types(times, a, rng):
    """
    Chọn loại chướng ngại vật cho từng mốc thời gian.

    Điểm của mỗi loại = mức "hợp" với âm thanh tại đó + 3 cơ chế đa dạng:
      - Hợp âm thanh (đặc trưng lấy trong cửa sổ quanh mốc, rồi đổi sang thứ hạng 0..1 giữa
        CHÍNH các mốc đã chọn, nên luôn trải đều dù các beat được chọn nghe giống nhau):
          PunchWall : to + dứt khoát (loud, attack cao)        -> "đập mạnh"
          Jump      : chói + dứt khoát (bright, attack cao)    -> "nảy lên"
          Crouch    : nhỏ + trầm (loud, bright thấp)           -> "lắng xuống"
          Dodge     : âm sắc trung bình; hướng theo độ chói đang tăng (phải) hay giảm (trái)
      - Phạt dùng lại loại vừa dùng gần đây (4 lần gần nhất) và phạt mạnh nếu trùng loại liền trước.
      - Thưởng cho loại đang dùng ít hơn tỉ lệ mục tiêu (cân bằng cả bài).
      - Nhiễu ngẫu nhiên nhỏ có hạt giống cố định (cùng bài + cùng seed -> cùng kết quả),
        để phá các chu kỳ lặp cứng nhắc mà vẫn tái lập được kết quả.
    """
    n = len(times)
    loud_raw = np.array([window_stat(a["rms"], t, a, 0.2, 0.2, np.mean) for t in times])
    bright_raw = np.array([window_stat(a["centroid"], t, a, 0.2, 0.2, np.mean) for t in times])
    attack_raw = np.array([window_stat(a["onset_env"], t, a, 0.08, 0.08, np.max) for t in times])

    loud = percentile_rank(loud_raw)
    bright = percentile_rank(bright_raw)
    attack = percentile_rank(attack_raw)
    delta = np.diff(bright_raw, prepend=bright_raw[0]) if n else bright_raw
    delta_eps = 0.05 * (np.std(delta) + 1e-9)

    target = {"Jump": 0.21, "Crouch": 0.21, "DodgeLeft": 0.21, "DodgeRight": 0.21, "PunchWall": 0.16}
    history, counts = [], {t: 0 for t in TYPES}

    for i in range(n):
        mid = 1.0 - 2.0 * abs(bright[i] - 0.5)                        # đỉnh ở âm sắc trung bình
        if abs(delta[i]) < delta_eps:
            go_right = bool(rng.random() < 0.5)                       # không rõ hướng -> ngẫu nhiên có hạt giống
        else:
            go_right = bool(delta[i] > 0)
        aff = {
            "PunchWall": 0.55 * loud[i] + 0.45 * attack[i],
            "Jump": 0.55 * bright[i] + 0.45 * attack[i],
            "Crouch": 0.60 * (1.0 - loud[i]) + 0.40 * (1.0 - bright[i]),
            "DodgeLeft": mid * (0.6 + 0.4 * (not go_right)),
            "DodgeRight": mid * (0.6 + 0.4 * go_right),
        }

        scores = {}
        for typ in TYPES:
            P = TYPE_PARAMS
            sc = P["aff_scale"] * aff[typ] + rng.normal(0.0, P["noise"])
            sc -= P["recent_w"] * history[-P["recent_n"]:].count(typ) # dùng nhiều gần đây -> bị phạt
            if history and history[-1] == typ:
                sc -= P["repeat_pen"]                                 # trùng liền trước -> phạt mạnh
            if typ == "PunchWall" and "PunchWall" in history[-2:]:
                sc -= 0.80                                            # tường không dồn liên tiếp
            if P["cycle_w"] > 0:
                # Vừa chọn loại giống k nấc trước => đang hình thành chu kỳ k. Chọn tiếp đúng loại
                # mà chu kỳ đó "mong đợi" (history[-k]) sẽ bị phạt -> phá lặp tuần tự.
                for lag in range(2, 7):
                    if len(history) > lag and history[-1] == history[-1 - lag] and typ == history[-lag]:
                        sc -= P["cycle_w"]
            share = counts[typ] / max(1, len(history))
            sc += P["balance_w"] * (target[typ] - share)              # thiếu so với mục tiêu -> được thưởng
            scores[typ] = sc

        pick = max(scores, key=scores.get)
        history.append(pick)
        counts[pick] += 1

    return history, loud, bright


def build_obstacles(a, difficulty, first_time, end_margin, seed=0, pace=1.0):
    a["intensity"] = intensity_curve(a)
    chosen = select_times(a, difficulty, first_time, end_margin, pace)
    times = [float(a["beat_times"][i]) for i in chosen]
    rng = np.random.default_rng(seed)
    types, loud, bright = assign_types(times, a, rng)

    out = []
    for k, (t, typ) in enumerate(zip(times, types)):
        inten = float(np.interp(t, a["beat_times"], a["intensity"]))
        out.append({
            "time": round(t, 3), "type": typ,
            "energy": round(float(loud[k]), 3), "brightness": round(float(bright[k]), 3),
            "intensity": round(inten, 3),
            # Hệ số tốc độ bay của vật (Unity nhân với tốc độ gốc): đoạn dồn dập bay nhanh hơn, đoạn êm bay chậm hơn.
            # Vật vẫn chạm người chơi đúng giờ - chỉ khác độ "lao tới" nhìn thấy.
            "speed_mult": round(0.85 + 0.40 * inten, 3),
        })
    return out


def save_plot(a, obstacles, path):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    t = librosa.times_like(a["onset_env"], sr=a["sr"], hop_length=a["hop"])
    fig, ax = plt.subplots(2, 1, figsize=(14, 6), sharex=True)
    ax[0].plot(np.arange(len(a["y"])) / a["sr"], a["y"], lw=0.4, color="#4a6")
    ax[0].set_ylabel("Dạng sóng")
    ax[1].plot(t, a["onset_env"], lw=0.8, label="Onset strength")
    ax[1].vlines(a["beat_times"], 0, a["onset_env"].max(), color="r", alpha=0.25, label="Beat")
    colors = {"Jump": "gold", "Crouch": "c", "DodgeLeft": "orange", "DodgeRight": "m", "PunchWall": "gray"}
    for o in obstacles:
        ax[1].axvline(o["time"], color=colors[o["type"]], lw=2.2)
    if "intensity" in a:
        ax2 = ax[1].twinx()
        ax2.plot(a["beat_times"], a["intensity"], color="k", lw=1.8, label="Cường độ (điều khiển nhịp độ)")
        ax2.set_ylim(0, 1.05)
        ax2.set_ylabel("Cường độ 0..1")
        ax2.legend(loc="upper left")
    ax[1].set_ylabel("Onset / beat / vật cản")
    ax[1].set_xlabel("Thời gian (s)")
    ax[1].legend(loc="upper right")
    fig.suptitle(f"BPM ~ {a['bpm']:.1f} | {len(a['beat_times'])} beat | {len(obstacles)} vật cản")
    fig.tight_layout()
    fig.savefig(path, dpi=110)
    plt.close(fig)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("audio", help="File nhạc (.wav/.ogg/.mp3/.flac)")
    ap.add_argument("-o", "--output", default=None, help="File beatmap JSON (mặc định: <tên nhạc>_beatmap.json)")
    ap.add_argument("--difficulty", choices=list(DIFFICULTY), default="normal")
    ap.add_argument("--first-time", type=float, default=4.0, help="Vật cản đầu tiên không sớm hơn mốc này (giây)")
    ap.add_argument("--end-margin", type=float, default=2.0, help="Không sinh vật cản trong N giây cuối bài")
    ap.add_argument("--pace", type=float, default=1.0,
                    help="Hệ số nhịp độ: >1 vật cản dày hơn, <1 thưa hơn (mặc định 1.0)")
    ap.add_argument("--seed", type=int, default=None,
                    help="Hạt giống cho phần chọn loại (mặc định suy từ tên + độ dài bài: cùng bài -> cùng kết quả). "
                         "Đổi số này để ra 1 kiểu sắp xếp khác từ cùng 1 bài")
    ap.add_argument("--plot", default=None, help="Lưu biểu đồ phân tích (PNG) để đưa vào báo cáo")
    args = ap.parse_args()

    t0 = time.time()
    print(f"Đang phân tích {args.audio} ...", flush=True)
    a = analyze(args.audio)
    stage("Sinh chướng ngại vật theo nhịp")
    seed = args.seed if args.seed is not None else zlib.crc32(
        f"{os.path.basename(args.audio)}|{a['duration']:.2f}".encode("utf-8")) & 0x7FFFFFFF
    obstacles = build_obstacles(a, args.difficulty, args.first_time, args.end_margin, seed, args.pace)

    beatmap = {
        "audio_file": os.path.basename(args.audio),
        "bpm": round(a["bpm"], 2),
        "duration": round(a["duration"], 2),
        "difficulty": args.difficulty,
        "analysis_seconds": round(time.time() - t0, 2),
        "seed": seed,
        "pace": args.pace,
        "beats": [round(float(t), 3) for t in a["beat_times"]],
        "obstacles": obstacles,
    }

    stage("Lưu beatmap")
    out = args.output or os.path.splitext(args.audio)[0] + "_beatmap.json"
    with open(out, "w", encoding="utf-8") as f:
        json.dump(beatmap, f, ensure_ascii=False, indent=1)

    counts = {t: sum(1 for o in obstacles if o["type"] == t) for t in TYPES}
    print(f"BPM ước lượng : {a['bpm']:.1f}")
    print(f"Thời lượng    : {a['duration']:.1f}s, {len(a['beat_times'])} beat")
    print(f"Vật cản       : {len(obstacles)}  {counts}")
    print(f"Đã lưu beatmap: {out}")

    if args.plot:
        save_plot(a, obstacles, args.plot)
        print(f"Đã lưu biểu đồ: {args.plot}")


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        # Unity đọc dòng [ERROR] này để hiển thị lý do thất bại cho người chơi
        print(f"[ERROR] {e}", file=sys.stderr, flush=True)
        sys.exit(1)