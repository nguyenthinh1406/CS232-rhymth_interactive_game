// SongSelector.cs
// Cho người chơi chọn 1 file nhạc BẤT KỲ, rồi tự động:
//   1) Gọi Python (beatmap_generator.py) tách nhịp -> ghi beatmap JSON ra thư mục tạm
//   2) Nạp file nhạc vào Unity lúc chạy (UnityWebRequestMultimedia)
//   3) Đưa nhạc + beatmap cho RhythmConductor để bắt đầu chơi
//
// Không lưu cache: mỗi lần chọn bài là phân tích lại từ đầu (vài giây cho bài ~1 phút).
//
// Chọn bài bằng 2 cách: bấm vào bài trong thư mục "Songs" (nằm cạnh thư mục Assets),
// hoặc dán đường dẫn đầy đủ tới file nhạc (.wav / .mp3 / .ogg).
//
// Cách gắn: GameObject "GameManager" -> Add Component SongSelector, rồi đặt:
//   Conductor   : có thể để trống - tự tìm RhythmConductor (GameManager cần có component này)
//   Python Exe  : đường dẫn python có cài librosa, ví dụ
//                 D:\project\python\venv_mediapipe\Scripts\python.exe
//   Script Path : đường dẫn đầy đủ tới beatmap_generator.py
// Nhớ để TRỐNG ô Music và Beatmap Json trên RhythmConductor (nếu không nó tự chạy bài mẫu).

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug; // tránh nhầm với System.Diagnostics.Debug

public class SongSelector : MonoBehaviour
{
    [Header("Tham chiếu")]
    public RhythmConductor conductor;

    [Header("Python (tách nhịp)")]
    [Tooltip("Python có cài librosa. Nên dùng python trong venv 3.11/3.12 đã tạo cho action_server.py")]
    public string pythonExe = "python";
    [Tooltip("Đường dẫn đầy đủ tới beatmap_generator.py")]
    public string scriptPath = "";
    [Tooltip("Quá thời gian này (giây) thì huỷ phân tích")]
    public float analysisTimeout = 300f;

    [Header("Nhạc")]
    [Tooltip("Để trống = thư mục 'Songs' nằm cạnh thư mục Assets")]
    public string songsFolder = "";

    private enum State { Menu, Analyzing, LoadingAudio, Error, Playing }
    private State _state = State.Menu;

    private static readonly string[] AllowedExt = { ".wav", ".mp3", ".ogg" };
    private static readonly string[] DifficultyNames = { "easy", "normal", "hard" };
    private static readonly string[] DifficultyLabels = { "Dễ", "Vừa", "Khó" };

    // Danh sách bài + ô nhập đường dẫn
    private string[] _songFiles = new string[0];
    private Vector2 _scroll;
    private string _typedPath = "";
    private int _difficultyIndex = 1;

    // Tiến trình phân tích
    private Process _proc;
    private readonly ConcurrentQueue<string> _outLines = new ConcurrentQueue<string>();
    private readonly StringBuilder _errText = new StringBuilder();
    private readonly object _errLock = new object();
    private float _analysisStart;
    private string _stageText = "";
    private string _audioPath;
    private string _beatmapPath;

    // Kết quả / lỗi
    private string _errorMessage = "";
    private AudioClip _lastClip;
    private Beatmap _lastMap;
    private string _lastSongName = "";

    // Style IMGUI
    private GUIStyle _label, _title, _button, _field, _small;

    void Start()
    {
        // Không kéo tay cũng được: tự tìm RhythmConductor trên cùng object, rồi đến cả scene
        if (conductor == null) conductor = GetComponent<RhythmConductor>();
        if (conductor == null) conductor = FindFirstObjectByType<RhythmConductor>();
        if (conductor == null)
        {
            Debug.LogError("[SongSelector] Không tìm thấy RhythmConductor trong scene. Hãy Add Component 'RhythmConductor' vào GameObject GameManager.");
            enabled = false;
            return;
        }

        if (string.IsNullOrEmpty(songsFolder))
            songsFolder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Songs"));
        Directory.CreateDirectory(songsFolder);
        RefreshSongs();

        // Nếu RhythmConductor sẽ tự chạy bài mẫu (đã kéo sẵn Music/Beatmap trong Inspector) thì bỏ qua màn chọn bài
        if (conductor.autoStart && conductor.music != null && conductor.beatmapJson != null)
            _state = State.Playing;
    }

    void RefreshSongs()
    {
        try
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var f in Directory.GetFiles(songsFolder))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (Array.IndexOf(AllowedExt, ext) >= 0) list.Add(f);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            _songFiles = list.ToArray();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SongSelector] Không đọc được thư mục nhạc: {e.Message}");
            _songFiles = new string[0];
        }
    }

    // ------------------------------------------------------------------
    //  Bước 1: chạy Python tách nhịp
    // ------------------------------------------------------------------
    void BeginAnalysis(string audioPath)
    {
        audioPath = (audioPath ?? "").Trim().Trim('"');

        // "Copy as path" của Windows thêm dấu ngoặc kép - bỏ đi để dán thẳng vào Inspector được
        pythonExe = (pythonExe ?? "").Trim().Trim('"');
        scriptPath = (scriptPath ?? "").Trim().Trim('"');

        if (!File.Exists(audioPath)) { Fail($"Không tìm thấy file:\n{audioPath}"); return; }

        string ext = Path.GetExtension(audioPath).ToLowerInvariant();
        if (Array.IndexOf(AllowedExt, ext) < 0) { Fail("Chỉ hỗ trợ file .wav, .mp3, .ogg"); return; }

        if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
        {
            Fail("Chưa đặt đúng 'Script Path' (đường dẫn tới beatmap_generator.py) trên SongSelector trong Inspector.");
            return;
        }

        conductor.Stop();
        _audioPath = audioPath;
        _lastSongName = Path.GetFileNameWithoutExtension(audioPath);
        _beatmapPath = Path.Combine(Application.temporaryCachePath, "beatmap_current.json");
        try { if (File.Exists(_beatmapPath)) File.Delete(_beatmapPath); } catch { }

        // Xả hàng đợi cũ
        string dummy;
        while (_outLines.TryDequeue(out dummy)) { }
        lock (_errLock) { _errText.Length = 0; }

        string args = $"-u \"{scriptPath}\" \"{audioPath}\" -o \"{_beatmapPath}\" --difficulty {DifficultyNames[_difficultyIndex]}";

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(scriptPath),
        };
        psi.EnvironmentVariables["PYTHONUTF8"] = "1";
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

        try
        {
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            // Các handler này chạy trên thread khác -> chỉ đẩy vào hàng đợi, xử lý ở Update()
            _proc.OutputDataReceived += (s, e) => { if (e.Data != null) _outLines.Enqueue(e.Data); };
            _proc.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) lock (_errLock) { _errText.AppendLine(e.Data); }
            };
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _proc = null;
            Fail($"Không chạy được Python ('{pythonExe}'): {ex.Message}\nKiểm tra lại ô 'Python Exe' trên SongSelector.");
            return;
        }

        _analysisStart = Time.realtimeSinceStartup;
        _stageText = "Khởi động Python...";
        _state = State.Analyzing;
        Debug.Log($"[SongSelector] Phân tích: {pythonExe} {args}");
    }

    void Update()
    {
        if (_state != State.Analyzing || _proc == null) return;

        // Hiển thị giai đoạn hiện tại do Python báo ([STAGE] ...)
        string line;
        while (_outLines.TryDequeue(out line))
        {
            if (line.StartsWith("[STAGE]")) _stageText = line.Substring(7).Trim();
        }

        if (Time.realtimeSinceStartup - _analysisStart > analysisTimeout)
        {
            KillProcess();
            Fail($"Phân tích quá {analysisTimeout:F0} giây nên đã huỷ. Thử bài ngắn hơn hoặc tăng 'Analysis Timeout'.");
            return;
        }

        if (_proc.HasExited)
        {
            _proc.WaitForExit(); // chờ xả nốt đầu ra (tiến trình đã kết thúc nên rất nhanh)
            int code = _proc.ExitCode;
            _proc.Dispose();
            _proc = null;

            // Lấy nốt các dòng còn lại
            while (_outLines.TryDequeue(out line)) { }

            if (code != 0 || !File.Exists(_beatmapPath))
            {
                Fail("Tách nhịp thất bại:\n" + ExtractError());
                return;
            }

            _state = State.LoadingAudio;
            StartCoroutine(LoadAndPlay());
        }
    }

    string ExtractError()
    {
        string all;
        lock (_errLock) { all = _errText.ToString(); }

        // Ưu tiên dòng [ERROR] do chính script báo (đã là tiếng Việt, dễ hiểu)
        foreach (var l in all.Split('\n'))
        {
            string t = l.Trim();
            if (t.StartsWith("[ERROR]")) return t.Substring(7).Trim();
        }

        // Không có -> lấy vài dòng cuối của stderr (thường là lỗi Python/thiếu thư viện)
        var lines = all.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int from = Mathf.Max(0, lines.Length - 5);
        string tail = string.Join("\n", lines, from, lines.Length - from).Trim();
        return string.IsNullOrEmpty(tail) ? "(không có thông tin lỗi)" : tail;
    }

    // ------------------------------------------------------------------
    //  Bước 2 + 3: nạp nhạc vào Unity, đưa cho RhythmConductor
    // ------------------------------------------------------------------
    IEnumerator LoadAndPlay()
    {
        AudioType type = AudioType.UNKNOWN;
        switch (Path.GetExtension(_audioPath).ToLowerInvariant())
        {
            case ".wav": type = AudioType.WAV; break;
            case ".mp3": type = AudioType.MPEG; break;
            case ".ogg": type = AudioType.OGGVORBIS; break;
        }

        string url = new Uri(_audioPath).AbsoluteUri; // mã hoá đúng khoảng trắng + ký tự tiếng Việt

        AudioClip clip = null;
        using (var req = UnityWebRequestMultimedia.GetAudioClip(url, type))
        {
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Fail($"Unity không nạp được file nhạc:\n{req.error}");
                yield break;
            }
            clip = DownloadHandlerAudioClip.GetContent(req);
        }

        if (clip == null || clip.length <= 0f)
        {
            Fail("File nhạc rỗng hoặc Unity không giải mã được định dạng này. Thử đổi sang .wav hoặc .ogg.");
            yield break;
        }
        // Tên clip phải trùng tên file đã đưa cho Python (RhythmConductor so sánh để phát hiện nhầm bài)
        clip.name = _lastSongName;

        // (không đặt yield trong khối catch: C# không cho phép một số dạng yield ở đó)
        Beatmap map = null;
        string mapError = null;
        try { map = JsonUtility.FromJson<Beatmap>(File.ReadAllText(_beatmapPath, Encoding.UTF8)); }
        catch (Exception e) { mapError = e.Message; }

        if (mapError != null)
        {
            Fail($"Không đọc được beatmap: {mapError}");
            yield break;
        }

        if (map == null || map.obstacles == null || map.obstacles.Length == 0)
        {
            Fail("Beatmap rỗng: không sinh được vật cản nào (bài quá ngắn hoặc không dò được nhịp).");
            yield break;
        }

        _lastClip = clip;
        _lastMap = map;
        if (conductor.StartWith(clip, map)) _state = State.Playing;
        else Fail("RhythmConductor từ chối bài này (xem Console).");
    }

    void Fail(string message)
    {
        _errorMessage = message;
        _state = State.Error;
        Debug.LogWarning("[SongSelector] " + message);
    }

    void KillProcess()
    {
        try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
        try { if (_proc != null) _proc.Dispose(); } catch { }
        _proc = null;
    }

    void OnDestroy()
    {
        KillProcess();
    }

    // ------------------------------------------------------------------
    //  Giao diện (IMGUI)
    // ------------------------------------------------------------------
    void EnsureStyles()
    {
        if (_label != null) return;
        _label = new GUIStyle(GUI.skin.label) { fontSize = 20, wordWrap = true };
        _label.normal.textColor = Color.white;
        _title = new GUIStyle(_label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _small = new GUIStyle(_label) { fontSize = 15 };
        _small.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
        _button = new GUIStyle(GUI.skin.button) { fontSize = 20, alignment = TextAnchor.MiddleLeft };
        _field = new GUIStyle(GUI.skin.textField) { fontSize = 18 };
    }

    void OnGUI()
    {
        if (!enabled) return;
        EnsureStyles();

        // Đang chơi: chỉ hiện nút nhỏ để chọn bài khác (và Chơi lại/Chọn bài khác khi hết bài)
        if (_state == State.Playing)
        {
            DrawPlayingControls();
            return;
        }

        float w = Mathf.Min(760f, Screen.width - 40f);
        float h = Mathf.Min(620f, Screen.height - 40f);
        var rect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);

        GUI.color = new Color(0f, 0f, 0f, 0.82f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUILayout.BeginArea(new Rect(rect.x + 20, rect.y + 15, rect.width - 40, rect.height - 30));
        switch (_state)
        {
            case State.Menu: DrawMenu(); break;
            case State.Analyzing: DrawAnalyzing(); break;
            case State.LoadingAudio: GUILayout.Label("Đang nạp nhạc vào Unity...", _title); break;
            case State.Error: DrawError(); break;
        }
        GUILayout.EndArea();
    }

    void DrawMenu()
    {
        GUILayout.Label("CHỌN BÀI HÁT", _title);
        GUILayout.Space(8);

        GUILayout.BeginHorizontal();
        GUILayout.Label("Độ khó:", _label, GUILayout.Width(90));
        _difficultyIndex = GUILayout.Toolbar(_difficultyIndex, DifficultyLabels, _button, GUILayout.Height(36));
        GUILayout.EndHorizontal();
        GUILayout.Space(8);

        GUILayout.BeginHorizontal();
        GUILayout.Label("Bài trong thư mục Songs:", _label);
        if (GUILayout.Button("Làm mới", _button, GUILayout.Width(110), GUILayout.Height(32))) RefreshSongs();
        GUILayout.EndHorizontal();
        GUILayout.Label(songsFolder, _small);

        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(220));
        if (_songFiles.Length == 0)
            GUILayout.Label("(Chưa có bài nào. Chép file .wav/.mp3/.ogg vào thư mục trên rồi bấm Làm mới)", _small);
        foreach (var f in _songFiles)
        {
            if (GUILayout.Button("▶  " + Path.GetFileName(f), _button, GUILayout.Height(36)))
                BeginAnalysis(f);
        }
        GUILayout.EndScrollView();

        GUILayout.Space(8);
        GUILayout.Label("Hoặc dán đường dẫn file nhạc bất kỳ:", _label);
        GUILayout.BeginHorizontal();
        _typedPath = GUILayout.TextField(_typedPath, _field, GUILayout.Height(34));
        if (GUILayout.Button("Phân tích & chơi", _button, GUILayout.Width(190), GUILayout.Height(34)))
            BeginAnalysis(_typedPath);
        GUILayout.EndHorizontal();
    }

    void DrawAnalyzing()
    {
        float elapsed = Time.realtimeSinceStartup - _analysisStart;
        GUILayout.Label("ĐANG PHÂN TÍCH NHẠC", _title);
        GUILayout.Space(10);
        GUILayout.Label(_lastSongName, _label);
        GUILayout.Space(10);
        GUILayout.Label($"{_stageText}  ({elapsed:F0}s)", _label);
        GUILayout.Space(6);
        GUILayout.Label("Bài dài có thể mất vài chục giây.", _small);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Huỷ", _button, GUILayout.Height(38)))
        {
            KillProcess();
            _state = State.Menu;
        }
    }

    void DrawError()
    {
        GUILayout.Label("CÓ LỖI", _title);
        GUILayout.Space(10);
        GUILayout.Label(_errorMessage, _label);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Quay lại", _button, GUILayout.Height(38)))
            _state = State.Menu;
    }

    void DrawPlayingControls()
    {
        if (conductor.finished)
        {
            // RhythmConductor đang vẽ bảng "HẾT BÀI" ở phần trên; đặt nút ở nửa dưới màn hình
            float w = 260f, h = 44f, x = (Screen.width - w) / 2f, y = Screen.height * 0.62f;
            if (_lastClip != null && GUI.Button(new Rect(x, y, w, h), "Chơi lại", _button))
                conductor.StartWith(_lastClip, _lastMap);
            if (GUI.Button(new Rect(x, y + h + 10, w, h), "Chọn bài khác", _button))
                BackToMenu();
        }
        else
        {
            if (GUI.Button(new Rect(Screen.width / 2f - 90f, 10f, 180f, 34f), "Chọn bài khác", _button))
                BackToMenu();
        }
    }

    void BackToMenu()
    {
        conductor.Stop();
        RefreshSongs();
        _state = State.Menu;
    }
}