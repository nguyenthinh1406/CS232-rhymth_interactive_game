// RhythmConductor.cs
// Đọc beatmap (JSON do beatmap_generator.py sinh ra), phát nhạc, và sinh chướng ngại vật
// sao cho mỗi vật chạm người chơi ĐÚNG vào thời điểm đã tách từ nhạc.
//
// Đồng bộ: dùng AudioSettings.dspTime (đồng hồ của bộ xử lý âm thanh, ổn định hơn Time.time)
// làm gốc, rồi làm mượt giữa các frame để chuyển động không bị giật theo kích thước buffer âm thanh.
//   SongTime = 0   : lúc nhạc thật sự bắt đầu phát
//   SongTime < 0   : giai đoạn "chuẩn bị" trước khi nhạc phát (để vật cản đầu tiên kịp bay tới)
//
// Cách gắn: GameObject "GameManager" -> Add Component RhythmConductor, rồi kéo vào ô Spawner.
// Có 2 cách đưa nhạc vào:
//   1) Lúc chạy: SongSelector gọi StartWith(clip, beatmap) sau khi người chơi chọn bài và Python tách nhịp xong.
//   2) Để test nhanh: kéo Music (AudioClip) + Beatmap Json (TextAsset) vào Inspector, tự chạy khi Play.
//      Music PHẢI là đúng file đã đưa cho beatmap_generator.py.

using System;
using UnityEngine;

[Serializable]
public class BeatmapObstacle
{
    public float time;        // giây trong bài nhạc mà vật phải chạm người chơi
    public string type;       // "Jump" | "Crouch" | "DodgeLeft" | "DodgeRight" | "PunchWall"
    public float energy;      // 0..1 độ mạnh của nhịp (để tham khảo/hiệu ứng)
    public float brightness;  // 0..1 độ chói của âm sắc tại nhịp
    public float intensity;   // 0..1 cường độ đoạn nhạc tại đây (điều khiển nhịp độ vật cản)
    public float speed_mult;  // hệ số tốc độ bay của vật (0 = beatmap cũ chưa có -> coi như 1)
}

[Serializable]
public class Beatmap
{
    public string audio_file;
    public float bpm;
    public float duration;
    public string difficulty;
    public float analysis_seconds;   // thời gian Python phân tích bài này (để hiển thị/báo cáo)
    public float[] beats;
    public BeatmapObstacle[] obstacles;
}

[DefaultExecutionOrder(-100)] // cập nhật đồng hồ TRƯỚC mọi Obstacle trong cùng frame
[RequireComponent(typeof(AudioSource))]
public class RhythmConductor : MonoBehaviour
{
    [Header("Tham chiếu")]
    public ObstacleSpawner spawner;
    [Tooltip("Tuỳ chọn (để test nhanh). Khi chạy thật, SongSelector sẽ nạp nhạc lúc chạy")]
    public AudioClip music;
    [Tooltip("Tuỳ chọn (để test nhanh). Khi chạy thật, SongSelector sẽ nạp beatmap lúc chạy")]
    public TextAsset beatmapJson;

    [Header("Điều khiển")]
    [Tooltip("Tự chạy ngay khi Play nếu đã kéo sẵn Music + Beatmap Json")]
    public bool autoStart = true;
    [Tooltip("Giây chờ thêm (ngoài leadTime) trước khi nhạc bắt đầu")]
    public float extraPreRoll = 0.5f;
    [Tooltip("Bù độ trễ phát âm thanh (giây). Nghe nhạc muộn hơn vật cản -> tăng; sớm hơn -> giảm (có thể âm)")]
    public float audioOffset = 0f;

    [Header("Trạng thái (chỉ đọc)")]
    public float bpm;
    public bool isPlaying;
    public bool finished;

    // Đồng hồ bài nhạc (giây). Obstacle đọc giá trị này để biết "bây giờ" là lúc nào của bài.
    public float SongTime { get { return _songTime; } }
    // 1 ngay khi có beat, giảm dần về 0 - dùng để nháy hình/hiệu ứng theo nhịp
    public float BeatFlash { get { return _flash; } }
    public event Action<int> OnBeat;

    private AudioSource _audio;
    private Beatmap _map;
    private double _dspSongStart;
    private float _songTime;
    private bool _started;
    private bool _pending;     // đã nạp bài, đang chờ nhân vật chạm đất rồi mới bắt đầu
    private bool _firstFrame;
    private int _obsIndex;
    private int _beatIndex;
    private float _flash;

    void Awake()
    {
        _audio = GetComponent<AudioSource>();
        _audio.playOnAwake = false;
        _audio.loop = false;
    }

    void Start()
    {
        if (spawner == null)
        {
            Debug.LogError("[RhythmConductor] Chưa kéo ObstacleSpawner vào ô Spawner.");
            enabled = false;
            return;
        }

        // Chế độ test nhanh: có sẵn nhạc + beatmap trong Inspector
        if (autoStart && music != null && beatmapJson != null)
            StartWith(music, JsonUtility.FromJson<Beatmap>(beatmapJson.text));
        else
            Debug.Log("[RhythmConductor] Chưa có nhạc/beatmap - chờ SongSelector nạp bài.");
    }

    // Nạp 1 bài mới (từ SongSelector hoặc Inspector) và bắt đầu khi nhân vật sẵn sàng.
    public bool StartWith(AudioClip clip, Beatmap map)
    {
        if (clip == null || map == null || map.obstacles == null || map.obstacles.Length == 0)
        {
            Debug.LogError("[RhythmConductor] Nhạc hoặc beatmap rỗng/sai định dạng.");
            return false;
        }

        Stop(); // dọn bài đang chơi (nếu có)

        music = clip;
        _map = map;

        // Cảnh báo nếu nhạc không khớp file đã dùng để tách nhịp (sẽ lệch hoàn toàn)
        string expected = System.IO.Path.GetFileNameWithoutExtension(_map.audio_file ?? "");
        if (!string.IsNullOrEmpty(expected) && expected != music.name)
            Debug.LogWarning($"[RhythmConductor] Beatmap tạo từ '{_map.audio_file}' nhưng Music đang là '{music.name}'. Kiểm tra lại!");

        bpm = _map.bpm;
        spawner.autoSpawn = false; // từ giờ vật cản sinh theo nhạc, không sinh ngẫu nhiên nữa
        if (ScoreManager.Instance != null) ScoreManager.Instance.ResetScore();

        _obsIndex = 0;
        _beatIndex = 0;
        _flash = 0f;
        finished = false;
        _pending = true;

        Debug.Log($"[RhythmConductor] {_map.obstacles.Length} vật cản, {(_map.beats != null ? _map.beats.Length : 0)} beat, ~{_map.bpm:F1} BPM, độ khó {_map.difficulty}, phân tích {_map.analysis_seconds:F1}s");
        return true;
    }

    // Dừng bài đang chơi, xoá hết vật cản còn trên màn hình.
    public void Stop()
    {
        if (_audio != null) _audio.Stop();
        _started = false;
        _pending = false;
        isPlaying = false;
        finished = false;
        _flash = 0f;

        foreach (var o in FindObjectsByType<Obstacle>(FindObjectsSortMode.None))
            Destroy(o.gameObject);
    }

    void Update()
    {
        if (!_started)
        {
            // Chờ nhân vật chạm đất + spawner chốt mốc rồi mới bắt đầu
            if (_pending && spawner.IsCalibrated) StartSong();
            return;
        }

        // Đồng hồ: dspTime làm gốc, làm mượt để không giật theo bước của buffer âm thanh
        float dspBased = (float)(AudioSettings.dspTime - _dspSongStart) - audioOffset;
        if (_firstFrame)
        {
            _songTime = dspBased;
            _firstFrame = false;
        }
        else
        {
            _songTime = Mathf.Lerp(_songTime + Time.unscaledDeltaTime, dspBased, 0.2f);
        }

        // Sinh vật cản khi tới lúc: bay mất leadTime giây để tới người chơi
        while (_obsIndex < _map.obstacles.Length &&
               _songTime >= _map.obstacles[_obsIndex].time - spawner.leadTime)
        {
            var o = _map.obstacles[_obsIndex++];
            ObstacleType t;
            if (Enum.TryParse(o.type, true, out t))
                spawner.SpawnAtSongTime(t, o.time, this, o.speed_mult);
            else
                Debug.LogWarning($"[RhythmConductor] Loại vật cản lạ trong beatmap: '{o.type}'");
        }

        // Báo từng beat (để nháy hình/hiệu ứng)
        if (_map.beats != null)
        {
            while (_beatIndex < _map.beats.Length && _songTime >= _map.beats[_beatIndex])
            {
                _beatIndex++;
                _flash = 1f;
                if (OnBeat != null) OnBeat(_beatIndex);
            }
        }
        _flash = Mathf.Max(0f, _flash - Time.unscaledDeltaTime * 4f);

        if (!finished && _songTime > music.length)
        {
            finished = true;
            isPlaying = false;
            Debug.Log("[RhythmConductor] Hết bài.");
        }
    }

    [ContextMenu("Start Song")]
    public void StartSong()
    {
        if (_started || !enabled || _map == null || music == null) return;
        _pending = false;

        float preRoll = spawner.leadTime + extraPreRoll;
        _dspSongStart = AudioSettings.dspTime + preRoll;

        _audio.clip = music;
        _audio.PlayScheduled(_dspSongStart);

        _songTime = -preRoll;
        _firstFrame = true;
        _started = true;
        isPlaying = true;
        Debug.Log($"[RhythmConductor] Bắt đầu: nhạc phát sau {preRoll:F1}s.");
    }

    // Hiển thị tạm để kiểm tra đồng bộ bằng mắt: ô vuông nháy theo từng beat
    void OnGUI()
    {
        if (!_started) return;

        var style = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperRight };
        style.normal.textColor = Color.white;

        float w = 260f;
        float x = Screen.width - w - 15f;
        GUI.Label(new Rect(x, 10, w, 30), $"{bpm:F0} BPM", style);
        GUI.Label(new Rect(x, 40, w, 30), _songTime < 0 ? "Chuẩn bị..." : $"{_songTime:F1}s / {music.length:F0}s", style);

        Color old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.15f + 0.85f * _flash);
        GUI.DrawTexture(new Rect(Screen.width - 60f, 75f, 45f, 45f), Texture2D.whiteTexture);
        GUI.color = old;

        if (finished)
        {
            var big = new GUIStyle(style) { fontSize = 40, alignment = TextAnchor.MiddleCenter };
            string summary = "HẾT BÀI";
            if (ScoreManager.Instance != null)
                summary += $"\nNé được {ScoreManager.Instance.hits} - Trúng {ScoreManager.Instance.misses}\nCombo cao nhất {ScoreManager.Instance.bestCombo}";
            GUI.Label(new Rect(0, Screen.height * 0.3f, Screen.width, 200), summary, big);
        }
    }
}