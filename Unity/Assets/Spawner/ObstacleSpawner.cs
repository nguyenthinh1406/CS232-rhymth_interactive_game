// ObstacleSpawner.cs
// Sinh chướng ngại vật ở phía trước người chơi (+Z) và cho chúng trôi về phía người chơi.
// Chưa có model 3D thì tự dựng khối hộp placeholder (mỗi loại 1 màu + kích thước riêng),
// có thể thay bằng prefab của bạn ở mục "Prefab tuỳ chọn".
//
// Hiện tại: chế độ TỰ SINH ngẫu nhiên theo chu kỳ (autoSpawn) để test va chạm ngay.
// Sau này khi có beatmap: tắt autoSpawn, rồi gọi SpawnObstacle(type) đúng thời điểm
// (beatTime - leadTime) cho từng nốt nhạc, vật sẽ tới người chơi đúng vào nhịp.
//
// Gắn vào 1 GameObject bất kỳ (ví dụ "GameManager"), kéo nhân vật vào ô Player.

using UnityEngine;

public class ObstacleSpawner : MonoBehaviour
{
    [System.Serializable]
    public struct PrefabEntry
    {
        public ObstacleType type;
        public GameObject prefab;
    }

    [Header("Tham chiếu")]
    public PlayerActionController player;

    [Header("Chuyển động")]
    [Tooltip("Tốc độ vật thể trôi về phía người chơi (m/s)")]
    public float speed = 6f;
    [Tooltip("Số giây từ lúc vật xuất hiện đến lúc tới người chơi. Khoảng cách sinh = speed x leadTime")]
    public float leadTime = 3f;
    [Tooltip("Bật: đoạn nhạc dồn dập thì vật bay nhanh hơn, đoạn êm thì chậm hơn (theo speed_mult trong beatmap). " +
             "Vật vẫn chạm người chơi đúng giờ, chỉ khác độ lao tới nhìn thấy")]
    public bool speedFollowsMusic = true;

    [Header("Tự sinh để test")]
    public bool autoSpawn = true;
    public float spawnInterval = 3f;
    public float firstSpawnDelay = 2f;

    [Header("Hướng trái/phải")]
    [Tooltip("Bật nếu 'né trái' mà bạn phải bước sang phải trên màn hình (do hướng camera game)")]
    public bool invertLeftRight = false;

    [Header("Độ khó / chấm điểm")]
    [Tooltip("Phải lệch khỏi tâm ít nhất bao nhiêu mét thì tính là đã né trái/phải")]
    public float dodgeThreshold = 0.5f;
    [Tooltip("Nhảy trong vòng bao nhiêu giây trước lúc vật tới thì tính là nhảy đúng lúc")]
    public float jumpWindow = 0.5f;
    [Tooltip("Khoan dung (giây) khi vừa đứng dậy khỏi tư thế cúi")]
    public float crouchGrace = 0.15f;
    [Tooltip("Tường vào trong khoảng này (mét) trước người chơi thì cú đấm mới tính")]
    public float punchReach = 1.5f;
    [Tooltip("Cho phép cú đấm xảy ra sớm hơn lúc tường vào tầm tối đa bao nhiêu giây")]
    public float punchLead = 0.25f;
    [Tooltip("Bù độ trễ webcam + xử lý ảnh (giây): chấm điểm muộn hơn thời điểm vật chạm một chút")]
    public float judgeLatency = 0.15f;

    [Header("Kích thước placeholder")]
    public float obstacleWidth = 3f;
    [Tooltip("Độ cao (từ chân người chơi) của thanh chắn phải cúi")]
    public float crouchBarHeight = 1.5f;

    [Header("Prefab tuỳ chọn (để trống = dùng khối hộp placeholder)")]
    public PrefabEntry[] prefabs;

    private float _centerX;   // X chính giữa = vị trí xuất phát của người chơi
    private float _judgeZ;    // Z người chơi (nơi chấm điểm)
    private float _floorY;    // cao độ chân người chơi
    private float _timer;
    private bool _calibrated;

    // RhythmConductor chờ cờ này (nhân vật đã chạm đất, đã chốt mốc) rồi mới bắt đầu phát nhạc
    public bool IsCalibrated => _calibrated;

    void Start()
    {
        if (player == null)
        {
            Debug.LogError("[ObstacleSpawner] Chưa kéo nhân vật vào ô Player.");
            enabled = false;
            return;
        }

        CaptureOrigin(); // tạm thời; sẽ chốt lại chính xác khi nhân vật chạm đất lần đầu
        _timer = -firstSpawnDelay;
    }

    void CaptureOrigin()
    {
        _centerX = player.transform.position.x;
        _judgeZ = player.transform.position.z;
        _floorY = player.transform.position.y;
    }

    void Update()
    {
        // Nhân vật có thể đang rơi xuống sàn lúc mới Play -> chờ chạm đất rồi mới chốt mốc tâm/sàn
        if (!_calibrated)
        {
            if (!player.IsGrounded) return;
            CaptureOrigin();
            _calibrated = true;
        }

        if (!autoSpawn) return;

        _timer += Time.deltaTime;
        if (_timer >= spawnInterval)
        {
            _timer = 0f;
            var values = (ObstacleType[])System.Enum.GetValues(typeof(ObstacleType));
            SpawnObstacle(values[Random.Range(0, values.Length)]);
        }
    }

    // Chế độ tự sinh (test): vật tới người chơi sau đúng leadTime giây tính từ lúc sinh.
    public Obstacle SpawnObstacle(ObstacleType type)
    {
        return Spawn(type, Time.time + leadTime, null);
    }

    // Chế độ theo nhạc: vật chạm người chơi đúng vào hitSongTime (giây, theo đồng hồ bài nhạc).
    public Obstacle SpawnAtSongTime(ObstacleType type, float hitSongTime, RhythmConductor conductor, float speedMult = 1f)
    {
        return Spawn(type, hitSongTime, conductor, speedMult);
    }

    Obstacle Spawn(ObstacleType type, float hitTime, RhythmConductor conductor, float speedMult = 1f)
    {
        // Tốc độ riêng của vật này: leadTime không đổi nên vật nhanh hơn sẽ được sinh ở xa hơn
        float sp = speed * ((speedFollowsMusic && speedMult > 0f) ? speedMult : 1f);

        float now = (conductor != null) ? conductor.SongTime : Time.time;
        float spawnZ = _judgeZ + sp * (hitTime - now);

        GameObject go = TryInstantiatePrefab(type);
        if (go == null) go = BuildPlaceholder(type);

        // Prefab/placeholder đã tự đặt X, Y; ta chỉ đặt Z
        Vector3 p = go.transform.position;
        go.transform.position = new Vector3(p.x, p.y, spawnZ);

        var ob = go.GetComponent<Obstacle>();
        if (ob == null) ob = go.AddComponent<Obstacle>();
        ob.Init(type, this, player, conductor, hitTime, _judgeZ, _centerX, sp);
        return ob;
    }

    GameObject TryInstantiatePrefab(ObstacleType type)
    {
        if (prefabs == null) return null;
        foreach (var e in prefabs)
        {
            if (e.type == type && e.prefab != null)
            {
                Vector3 pos = new Vector3(_centerX, _floorY, _judgeZ);
                return Instantiate(e.prefab, pos, Quaternion.identity);
            }
        }
        return null;
    }

    // ---------- Khối hộp placeholder ----------
    GameObject BuildPlaceholder(ObstacleType type)
    {
        float x = _centerX;
        float w = obstacleWidth, h, d = 0.4f, centerY;
        Color color;

        // Vật chặn 1 bên: đặt lệch sang phía NGƯỢC với hướng cần né, chừa khoảng trống phía né
        float sideSign = (type == ObstacleType.DodgeLeft) ? 1f : -1f;
        if (invertLeftRight) sideSign = -sideSign;

        switch (type)
        {
            case ObstacleType.Jump:
                h = 0.4f; centerY = _floorY + h / 2f; color = new Color(1f, 0.9f, 0.2f);          // vàng
                break;
            case ObstacleType.Crouch:
                h = 0.4f; centerY = _floorY + crouchBarHeight; color = new Color(0.2f, 0.9f, 1f); // lục lam
                break;
            case ObstacleType.DodgeLeft:
            case ObstacleType.DodgeRight:
                w = 2.4f; h = 2.2f; centerY = _floorY + h / 2f;
                x = _centerX + sideSign * 0.9f;
                color = (type == ObstacleType.DodgeLeft) ? new Color(1f, 0.55f, 0.1f) : new Color(0.9f, 0.3f, 1f); // cam / tím
                break;
            default: // PunchWall
                w = obstacleWidth + 1f; h = 2.4f; d = 0.3f; centerY = _floorY + h / 2f;
                color = new Color(0.55f, 0.6f, 0.75f);                                           // xám xanh
                break;
        }

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = $"Obstacle_{type}";
        go.transform.position = new Vector3(x, centerY, 0f);
        go.transform.localScale = new Vector3(w, h, d);

        // Chấm bằng logic, không cần Collider
        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);

        var r = go.GetComponent<Renderer>();
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        if (sh != null) r.material = new Material(sh);
        r.material.color = color;

        return go;
    }

    // ---------- Test thủ công: chuột phải vào component trong Inspector (lúc đang Play) ----------
    [ContextMenu("Spawn: Jump")] void TestJump() => SpawnObstacle(ObstacleType.Jump);
    [ContextMenu("Spawn: Crouch")] void TestCrouch() => SpawnObstacle(ObstacleType.Crouch);
    [ContextMenu("Spawn: DodgeLeft")] void TestDodgeLeft() => SpawnObstacle(ObstacleType.DodgeLeft);
    [ContextMenu("Spawn: DodgeRight")] void TestDodgeRight() => SpawnObstacle(ObstacleType.DodgeRight);
    [ContextMenu("Spawn: PunchWall")] void TestPunchWall() => SpawnObstacle(ObstacleType.PunchWall);
}