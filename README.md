# Game né chướng ngại vật theo nhịp nhạc, điều khiển bằng chuyển động cơ thể

Đồ án môn **Tính toán đa phương tiện**. Người chơi đứng trước webcam; nhân vật trong game bắt chước toàn bộ chuyển động của người chơi theo thời gian thực. Chướng ngại vật được **sinh tự động từ bất kỳ file nhạc nào** người chơi chọn, và chạm người chơi đúng vào nhịp.

---

## Mục lục

1. [Giới thiệu và luật chơi](#1-giới-thiệu-và-luật-chơi)
2. [Kiến trúc tổng thể](#2-kiến-trúc-tổng-thể)
3. [Phần A - Xử lý video và ước lượng tư thế](#3-phần-a---xử-lý-video-và-ước-lượng-tư-thế)
4. [Phần B - Truyền dữ liệu thời gian thực (streaming)](#4-phần-b---truyền-dữ-liệu-thời-gian-thực-streaming)
5. [Phần C - Xử lý âm thanh và sinh beatmap](#5-phần-c---xử-lý-âm-thanh-và-sinh-beatmap)
6. [Phần D - Đồng bộ âm thanh và hình ảnh](#6-phần-d---đồng-bộ-âm-thanh-và-hình-ảnh)
7. [Phần E - Chấm điểm va chạm](#7-phần-e---chấm-điểm-va-chạm)
8. [Chọn nhạc lúc chạy](#8-chọn-nhạc-lúc-chạy)
9. [Cài đặt và chạy](#9-cài-đặt-và-chạy)
10. [Bảng tham số tinh chỉnh](#10-bảng-tham-số-tinh-chỉnh)
11. [Kết quả kiểm thử](#11-kết-quả-kiểm-thử)
12. [Hạn chế đã biết](#12-hạn-chế-đã-biết)
13. [Tài liệu tham khảo](#13-tài-liệu-tham-khảo)

---

## 1. Giới thiệu và luật chơi

Vật cản bay từ phía trước về phía người chơi. Mỗi vật cản đòi hỏi **một động tác thật** của người chơi, và phải làm đúng lúc vật chạm.

| Vật cản | Hành động cần làm | Điều kiện né thành công |
|---|---|---|
| `Jump` (vật thấp dưới đất) | Nhảy | Đang ở trên không, hoặc vừa nhảy trong 0,5 giây trước đó |
| `Crouch` (thanh chắn ngang đầu) | Cúi xuống | Đang cúi (hoặc vừa cúi trong 0,15 giây) |
| `DodgeLeft` | Né sang trái | Đứng lệch trái so với tâm ít nhất 0,5 m |
| `DodgeRight` | Né sang phải | Đứng lệch phải so với tâm ít nhất 0,5 m |
| `PunchWall` (bức tường) | Đấm | Có cú đấm khi tường đi vào tầm đấm (1,5 m) thì tường vỡ |

- Né được thì vật chuyển **xanh**, bị trúng thì chuyển **đỏ**. Có đếm số lần né được, số lần trúng và **combo** (chuỗi né liên tiếp).
- Nhân vật còn **bắt chước toàn bộ tư thế** của người chơi (tay, chân, thân, xoay người), không chỉ 5 động tác trên.
- Nhạc: người chơi chọn **file bất kỳ** (`.wav`, `.mp3`, `.ogg`). Hệ thống tự phân tích nhịp rồi sinh vật cản. Đoạn nhạc dồn dập thì vật cản dày và bay nhanh hơn, đoạn êm thì thưa và chậm hơn.

---

## 2. Kiến trúc tổng thể

Hai tiến trình chạy song song vì ước lượng tư thế chạy tốt nhất trong Python, còn game chạy trong Unity (C#). Chúng giao tiếp qua mạng cục bộ.

```
                    ┌────────────── Tiến trình Python (action_server.py) ──────────────┐
  Webcam ──────────►│ MediaPipe Pose ─► One Euro Filter ─► tính dịch chuyển + sự kiện   │
  (video, ~30 fps)  └───────────────────────────────┬───────────────────────────────────┘
                                                    │  UDP 127.0.0.1:5065, JSON (~0,5 KB/gói)
                                                    ▼
                    ┌────────────────────────────── Unity ─────────────────────────────┐
                    │ UdpActionReceiver (thread nền + hàng đợi)                         │
                    │    ├─► PoseRetargeter       : xoay xương Humanoid theo tư thế 3D  │
                    │    └─► PlayerActionController: di chuyển, nhảy, cúi, đấm, đá      │
                    │                                                                   │
  File nhạc ───────►│ SongSelector ─(gọi tiến trình con)─► beatmap_generator.py (librosa)│
  (âm thanh)        │       │                                   │                       │
                    │       │ nạp nhạc                          │ beatmap JSON          │
                    │       ▼                                   ▼                       │
                    │ RhythmConductor (đồng hồ bài nhạc) ─► ObstacleSpawner ─► Obstacle │
                    │                                          └──► ScoreManager        │
                    └───────────────────────────────────────────────────────────────────┘
```

**Các kỹ thuật đa phương tiện và vị trí trong mã nguồn**

| Chủ đề | Nội dung | File |
|---|---|---|
| Xử lý video / thị giác máy tính | Ước lượng tư thế người từ ảnh | `python/action_server.py` |
| Xử lý tín hiệu thời gian thực | Lọc nhiễu bằng One Euro Filter | `python/action_server.py` |
| Truyền dữ liệu / streaming | UDP, JSON, thread nhận dữ liệu | `action_server.py`, `UdpActionReceiver.cs` |
| Đồ hoạ 3D | Dựng dáng người bằng xoay xương (retargeting) | `PoseRetargeter.cs` |
| Xử lý âm thanh | STFT, onset, dò nhịp, đặc trưng phổ | `python/beatmap_generator.py` |
| Sinh nội dung theo dữ liệu | Beatmap sinh từ đặc trưng âm thanh | `python/beatmap_generator.py` |
| Đồng bộ audio - visual | Đồng hồ bài nhạc dựa trên `dspTime` | `RhythmConductor.cs`, `Obstacle.cs` |
| Tương tác người - máy | Điều khiển bằng cơ thể, chấm theo thời gian | `ObstacleSpawner.cs`, `Obstacle.cs` |

---

## 3. Phần A - Xử lý video và ước lượng tư thế

### A.1 Ước lượng tư thế bằng MediaPipe Pose Landmarker

Mỗi khung hình từ webcam được đưa vào mô hình **MediaPipe Pose Landmarker** (kiến trúc BlazePose). Mỗi người cho ra **33 điểm mốc (landmark)** ở hai hệ toạ độ:

| Loại | Ý nghĩa | Dùng cho |
|---|---|---|
| **Toạ độ ảnh** (`pose_landmarks`) | $(x, y)$ chuẩn hoá về $[0,1]$ theo khung hình, gốc ở góc trên trái | Vị trí người trong khung (dịch chuyển, nhảy) |
| **Toạ độ thế giới** (`pose_world_landmarks`) | $(x, y, z)$ tính bằng **mét**, gốc ở **trung điểm hai hông** | Hướng các chi, nhận diện đấm/đá/cúi, dựng dáng nhân vật |

Hệ thống chỉ dùng 12 điểm: hai vai (11, 12), hai khuỷu (13, 14), hai cổ tay (15, 16), hai hông (23, 24), hai gối (25, 26), hai mắt cá chân (27, 28). Model chạy ở chế độ `VIDEO` (có dùng thông tin các khung hình trước để bám theo người), `num_poses = 1`, ngưỡng phát hiện và bám đều 0,5. Có 3 biến thể `lite`, `full`, `heavy` (mặc định `full`); biến thể nặng hơn chính xác hơn nhưng chậm hơn.

> Vì toạ độ thế giới lấy hông làm gốc nên **hông luôn ở $(0,0,0)$**. Do đó nó không cho biết người đang đứng ở đâu trong phòng, chỉ cho biết hình dáng cơ thể. Vị trí trong khung hình phải lấy từ toạ độ ảnh (mục A.3).

### A.2 Làm mượt bằng One Euro Filter

Toạ độ do mô hình ước lượng bị rung giữa các khung hình. Làm mượt bằng bộ lọc thông thấp có tần số cắt **thay đổi theo tốc độ**: đứng yên thì lọc mạnh (hết rung), chuyển động nhanh thì lọc nhẹ (không bị trễ).

Với mỗi kênh dữ liệu, mẫu $x_t$ tại thời điểm $t$, $\Delta t = t - t_{\text{prev}}$:

$$\alpha(f_c) = \frac{1}{1 + \dfrac{\tau}{\Delta t}}, \qquad \tau = \frac{1}{2\pi f_c}$$

$$\dot{x}_t = \frac{x_t - \hat{x}_{t-1}}{\Delta t}, \qquad \hat{\dot{x}}_t = \alpha(f_d)\,\dot{x}_t + \bigl(1-\alpha(f_d)\bigr)\,\hat{\dot{x}}_{t-1}$$

$$f_c = f_{\min} + \beta \,\bigl|\hat{\dot{x}}_t\bigr|, \qquad \hat{x}_t = \alpha(f_c)\,x_t + \bigl(1-\alpha(f_c)\bigr)\,\hat{x}_{t-1}$$

Tham số: $f_{\min} = 0{,}8$ Hz, $\beta = 0{,}4$, $f_d = 1{,}0$ Hz. Tăng $\beta$ nếu thấy chuyển động bị trễ, giảm $f_{\min}$ nếu vẫn còn rung. Áp dụng cho 38 kênh: 12 khớp × 3 trục, cộng vị trí ngang của vai và bề rộng vai (dùng ở A.3).

### A.3 Ước lượng độ dịch chuyển từ camera đơn

Người chơi bước đi thật thì nhân vật cũng đi tương ứng. Dùng **độ dịch (delta) mỗi khung hình**, không dùng vận tốc theo trạng thái, nên đứng yên thì nhân vật đứng yên, không trôi.

Gọi $c_t$ là trung điểm hai vai theo trục ngang (toạ độ ảnh, đã lọc) và $w_t$ là bề rộng hai vai (toạ độ ảnh, đã lọc). Lấy bề rộng vai thật $W = 0{,}40$ m làm thước đo:

$$s_t = \frac{W}{w_t}$$

($s_t$ là số mét ứng với một đơn vị toạ độ ảnh tại độ sâu hiện tại.)

**Dịch ngang:**

$$\Delta x_t = (c_t - c_{t-1})\cdot s_t$$

**Dịch sâu (tiến/lùi).** Theo mô hình lỗ kim, bề rộng vai trên ảnh tỉ lệ nghịch với khoảng cách tới camera, nên $D \propto 1/w$. Do đó:

$$\Delta z_t = -W\left(\frac{1}{w_t} - \frac{1}{w_{t-1}}\right)$$

Dấu được chọn để lại gần camera ($w$ tăng) cho $\Delta z > 0$. Sau đó:

- **Vùng chết:** nếu $|\Delta| < 0{,}0015$ m thì đặt bằng 0 (chặn rung nhiễu cộng dồn thành trôi).
- **Chặn trên:** $|\Delta| \le 0{,}06$ m mỗi khung (tránh nhảy vọt khi mất dấu rồi bắt lại người).
- Nhân hệ số `--move-scale` (mặc định 1,0).

Phía Unity, `PlayerActionController` cộng dồn các $\Delta$ nhận được giữa hai khung hình rồi áp dụng một lần qua `CharacterController.Move`. Khi đang cúi, độ dịch nhân 0,5. Trục dọc dùng vật lý riêng:

$$v_y \leftarrow v_y + g\,\Delta t,\qquad p_y \leftarrow p_y + v_y\,\Delta t$$

với $g = -18$ m/s². Cú nhảy đặt $v_y = 6$ m/s khi đang chạm đất, cho độ cao $h = v^2/2|g| = 1{,}0$ m và thời gian trên không $2v/|g| \approx 0{,}67$ s.

> Đây là **ước lượng gần đúng** (chưa hiệu chuẩn camera). Hệ số tỉ lệ phụ thuộc tiêu cự và bề rộng vai thật của từng người, nên dùng `--move-scale` để chỉnh cảm giác cho vừa.

### A.4 Nhận diện sự kiện (nhảy, đấm, đá, cúi)

Dùng luật heuristic trên khoảng cách 3D thật (mét), chuẩn hoá theo **chiều dài thân** $L = \lVert \mathbf{M}_S - \mathbf{M}_H \rVert$ để không phụ thuộc chiều cao người chơi. Chỉ xét khớp có độ tin cậy (visibility) lớn hơn 0,5.

Ký hiệu: $\mathbf{M}_S$, $\mathbf{M}_H$ là trung điểm hai vai và hai hông; $\mathbf{S}$ vai, $\mathbf{W}$ cổ tay, $\mathbf{K}$ gối, $\mathbf{A}$ mắt cá chân; $y^{H}_t$ là toạ độ ảnh theo trục dọc của hông ở khung hình $t$; $L_{\text{img}}$ là chiều dài thân đo trên ảnh.

| Sự kiện | Công thức | Ngưỡng | Loại |
|---|---|---|---|
| **Cúi** | $\lVert \mathbf{K} - \mathbf{M}_H \rVert / L$ | $< 0{,}55$ | Trạng thái liên tục |
| **Đấm** | $\lVert \mathbf{W} - \mathbf{S} \rVert / L$ (mỗi tay) | $> 1{,}3$ | Sự kiện, cooldown 0,35 s |
| **Đá** | $\lVert \mathbf{A} - \mathbf{M}_H \rVert / L$ (mỗi chân) | $> 1{,}15$ | Sự kiện, cooldown 0,35 s |
| **Nhảy** | $\bigl(y^{H}_{t-4} - y^{H}_{t}\bigr) / L_{\text{img}}$ | $> 0{,}35$ | Sự kiện, cooldown 0,35 s |

Riêng **nhảy** dùng toạ độ **ảnh** (vì cần biết cả cơ thể có dịch lên trong khung hình hay không, mà toạ độ thế giới đã ghim hông ở gốc): lấy độ dịch lên của hông trong cửa sổ 5 khung hình, chia cho chiều dài thân trên ảnh.

### A.5 Dựng dáng nhân vật (retargeting)

Nhân vật là mô hình **Humanoid** của Unity. Với mỗi xương (ví dụ cánh tay trên = vai → khuỷu) ta tính hướng của chi người thật rồi xoay xương nhân vật sao cho cùng hướng.

Gọi $\mathbf{d}$ là vector từ khớp đầu đến khớp cuối (toạ độ thế giới, mét), đổi trục sang hệ Unity: $(x,y,z) \to (-d_x, -d_y, d_z)$ (có các cờ `flipX/Y/Z` để đảo nếu lệch). Chuẩn hoá được hướng mục tiêu $\hat{\mathbf{d}}$. Lúc khởi động, ghi lại **tư thế nghỉ** của từng xương: hướng $\hat{\mathbf{r}}$ từ xương sang xương con và góc xoay $R_{\text{rest}}$ (trong hệ của nhân vật). Mỗi khung hình:

$$\Delta R = \operatorname{FromToRotation}\bigl(\hat{\mathbf{r}},\ \hat{\mathbf{d}}_{\text{local}}\bigr), \qquad R^{\ast} = R_{\text{root}} \cdot \Delta R \cdot R_{\text{rest}}$$

$$R_t = \operatorname{Slerp}\bigl(R_{t-1},\ R^{\ast},\ 1 - e^{-k\,\Delta t}\bigr)$$

với $R^{\ast}$ là góc xoay đích và $k = 14$ (hệ số làm mượt theo thời gian, không phụ thuộc FPS). Cách này đúng cho mọi tư thế nghỉ (T-pose hay A-pose) vì chỉ tính góc quay **từ hướng nghỉ đến hướng mục tiêu**. Các xương được xử lý theo thứ tự cha trước con: thân (Spine), cánh tay trên, cẳng tay, đùi, cẳng chân. Khớp có độ tin cậy dưới 0,5 hoặc chi ngắn hơn 2 cm (thường là nhiễu) thì bị bỏ qua, xương giữ nguyên.

**Xoay thân (yaw).** Lấy vector từ vai trái ($\mathbf{p}_L$) sang vai phải ($\mathbf{p}_R$), chiếu lên mặt phẳng ngang:

$$\mathbf{r} = \operatorname{proj}_{xz}(\mathbf{p}_{R} - \mathbf{p}_{L}),\qquad \mathbf{f} = \hat{\mathbf{r}} \times \hat{\mathbf{y}}$$

Ở khung hình đầu có người, ghi $\mathbf{f}_0$ làm mốc 0°. Về sau, cả nhân vật xoay một góc bằng phần chênh lệch:

$$R_{\text{root}} = \operatorname{FromToRotation}(\mathbf{f}_0,\ \mathbf{f}) \cdot R_{\text{root}}^{(0)}$$

làm mượt bằng Slerp với $k = 8$. Xoay thân được tính **trước** các chi trong cùng khung hình, vì hướng chi được quy đổi về hệ toạ độ cục bộ của nhân vật đang xoay.

---

## 4. Phần B - Truyền dữ liệu thời gian thực (streaming)

### B.1 Vì sao chọn UDP

| Tiêu chí | UDP (đang dùng) | TCP |
|---|---|---|
| Độ trễ | Thấp, gửi là đi | Cao hơn (bắt tay, gửi lại gói mất, chặn đầu hàng đợi) |
| Gói bị mất | Bỏ qua, gói sau thay thế | Chờ gửi lại làm trễ cả các gói sau |
| Phù hợp | Dữ liệu "trạng thái mới nhất" quan trọng hơn dữ liệu cũ | Dữ liệu bắt buộc đủ và đúng thứ tự |

Tư thế là dữ liệu kiểu "càng mới càng đúng": mất 1 gói thì gói kế tiếp (33 ms sau) đã có tư thế mới, chờ gửi lại gói cũ chỉ làm trễ thêm. Vì vậy chọn UDP trên `127.0.0.1:5065` (cùng máy).

### B.2 Định dạng gói tin

Mỗi khung hình camera gửi một gói JSON (UTF-8):

```jsonc
{
  "ts": 1760000000.123,
  "kpts": [x0,y0,z0,vis0, x1,y1,z1,vis1, ... ],   // 12 khớp x 4 số = 48 số, mét, gốc ở hông
  "move_dx": 0.00312,                              // mét, độ dịch ngang trong khung này
  "move_dz": -0.00041,                             // mét, độ dịch sâu trong khung này
  "crouch": false,                                 // trạng thái liên tục
  "events": ["punch"]                              // sự kiện rời rạc: "jump" | "punch" | "kick" (thường rỗng)
}
```

Thứ tự 12 khớp trong `kpts` cố định: vai T, vai P, khuỷu T, khuỷu P, cổ tay T, cổ tay P, hông T, hông P, gối T, gối P, mắt cá T, mắt cá P (T = trái, P = phải). Mảng `kpts` **rỗng** nghĩa là không thấy người; Unity giữ nguyên tư thế cuối.

**Băng thông:** một gói mẫu nặng khoảng **0,5 KB**; ở ~30 gói/giây là cỡ **15 KB/s** (khoảng 120 kbit/s), rất nhỏ so với khả năng của mạng cục bộ. Dạng JSON dễ đọc và dễ gỡ lỗi; nếu cần giảm thêm, có thể chuyển sang nhị phân (48 số `float16` chỉ còn khoảng 100 byte).

### B.3 Nhận dữ liệu bên Unity

```
Thread nền:  UdpClient.Receive() ─► JsonUtility.FromJson ─► ConcurrentQueue<ActionPacket>
Main thread: Update() lấy hết hàng đợi ─► phát sự kiện OnActionReceived ─► PoseRetargeter, PlayerActionController
```

- Nhận ở **thread nền** để không chặn vòng lặp game; đẩy qua hàng đợi an toàn đa luồng rồi xử lý ở **main thread**, vì API của Unity chỉ gọi được từ main thread.
- Socket bật `ReuseAddress` để mở lại cổng nhanh sau khi dừng và chạy lại trong Editor.
- Mỗi gói được xử lý riêng; nếu Unity chậm hơn camera thì các $\Delta$ dịch chuyển được cộng dồn giữa hai khung hình (mục A.3), không bị bỏ.

### B.4 Luồng dữ liệu thứ hai: tiến trình phân tích nhạc

Khi người chơi chọn bài, Unity chạy `beatmap_generator.py` như **tiến trình con**. Script in các dòng `[STAGE] <bước đang làm>` ra đầu ra chuẩn; Unity đọc bất đồng bộ qua pipe (`OutputDataReceived`) để hiện tiến độ. Lỗi được báo bằng dòng `[ERROR] ...` trên đầu ra lỗi. Mã hoá được ép UTF-8 hai phía để tiếng Việt không vỡ khi chạy trên Windows.

---

## 5. Phần C - Xử lý âm thanh và sinh beatmap

Phân tích nhạc chạy **offline** bằng `librosa` (mỗi bài một lần, trước khi chơi), không phân tích lúc đang chơi. Cách này chính xác hơn vì có thể nhìn cả bài, và không tốn tài nguyên trong lúc game đang chạy.

```
File nhạc ─► mono 22,05 kHz ─► STFT ─► onset strength ─► dò nhịp (DP) ─► chọn pha + canh beat theo bass
                                  │                                              │
                                  └──► RMS, độ chói (centroid) ──► đường cong cường độ ──► chọn mốc + chọn loại ──► beatmap.json
```

### C.1 Tiền xử lý

Đọc file, đổi về **mono**, lấy mẫu lại **22 050 Hz**. Dùng STFT với cửa sổ 2048 mẫu và bước nhảy (hop) **256 mẫu**, tức mỗi khung cách nhau $256/22050 \approx 11{,}6$ ms.

### C.2 Onset strength (độ tăng năng lượng phổ)

Nhịp là những thời điểm năng lượng phổ tăng đột ngột. Tính phổ Mel $S(m,t)$ ($M$ dải), đổi sang dB, rồi lấy phần tăng dương giữa hai khung liên tiếp, trung bình các dải:

$$O(t) = \frac{1}{M}\sum_{m=1}^{M} \max\bigl(0,\ S_{\text{dB}}(m,t) - S_{\text{dB}}(m,t-1)\bigr)$$

Đỉnh của $O(t)$ trùng với tiếng trống, nốt đàn bật mạnh...

### C.3 Dò nhịp bằng quy hoạch động

Ước lượng tempo bằng **tự tương quan** của $O(t)$ (độ lệch $\ell$ khung có tự tương quan cao nhất, có trọng số ưu tiên quanh 120 BPM):

$$\text{BPM} = \frac{60\cdot sr}{hop\cdot \ell}$$

Rồi tìm chuỗi thời điểm $\{t_i\}$ **bám theo các đỉnh onset nhưng đều theo chu kỳ** (thuật toán Ellis, 2007) bằng cách cực đại hoá:

$$C(\{t_i\}) = \sum_{i=1}^{N} O(t_i) \;-\; \lambda \sum_{i=2}^{N} \Bigl(\ln\frac{t_i - t_{i-1}}{\tau}\Bigr)^2, \qquad \tau = \frac{60}{\text{BPM}},\ \lambda = 100$$

Số hạng đầu thưởng việc đặt beat ở đỉnh onset, số hạng sau phạt việc khoảng cách giữa hai beat lệch khỏi chu kỳ $\tau$. Cực đại hoá bằng quy hoạch động:

$$C^*(t) = O(t) + \max_{t'}\Bigl[\,C^*(t') - \lambda\bigl(\ln\tfrac{t-t'}{\tau}\bigr)^2\Bigr]$$

rồi truy ngược để lấy chuỗi beat. Mình tắt `trim` để giữ lại beat ở đoạn intro nhỏ (mặc định của thư viện cắt bỏ các beat đầu bài có onset yếu).

**Vì sao dùng `hop = 256` thay vì 512?** Tempo chỉ lấy được ở các giá trị rời rạc theo $\ell$ nguyên. Với $hop=512$ và 120 BPM, hai giá trị gần nhất là $\ell=21 \to 123{,}0$ BPM và $\ell=22 \to 117{,}4$ BPM (cách nhau khoảng 5,6 BPM). Ước lượng 117,4 khiến khoảng cách beat là 0,511 s thay vì 0,500 s, nên beat **trôi dần** khỏi nhịp thật theo thời gian. Hop nhỏ hơn cho phân giải mịn hơn, và BPM cuối cùng còn được tinh chỉnh lại từ chính các beat:

$$\text{BPM}_{\text{final}} = \frac{60}{\operatorname{median}(t_i - t_{i-1})}$$

### C.4 Chọn đúng pha và canh beat theo trống bass

Dò nhịp có thể **nhầm pha**: đúng chu kỳ nhưng lệch nửa nhịp (bám vào tiếng hi-hat trái nhịp thay vì trống bass). Cách sửa dựa trên tiếng bass:

$$L(t) = \sum_{f < 150\,\text{Hz}} |X(f,t)|, \qquad b(t) = \max\bigl(0,\ L(t) - L(t-1)\bigr)$$

Với lưới beat $T=\{t_i\}$ và chu kỳ $P = \operatorname{median}(t_i - t_{i-1})$, định nghĩa điểm lưới $G(T)$ là trung bình của $b$ tại các beat (lấy đỉnh trong $\pm 50$ ms). **Nếu** lưới lệch nửa nhịp có điểm cao rõ rệt:

$$G(T + P/2) > 1{,}3\cdot G(T) \;\Rightarrow\; T \leftarrow T + P/2$$

Cuối cùng canh từng beat vào **đỉnh bass gần nhất** trong $\pm\min(60\ \text{ms},\ 0{,}2P)$ (chỉ canh nếu đỉnh đủ lớn), để loại độ trễ cố định của thuật toán.

### C.5 Đường cong cường độ

Để nhịp độ vật cản bám theo nhạc, dựng đường cong **cường độ** $I(t)\in[0,1]$ tại mỗi beat, kết hợp **độ to** và **độ dày/dồn dập** của nhịp. Tại beat $t_i$:

- $A_i$: độ to (RMS) trung bình trong cửa sổ $[t_i - 1,\ t_i + 1]$ giây.
- $B_i$: onset strength trung bình trong cùng cửa sổ.

Chuẩn hoá từng đặc trưng theo từng bài (phân vị 10% và 90%):

$$z = \operatorname{clip}\Bigl(\frac{x - P_{10}}{P_{90} - P_{10}},\ 0,\ 1\Bigr), \qquad c = \operatorname{clip}\Bigl(\frac{(P_{90}-P_{10})/(P_{90}+P_{10})}{0{,}30},\ 0{,}35,\ 1\Bigr), \qquad N(x) = 0{,}5 + (z - 0{,}5)\,c$$

Hệ số tương phản $c$ làm biên độ **co lại** khi bài ít thay đổi to/nhỏ, nên nhạc đều đều cho nhịp độ vật cản đều đều thay vì bị phóng đại. Rồi:

$$I_{\text{raw}}(t_i) = 0{,}55\,N(A_i) + 0{,}45\,N(B_i), \qquad I = I_{\text{raw}} * h_9$$

trong đó $h_9$ là cửa sổ Hann dài 9 beat (khoảng 2 ô nhịp), chuẩn hoá tổng bằng 1, có đệm biên để hai đầu bài không bị tụt.

### C.6 Nhịp độ: khoảng cách giữa hai vật cản

Khoảng cách tới vật cản kế tiếp co giãn **liên tục** theo cường độ (đoạn êm thì thưa, đoạn dồn dập thì dày):

$$g(t) = \max\!\Bigl(f,\ \bigl[g_{\max} - (g_{\max} - g_{\min})\,I(t)\bigr]\cdot\frac{\kappa}{\text{pace}}\Bigr), \qquad \kappa = \operatorname{clip}\Bigl(\sqrt{100/\text{BPM}},\ 0{,}75,\ 1{,}25\Bigr)$$

| Độ khó | $g_{\min}$ (s) | $g_{\max}$ (s) | Sàn $f$ (s) |
|---|---|---|---|
| `easy` | 1,8 | 4,2 | 1,4 |
| `normal` | 1,2 | 3,4 | 1,0 |
| `hard` | 0,85 | 2,2 | 0,8 |

- $\kappa$: nhạc nhanh thì nhịp độ nhanh hơn một chút.
- Sàn $f$: người chơi qua webcam có độ trễ, cần đủ thời gian làm xong động tác trước khi vật kế tiếp tới.
- Có nhìn trước nửa khoảng để vào đoạn mới thì đổi nhịp kịp.

Từ vật cản ở $t_k$, mốc kế tiếp là **beat gần nhất** với mục tiêu $t^{\ast}$ (vật luôn rơi đúng vào một beat), với điều kiện cách $t_k$ ít nhất $f$ giây:

$$g_0 = g(t_k),\quad g_1 = g\bigl(t_k + \tfrac{1}{2}g_0\bigr),\quad t^{\ast} = t_k + \tfrac{1}{2}(g_0 + g_1)$$

Vật đầu tiên không sớm hơn 4 giây, và không sinh vật trong 2 giây cuối bài.

**Tốc độ bay** của vật cũng đổi theo cường độ: $m = 0{,}85 + 0{,}40\,I$ (từ 0,85 đến 1,25 lần tốc độ gốc). Vật nhanh được sinh ở xa hơn nên **vẫn chạm đúng giờ**, chỉ khác độ "lao tới" nhìn thấy.

### C.7 Chọn loại vật cản

Tại mỗi mốc đã chọn, lấy đặc trưng trong cửa sổ quanh mốc: độ to $\ell$ (RMS, $\pm0{,}2$ s), độ chói $b$ (spectral centroid, $\pm0{,}2$ s), độ dứt khoát $a$ (onset lớn nhất, $\pm0{,}08$ s). Mỗi đặc trưng được đổi sang **thứ hạng phần trăm** $\in[0,1]$ giữa **chính các mốc đã chọn**, nên luôn trải đều dù các beat được chọn nghe giống nhau (xem bài học ở mục 11).

**Mức "hợp" âm thanh của từng loại:**

$$A_{\text{Punch}} = 0{,}55\,\ell + 0{,}45\,a \qquad A_{\text{Jump}} = 0{,}55\,b + 0{,}45\,a \qquad A_{\text{Crouch}} = 0{,}60\,(1-\ell) + 0{,}40\,(1-b)$$

$$m = 1 - 2\,\lvert b - 0{,}5\rvert, \qquad A_{\text{Left}} = m\,(0{,}6 + 0{,}4\,[\Delta b < 0]), \qquad A_{\text{Right}} = m\,(0{,}6 + 0{,}4\,[\Delta b > 0])$$

Ý nghĩa: tường hợp với nhịp to và dứt khoát; nhảy hợp với âm chói và dứt khoát; cúi hợp với đoạn nhỏ và trầm; né trái/phải hợp với âm sắc trung bình, và né phải khi độ chói đang **tăng** (giai điệu đi lên), né trái khi đang **giảm**.

**Điểm cuối cùng** của loại $\tau$, kèm các cơ chế đa dạng:

$$S_\tau = 1{,}5\,A_\tau + \varepsilon - 0{,}30\,n_{\text{recent}}(\tau) - 0{,}50\,[\tau = \tau_{\text{last}}] - 0{,}45\,\Gamma_\tau + 0{,}5\,(p^*_\tau - \text{share}_\tau)$$

- $\varepsilon \sim \mathcal{N}(0,\ 0{,}20^2)$: nhiễu có **hạt giống cố định** (`--seed`, mặc định suy từ tên file và độ dài bài), nên cùng bài cho cùng kết quả, đổi seed thì ra kiểu sắp xếp khác.
- $n_{\text{recent}}$: số lần loại $\tau$ xuất hiện trong 4 lần chọn gần nhất (phạt dùng lặp); $\tau_{\text{last}}$ là loại vừa chọn ngay trước đó; $\text{share}_\tau$ là tỉ lệ loại $\tau$ đã dùng từ đầu bài. Tường còn bị phạt thêm 0,80 nếu vừa xuất hiện trong 2 lần gần nhất.
- $\Gamma_\tau$: số chu kỳ lặp $k\in\{2,\dots,6\}$ mà việc chọn $\tau$ sẽ **tiếp tục** (vừa chọn loại giống $k$ nấc trước và $\tau$ trùng loại mà chu kỳ đó "mong đợi"). Đây là cơ chế phá lặp tuần tự.
- $p^*$: tỉ lệ mục tiêu (0,21 cho Jump, Crouch, DodgeLeft, DodgeRight; 0,16 cho PunchWall), thưởng cho loại đang bị dùng ít.

Loại được chọn là $\arg\max_\tau S_\tau$.

### C.8 Định dạng beatmap

```json
{
  "audio_file": "bai_hat.mp3",
  "bpm": 99.4,
  "duration": 194.0,
  "difficulty": "normal",
  "analysis_seconds": 2.8,
  "seed": 157625507,
  "pace": 1.0,
  "beats": [0.62, 1.22, 1.82, ...],
  "obstacles": [
    {"time": 4.226, "type": "Crouch", "energy": 0.12, "brightness": 0.31,
     "intensity": 0.085, "speed_mult": 0.884},
    ...
  ]
}
```

`time` là thời điểm (giây trong bài) mà vật phải chạm người chơi. Mảng `beats` dùng để nháy hình theo nhịp.

---

## 6. Phần D - Đồng bộ âm thanh và hình ảnh

Vật cản phải chạm người chơi đúng vào nhịp mà tai nghe được, nên cần một **đồng hồ bài nhạc** đáng tin cậy.

### D.1 Đồng hồ bài nhạc

Dùng `AudioSettings.dspTime` (đồng hồ của bộ xử lý âm thanh, ổn định hơn `Time.time` vì chạy theo bộ đệm âm thanh) làm gốc. Khi bắt đầu, lên lịch phát nhạc ở thời điểm $t_0 = \text{dspTime}_{\text{now}} + (\text{leadTime} + 0{,}5)$ để vật cản đầu tiên kịp bay tới. Thời gian bài nhạc:

$$t_{\text{dsp}}(f) = \text{dspTime}(f) - t_0 - \delta_{\text{audio}}$$

($\delta_{\text{audio}}$ là độ lệch bù trễ phát âm thanh, chỉnh trong Inspector). Vì `dspTime` cập nhật theo bước của bộ đệm nên có thể giật nhẹ giữa các khung hình; làm mượt bằng nội suy:

$$\tilde{t}_f = 0{,}8\,(\tilde{t}_{f-1} + \Delta t_f) + 0{,}2\;t_{\text{dsp}}(f)$$

Phần $\tilde{t}_{f-1} + \Delta t_f$ cho độ mượt giữa các khung hình, phần $t_{\text{dsp}}$ kéo về đồng hồ âm thanh nên không bị lệch dần.

### D.2 Vị trí vật cản tính từ đồng hồ

Thay vì cộng dồn `vị trí += tốc độ × deltaTime` (sai số cộng dồn, lệch dần so với nhạc), vị trí được **tính lại mỗi khung hình từ đồng hồ**:

$$z_i(\tilde{t}) = z_{\text{player}} + v_i\,\bigl(t_{\text{hit},i} - \tilde{t}\bigr), \qquad v_i = v_0\,m_i,\ v_0 = 6\ \text{m/s}$$

Nên đúng $\tilde{t} = t_{\text{hit}}$ thì vật nằm chính xác tại người chơi, không có sai số tích luỹ.

```
t_chạm - 3,0 s            t_chạm               t_chạm + 0,15 s
      │  sinh vật cản       │  vật tới người chơi   │  chấm điểm
      ├─────────────────────┼───────────────────────┤
          vật bay v_i · 3 s       (bù độ trễ 0,15 s)
```

Ở đây $t_{\text{hit}}$ là thời điểm vật chạm người chơi (trường `time` trong beatmap). Vật được sinh khi $\tilde{t} \ge t_{\text{hit}} - \text{leadTime}$ (leadTime = 3 s), tức ở cách người chơi $3\,v_i$ mét.

### D.3 Bù độ trễ

Từ lúc người chơi cử động đến lúc Unity biết có độ trễ do: khung hình camera (~33 ms ở 30 fps), suy luận mô hình, bộ lọc One Euro, truyền UDP và khung hình Unity. Tổng cộng cỡ **vài chục đến vài trăm ms** (chưa đo chính xác trên máy thật). Để không phạt oan người chơi, chấm điểm ở $t_{\text{hit}} + 0{,}15$ s (`judgeLatency`).

---

## 7. Phần E - Chấm điểm va chạm

Không dùng Collider/vật lý; chấm hoàn toàn bằng logic dựa trên trạng thái nhân vật do `PlayerActionController` ghi lại (`IsGrounded`, `IsCrouching`, thời điểm nhảy/đấm/cúi gần nhất). Chấm bằng logic dễ canh thời gian theo nhịp hơn vật lý, và không xung đột với `CharacterController`.

| Loại | Thời điểm chấm | Điều kiện đúng |
|---|---|---|
| `Jump` | $t_{\text{hit}} + 0{,}15$ | `!IsGrounded` **hoặc** $t_{\text{now}} - t_{\text{jump}} \le 0{,}5$ |
| `Crouch` | $t_{\text{hit}} + 0{,}15$ | `IsCrouching` **hoặc** $t_{\text{now}} - t_{\text{crouch}} \le 0{,}15$ |
| `DodgeLeft` | $t_{\text{hit}} + 0{,}15$ | $x_{\text{player}} - x_{\text{center}} \le -0{,}5$ |
| `DodgeRight` | $t_{\text{hit}} + 0{,}15$ | $x_{\text{player}} - x_{\text{center}} \ge +0{,}5$ |
| `PunchWall` | Liên tục khi tường trong tầm đấm | Có cú đấm sau thời điểm tường vào tầm (cho phép sớm tối đa 0,25 s); nếu tường trôi qua mà chưa đấm thì trúng |

- Ký hiệu: $t_{\text{now}}$ là đồng hồ hiện tại; $t_{\text{jump}}$, $t_{\text{crouch}}$ là lần nhảy/cúi gần nhất; $x_{\text{player}}$ là vị trí ngang của nhân vật; $x_{\text{center}}$ là tâm, chốt khi nhân vật chạm đất lần đầu.
- Tầm đấm tính theo **thời gian** ($1{,}5\ \text{m} / v_0 = 0{,}25$ s) với tốc độ gốc, nên cửa sổ để đấm công bằng dù vật bay nhanh hay chậm.
- Nếu "né trái" bị ngược so với màn hình (do hướng camera game), bật `Invert Left Right` trên `ObstacleSpawner`.

---

## 8. Chọn nhạc lúc chạy

`SongSelector` cho người chơi chọn file nhạc bất kỳ, rồi tự động:

1. Chạy `beatmap_generator.py` như tiến trình con, ghi beatmap vào thư mục tạm (`Application.temporaryCachePath/beatmap_current.json`). **Không lưu cache**: mỗi lần chọn bài là phân tích lại.
2. Nạp file nhạc bằng `UnityWebRequestMultimedia` (đường dẫn được mã hoá URI nên chịu được khoảng trắng và tiếng Việt).
3. Đưa nhạc và beatmap cho `RhythmConductor` bắt đầu chơi.

Chọn bài bằng cách bấm vào bài trong thư mục `Songs/` (cạnh thư mục `Assets`) hoặc dán đường dẫn đầy đủ. Hỗ trợ `.wav`, `.mp3`, `.ogg`. Có 3 mức khó. Tên file nhạc phải trùng tên đã đưa cho Python; `RhythmConductor` cảnh báo nếu nạp nhầm bài (sẽ lệch hoàn toàn). Quá 300 giây phân tích thì tự huỷ.

---

## 9. Cài đặt và chạy

### 9.1 Môi trường Python

> **MediaPipe chưa hỗ trợ Python 3.13.** Dùng Python **3.11 hoặc 3.12** trong môi trường ảo (venv).

```bash
cd python
py -3.11 -m venv venv_mediapipe          # hoặc py -3.12
venv_mediapipe\Scripts\activate
pip install -r requirements.txt
```

### 9.2 Chạy phần nhận diện tư thế

```bash
python action_server.py --show --variant full
```

Các tuỳ chọn hay dùng: `--cam 1` (đổi camera), `--backend dshow` (nếu báo lỗi `grabFrame` trên Windows), `--variant lite` (máy yếu), `--mincutoff`, `--beta` (bộ lọc), `--move-scale` (độ nhạy dịch chuyển).

### 9.3 Cài đặt trong Unity (Unity 6)

| GameObject | Component cần gắn | Ghi chú |
|---|---|---|
| Nhân vật (Humanoid + Animator) | `CharacterController`, `PlayerActionController`, `PoseRetargeter` | Kéo `NetworkManager` vào ô **Receiver**; Rig phải là **Humanoid** |
| `NetworkManager` (Empty) | `UdpActionReceiver` | Cổng 5065 |
| `GameManager` (Empty) | `ObstacleSpawner`, `ScoreManager`, `RhythmConductor`, `SongSelector` | `ObstacleSpawner`: kéo nhân vật vào **Player**; `RhythmConductor`: kéo `GameManager` vào **Spawner** |
| Mặt sàn | Plane/Terrain có Collider | Để nhân vật không rơi |

Trên `SongSelector` điền **Python Exe** (python trong venv, ví dụ `...\venv_mediapipe\Scripts\python.exe`) và **Script Path** (đường dẫn đầy đủ tới `beatmap_generator.py`). Để **trống** ô Music và Beatmap Json trên `RhythmConductor` (nếu điền, nó sẽ tự chạy bài đó thay vì hiện màn chọn bài). Nếu nút trên màn hình không bấm được, vào *Project Settings → Player → Active Input Handling* chọn **Both**.

### 9.4 Chạy

1. Chạy `action_server.py` (mục 9.2).
2. Bấm Play trong Unity. Màn hình **CHỌN BÀI HÁT** hiện lên; chọn bài và độ khó.
3. Đợi phân tích (vài giây), nhạc sẽ bắt đầu sau khoảng 3,5 giây chuẩn bị. Đứng trước camera và chơi.

### 9.5 Tạo beatmap thủ công (không cần Unity)

```bash
python beatmap_generator.py bai_hat.wav --difficulty normal --plot phan_tich.png
python beatmap_generator.py bai_hat.wav --pace 1.3 --seed 7    # dày hơn, kiểu sắp xếp khác
```

`--plot` xuất biểu đồ gồm dạng sóng, beat, vị trí vật cản và đường cường độ, tiện đưa vào báo cáo.

### 9.6 Cấu trúc thư mục

```
unity-yolo-game/
├── README.md
├── python/
│   ├── action_server.py          # webcam -> MediaPipe -> lọc -> UDP
│   ├── beatmap_generator.py      # nhạc -> beat -> beatmap JSON
│   ├── requirements.txt
│   └── tools/                    # công cụ đo cho báo cáo (độ đa dạng, nhịp độ, bài nhạc thử)
├── UnityScripts/                 # copy vào Assets/
│   ├── UdpActionReceiver.cs      # nhận UDP (thread nền + hàng đợi)
│   ├── PoseRetargeter.cs         # xoay xương theo tư thế 3D, xoay thân
│   ├── PlayerActionController.cs # di chuyển, nhảy, cúi, đấm, đá
│   ├── RhythmConductor.cs        # đồng hồ bài nhạc, sinh vật cản theo beatmap
│   ├── ObstacleSpawner.cs        # sinh vật cản (khối placeholder hoặc prefab)
│   ├── Obstacle.cs               # chuyển động + chấm điểm từng vật
│   ├── ObstacleType.cs           # 5 loại vật cản
│   ├── ScoreManager.cs           # điểm, combo
│   └── SongSelector.cs           # màn hình chọn bài, gọi Python, nạp nhạc
├── Songs/                        # nhạc thử
└── samples/                      # beatmap và biểu đồ phân tích mẫu
```

---

## 10. Bảng tham số tinh chỉnh

| Muốn chỉnh | Tham số | Nơi chỉnh |
|---|---|---|
| Còn rung khi đứng yên | `--mincutoff` giảm (mặc định 0,8) | `action_server.py` |
| Chuyển động bị trễ | `--beta` tăng (mặc định 0,4) | `action_server.py` |
| Nhân vật đi quá nhanh/chậm | `--move-scale` | `action_server.py` |
| Đấm/đá/cúi quá dễ hoặc khó | Ngưỡng 1,3 / 1,15 / 0,55 trong `EventClassifier` | `action_server.py` |
| Nhân vật giơ nhầm tay hoặc ngược chiều | `Flip X/Y/Z` | `PoseRetargeter` |
| Nhân vật đi ngược chiều | `Flip X/Z` | `PlayerActionController` |
| Vật cản dày/thưa toàn bài | `--pace` (> 1 dày hơn) | `beatmap_generator.py` |
| Chênh lệch nhịp độ êm/dồn dập | Bảng `DIFFICULTY` ($g_{\min}, g_{\max}, f$) | `beatmap_generator.py` |
| Cân bằng "bám nhạc" và "đa dạng" | `TYPE_PARAMS` | `beatmap_generator.py` |
| Kiểu sắp xếp loại vật cản | `--seed` | `beatmap_generator.py` |
| Vật bay nhanh/chậm | `speed`, `Speed Follows Music` | `ObstacleSpawner` |
| Thời gian nhìn thấy vật trước khi tới | `leadTime` | `ObstacleSpawner` |
| Cảm giác bị trúng oan | `Judge Latency`, `Jump Window`, `Dodge Threshold` | `ObstacleSpawner` |
| Nhạc lệch vật cản | `Audio Offset` | `RhythmConductor` |
| Thân xoay quá chậm/nhanh | `Root Rotation Smoothing` | `PoseRetargeter` |

---

## 11. Kết quả kiểm thử

> **Lưu ý:** các số dưới đây đo trên **bài nhạc tổng hợp** (do chương trình tự tạo, có nhịp biết trước, có đoạn intro/verse/chorus/outro), chạy trên môi trường thử nghiệm chứ không phải máy chơi game. Nhạc thật có thể cho kết quả khác. Công cụ để lặp lại các phép đo nằm trong `python/tools/`.

**Độ chính xác dò nhịp** (3 mức BPM: 97, 120, 140; có và không có hi-hat trái nhịp):

| | Trước khi sửa (`hop=512`, chưa chọn pha) | Sau khi sửa (`hop=256`, chọn pha, canh theo bass) |
|---|---|---|
| Khoảng cách beat (thật 0,500 s) | 0,511 s (BPM ước lượng 117,5) | 0,499–0,500 s |
| Độ lệch so với tiếng trống bass | trung bình khoảng 195–290 ms (xấp xỉ nửa nhịp, tuỳ BPM) | trung bình **5 ms**, lớn nhất 10–23 ms |
| Số beat dò được | bỏ mất toàn bộ beat ở đoạn intro nhỏ (do `trim` mặc định) | gần đủ (96–97 trên 97 ở bài 97 BPM; 119–120 trên 120 ở bài 120 BPM) |

**Độ đa dạng loại vật cản** (3 bài × 3 mức khó):

| | Bản đầu (xếp theo ngưỡng cố định) | Bản hiện tại |
|---|---|---|
| Số loại xuất hiện | 4–5 loại, nhưng lệch nặng (mức Khó: 2 loại chiếm ~77%) | **đủ 5/5 loại**, mỗi loại khoảng 15–25% |
| Lặp theo chu kỳ (mức ngẫu nhiên ≈ 20%) | khoảng 37–61% | khoảng 22–28% |
| Chuỗi cùng loại liên tiếp dài nhất | 2 | 1–2 |

Nguyên nhân lỗi cũ: chỉ lấy đặc trưng tại đúng các beat được chọn (cứ 2–4 beat một lần), mà các beat đó cùng rơi vào một vị trí trong ô nhịp nên nghe gần như giống nhau, dẫn tới cùng một loại. Bước "chống lặp" xoay vòng cố định lại tự tạo ra chu kỳ.

**Nhịp độ bám nhạc** (mức Vừa, bài 100 BPM; mật độ đo bằng số vật cản mỗi phút):

| | Trước | Sau |
|---|---|---|
| Khoảng cách giữa hai vật cản | 1,79–2,42 s (gần như cố định) | 1,2–3,0 s (đổi theo nhạc) |
| Mật độ verse / điệp khúc | 25 / 33 vật/phút | 24 / 46 vật/phút |
| Tương quan mật độ và độ to (3 bài × 3 mức khó) | $r = 0{,}37$ đến $0{,}88$ | $r = 0{,}88$ đến $0{,}95$ |

**Thời gian phân tích:** khoảng 3 giây cho 60 giây nhạc (gồm cả khởi động Python và nạp `librosa`) trên môi trường thử nghiệm; bài 3–4 phút ước chừng 10–15 giây (suy ra, chưa đo).

---

## 12. Hạn chế đã biết

**Về video / tư thế**
- Ước lượng từ **camera đơn**: độ sâu từ mô hình là ước lượng học được, và độ dịch tiến/lùi suy ra từ bề rộng vai chỉ là **gần đúng** (chưa hiệu chuẩn camera).
- Mỗi khung hình chỉ gửi **độ dịch** ($\Delta x, \Delta z$). Nếu một gói UDP bị mất thì độ dịch của khung đó mất theo, và vị trí nhân vật có thể **lệch dần** so với vị trí thật. Trên cùng máy (localhost) việc mất gói rất hiếm; hướng cải tiến là gửi **vị trí tích luỹ** thay vì độ dịch.
- Chưa mô phỏng được xoay dọc trục xương (xoay cẳng tay, cổ tay); bàn tay, bàn chân và đầu chưa được điều khiển.
- Chỉ nhận diện **một người**. Ngưỡng nhận diện đấm/đá/cúi/nhảy là cố định, chưa hiệu chỉnh theo từng người.
- Gói tin có trường `ts` (thời điểm gửi) nhưng hiện **chưa dùng** để đồng bộ hay đo độ trễ.

**Về âm thanh**
- Dò nhịp giả định tempo gần đều. Nhạc đổi nhịp liên tục hoặc không có trống rõ có thể dò kém; một lỗi phổ biến là ra BPM gấp đôi hoặc một nửa.
- Chưa dò được **ô nhịp (downbeat)**; vật cản rơi vào beat bất kỳ chứ chưa ưu tiên nhịp mạnh.
- File `.mp3` có độ trễ giải mã cố định tuỳ bộ mã hoá; bù bằng `Audio Offset`.
- Loại vật cản chọn theo đặc trưng âm sắc/độ to; chưa xét giai điệu, hoà âm hay lời.

**Về hệ thống**
- Giao diện chọn bài dùng IMGUI nên đơn giản; có thể thay bằng UI Canvas.
- Độ trễ từ cử động đến phản hồi trong game chưa được đo thực tế; `Judge Latency = 0,15 s` là giá trị ước lượng.

**Hướng phát triển:** hiệu chỉnh camera, gửi vị trí tích luỹ, ngưỡng thích nghi theo từng người, dò downbeat, lưu cache beatmap, giao diện Canvas.

---

## 13. Tài liệu tham khảo

1. Casiez, G., Roussel, N., Vogel, D. *1€ Filter: A Simple Speed-based Low-pass Filter for Noisy Input in Interactive Systems.* CHI 2012.
2. Ellis, D. P. W. *Beat Tracking by Dynamic Programming.* Journal of New Music Research, 36(1), 2007.
3. Bazarevsky, V. et al. *BlazePose: On-device Real-time Body Pose Tracking.* arXiv:2006.10204, 2020.
4. McFee, B. et al. *librosa: Audio and Music Signal Analysis in Python.* Proceedings of the 14th Python in Science Conference, 2015.
5. Google. *MediaPipe Pose Landmarker.* https://ai.google.dev/edge/mediapipe/solutions/vision/pose_landmarker
6. Unity Technologies. *AudioSettings.dspTime*, *UnityWebRequestMultimedia*, *CharacterController* (Unity Manual / Scripting API).
