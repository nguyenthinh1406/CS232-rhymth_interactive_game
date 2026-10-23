// Obstacle.cs
// Mỗi vật thể có 1 "thời điểm chạm" (hitTime). Vị trí được TÍNH LẠI mỗi frame từ đồng hồ:
//     z = judgeZ + speed * (hitTime - bâyGiờ)
// nên đúng hitTime thì vật nằm chính xác tại người chơi, không bị lệch dần như kiểu cộng dồn
// theo deltaTime. "Bây giờ" là đồng hồ bài nhạc (RhythmConductor.SongTime) khi chơi theo nhạc,
// hoặc Time.time khi ở chế độ tự sinh để test.
//
// Chấm điểm tại hitTime + judgeLatency (bù độ trễ webcam + xử lý ảnh, vì người chơi luôn
// phản ứng chậm hơn thực tế một chút):
//   Jump       -> đang trên không, hoặc vừa nhảy trong cửa sổ jumpWindow
//   Crouch     -> đang cúi (hoặc vừa cúi trong crouchGrace giây)
//   DodgeLeft  -> đứng lệch sang TRÁI so với tâm ít nhất dodgeThreshold mét
//   DodgeRight -> đứng lệch sang PHẢI so với tâm ít nhất dodgeThreshold mét
//   PunchWall  -> có cú đấm trong lúc tường đi vào tầm đấm (punchReach) -> tường vỡ
//
// Không dùng vật lý/Collider. Script này do ObstacleSpawner tự gắn và khởi tạo (Init).

using UnityEngine;

public class Obstacle : MonoBehaviour
{
    public ObstacleType type;

    private ObstacleSpawner _s;
    private PlayerActionController _player;
    private RhythmConductor _conductor;   // null = chế độ tự sinh (dùng Time.time)
    private float _hitTime;
    private float _speed;                 // tốc độ bay riêng của vật này (m/s) - có thể khác tốc độ gốc theo cường độ nhạc
    private float _judgeZ;                // toạ độ Z của người chơi (nơi chấm điểm)
    private float _centerX;               // X chính giữa (vị trí xuất phát của người chơi)

    private bool _judged;
    private bool _punchZoneEntered;
    private float _punchZoneEnterTime;
    private Renderer[] _renderers;

    public void Init(ObstacleType t, ObstacleSpawner spawner, PlayerActionController player,
                     RhythmConductor conductor, float hitTime, float judgeZ, float centerX, float speed)
    {
        type = t;
        _s = spawner;
        _player = player;
        _conductor = conductor;
        _hitTime = hitTime;
        _speed = speed;
        _judgeZ = judgeZ;
        _centerX = centerX;
        _renderers = GetComponentsInChildren<Renderer>();
    }

    float Now()
    {
        return (_conductor != null) ? _conductor.SongTime : Time.time;
    }

    void Update()
    {
        float timeToHit = _hitTime - Now();

        Vector3 p = transform.position;
        transform.position = new Vector3(p.x, p.y, _judgeZ + _speed * timeToHit);

        if (!_judged)
        {
            if (type == ObstacleType.PunchWall) UpdatePunchWall(timeToHit);
            else if (timeToHit <= -_s.judgeLatency) JudgeInstant();
        }

        // Dọn dẹp khi đã trôi qua sau lưng người chơi
        if (transform.position.z < _judgeZ - 6f) Destroy(gameObject);
    }

    // ---------- Các loại chấm tại thời điểm vật tới người chơi ----------
    void JudgeInstant()
    {
        _judged = true;
        bool success = false;

        switch (type)
        {
            case ObstacleType.Jump:
                success = !_player.IsGrounded || (Time.time - _player.LastJumpTime) <= _s.jumpWindow;
                break;

            case ObstacleType.Crouch:
                success = _player.IsCrouching || (Time.time - _player.LastCrouchTime) <= _s.crouchGrace;
                break;

            case ObstacleType.DodgeLeft:
            case ObstacleType.DodgeRight:
            {
                float offset = _player.transform.position.x - _centerX;   // dương = lệch sang +X
                bool wantLeft = (type == ObstacleType.DodgeLeft);
                if (_s.invertLeftRight) wantLeft = !wantLeft;              // +X có thể là "trái" tuỳ hướng camera
                success = wantLeft ? offset <= -_s.dodgeThreshold : offset >= _s.dodgeThreshold;
                break;
            }
        }

        Finish(success);
    }

    // Tường: có "tầm đấm" - đi vào tầm mà có cú đấm thì vỡ ngay; trôi qua mà không đấm thì trúng.
    void UpdatePunchWall(float timeToHit)
    {
        // Tầm đấm tính theo TỐC ĐỘ GỐC (không theo speed riêng của vật) để cửa sổ thời gian để đấm luôn công bằng
        float reachTime = _s.punchReach / Mathf.Max(_s.speed, 0.01f);

        if (!_punchZoneEntered && timeToHit <= reachTime)
        {
            _punchZoneEntered = true;
            _punchZoneEnterTime = Time.time;
        }

        if (_punchZoneEntered)
        {
            // Chấp nhận cú đấm xảy ra ngay trước khi tường vào tầm (punchLead giây) để bớt khắt khe
            if (_player.LastPunchTime >= _punchZoneEnterTime - _s.punchLead)
            {
                _judged = true;
                Finish(true);
                return;
            }
        }

        if (timeToHit <= -_s.judgeLatency)
        {
            _judged = true;
            Finish(false);
        }
    }

    // ---------- Kết quả ----------
    void Finish(bool success)
    {
        if (ScoreManager.Instance != null) ScoreManager.Instance.Report(success, type);
        Debug.Log($"[Obstacle] {type}: {(success ? "NÉ ĐƯỢC" : "BỊ TRÚNG")}");

        Tint(success ? new Color(0.2f, 1f, 0.3f) : new Color(1f, 0.15f, 0.15f));

        // Tường vỡ thì biến mất luôn; vật khác cứ trôi qua rồi bị dọn ở Update()
        if (type == ObstacleType.PunchWall && success)
            Destroy(gameObject, 0.1f);
    }

    void Tint(Color c)
    {
        if (_renderers == null) return;
        foreach (var r in _renderers)
            if (r != null) r.material.color = c;
    }
}