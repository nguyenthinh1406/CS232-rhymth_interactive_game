// PoseRetargeter.cs
// Bắt chước tư thế người chơi: xoay TOÀN THÂN nhân vật theo hướng vai (hông quay
// theo người chơi quay), đồng thời xoay từng xương tay/chân (vai->khuỷu,
// khuỷu->cổ tay, hông->gối, gối->mắt cá...) khớp với hướng thật ngoài đời,
// dùng toạ độ THẾ GIỚI 3D (mét, gốc ở giữa hông) do MediaPipe Pose Landmarker
// cung cấp (pose_world_landmarks).
//
// Gắn script này vào CÙNG object nhân vật (object có Animator Humanoid, cũng
// là object có CharacterController/PlayerActionController).
// Kéo GameObject "NetworkManager" vào ô Receiver.
//
// Giới hạn: xương không xoay dọc trục (twist) vì chỉ có hướng của xương,
// không có hướng "xoay" quanh trục đó (ví dụ xoay cổ tay, xoay cẳng tay).

using UnityEngine;

[RequireComponent(typeof(Animator))]
public class PoseRetargeter : MonoBehaviour
{
    [Header("Tham chiếu")]
    public UdpActionReceiver receiver;

    [Header("Tinh chỉnh chung")]
    [Tooltip("Bật nếu thấy nhân vật giơ nhầm tay trái/phải hoặc bị ngược trái-phải")]
    public bool flipX = false;
    [Tooltip("Bật nếu tay/chân bị lộn ngược trên-dưới")]
    public bool flipY = false;
    [Tooltip("Bật nếu cử động tiến/lùi (chiều sâu) bị ngược")]
    public bool flipZ = false;
    [Range(1f, 30f)] public float smoothing = 14f;          // càng lớn càng bám sát, càng nhỏ càng mượt
    [Range(0f, 1f)] public float minConfidence = 0.5f;       // bỏ qua khớp có độ tin cậy (visibility) thấp
    [Range(0f, 0.1f)] public float minLimbLength = 0.02f;    // bỏ qua chi quá ngắn (mét) - thường là nhiễu
    public bool driveTorso = true;

    [Header("Xoay hông/thân người (yaw)")]
    [Tooltip("Xoay cả nhân vật theo hướng vai người chơi (quay người thật thì nhân vật quay theo)")]
    public bool rotateRoot = true;
    [Range(1f, 30f)] public float rootRotationSmoothing = 8f;

    // Thứ tự 12 khớp gửi từ Python (RETARGET_ORDER trong action_server.py) - PHẢI khớp thứ tự này
    const int L_SHOULDER = 0, R_SHOULDER = 1, L_ELBOW = 2, R_ELBOW = 3, L_WRIST = 4, R_WRIST = 5;
    const int L_HIP = 6, R_HIP = 7, L_KNEE = 8, R_KNEE = 9, L_ANKLE = 10, R_ANKLE = 11;

    // Mỗi liên kết = 1 xương, hướng của nó được xác định bởi 2 khớp (từ -> đến)
    class Link
    {
        public HumanBodyBones bone;
        public HumanBodyBones childBone;
        public int kFrom, kTo;             // khớp dùng cho hướng
        public int kFrom2 = -1, kTo2 = -1; // nếu >= 0: hướng = trung điểm(kFrom,kFrom2) -> trung điểm(kTo,kTo2)

        [System.NonSerialized] public Transform t;
        [System.NonSerialized] public Quaternion restRotLocalToRoot; // xoay lúc nghỉ, tính trong hệ toạ độ của nhân vật
        [System.NonSerialized] public Vector3 restDirLocal;          // hướng xương->xương con lúc nghỉ, hệ toạ độ nhân vật
        [System.NonSerialized] public bool valid;
    }

    private Animator _animator;
    private Link[] _links;
    private float[] _kpts;      // keypoint mới nhất (48 số = 12 khớp x [x,y,z,vis]), null nếu chưa có / mất người
    private bool _hasPose;
    private Quaternion _restRootRotation; // hướng ban đầu của nhân vật (T-pose), dùng làm mốc "0 độ"
    private Vector3 _restForwardWorld;    // hướng vai lúc hiệu chỉnh (frame pose đầu tiên nhận được)
    private bool _rootCalibrated;

    void Awake()
    {
        _animator = GetComponent<Animator>();
    }

    void Start()
    {
        if (_animator.avatar == null || !_animator.isHuman)
        {
            Debug.LogError("[PoseRetargeter] Nhân vật cần Animator có Avatar kiểu Humanoid (Rig > Animation Type = Humanoid).");
            enabled = false;
            return;
        }

        _restRootRotation = transform.rotation;

        _links = new[]
        {
            // Thân: hướng từ trung điểm hông -> trung điểm vai. Phải xử lý TRƯỚC tay (tay là con của thân).
            new Link { bone = HumanBodyBones.Spine, childBone = HumanBodyBones.Neck, kFrom = L_HIP, kFrom2 = R_HIP, kTo = L_SHOULDER, kTo2 = R_SHOULDER },

            new Link { bone = HumanBodyBones.LeftUpperArm,  childBone = HumanBodyBones.LeftLowerArm,  kFrom = L_SHOULDER, kTo = L_ELBOW },
            new Link { bone = HumanBodyBones.LeftLowerArm,  childBone = HumanBodyBones.LeftHand,      kFrom = L_ELBOW,    kTo = L_WRIST },
            new Link { bone = HumanBodyBones.RightUpperArm, childBone = HumanBodyBones.RightLowerArm, kFrom = R_SHOULDER, kTo = R_ELBOW },
            new Link { bone = HumanBodyBones.RightLowerArm, childBone = HumanBodyBones.RightHand,     kFrom = R_ELBOW,    kTo = R_WRIST },

            new Link { bone = HumanBodyBones.LeftUpperLeg,  childBone = HumanBodyBones.LeftLowerLeg,  kFrom = L_HIP,  kTo = L_KNEE },
            new Link { bone = HumanBodyBones.LeftLowerLeg,  childBone = HumanBodyBones.LeftFoot,      kFrom = L_KNEE, kTo = L_ANKLE },
            new Link { bone = HumanBodyBones.RightUpperLeg, childBone = HumanBodyBones.RightLowerLeg, kFrom = R_HIP,  kTo = R_KNEE },
            new Link { bone = HumanBodyBones.RightLowerLeg, childBone = HumanBodyBones.RightFoot,     kFrom = R_KNEE, kTo = R_ANKLE },
        };

        // Ghi lại tư thế nghỉ (T-pose / A-pose ban đầu) của từng xương
        foreach (var l in _links)
        {
            l.t = _animator.GetBoneTransform(l.bone);
            Transform child = _animator.GetBoneTransform(l.childBone);
            if (l.t == null || child == null)
            {
                Debug.LogWarning($"[PoseRetargeter] Thiếu xương {l.bone} hoặc {l.childBone}, bỏ qua.");
                continue;
            }

            Vector3 restDirWorld = (child.position - l.t.position).normalized;
            l.restDirLocal = transform.InverseTransformDirection(restDirWorld);
            l.restRotLocalToRoot = Quaternion.Inverse(transform.rotation) * l.t.rotation;
            l.valid = true;
        }
    }

    void OnEnable()
    {
        if (receiver != null) receiver.OnActionReceived += OnPacket;
    }

    void OnDisable()
    {
        if (receiver != null) receiver.OnActionReceived -= OnPacket;
    }

    void OnPacket(ActionPacket p)
    {
        if (p.kpts != null && p.kpts.Length >= 48)
        {
            _kpts = p.kpts;
            _hasPose = true;
        }
        else
        {
            _hasPose = false; // không thấy người -> giữ nguyên tư thế cuối
        }
    }

    // LateUpdate chạy SAU Animator (và sau PlayerActionController.Update di chuyển vị trí),
    // nên tư thế + hướng xoay của ta ghi đè lên animation/di chuyển.
    void LateUpdate()
    {
        if (!_hasPose || _kpts == null || _links == null) return;

        float t = 1f - Mathf.Exp(-smoothing * Time.deltaTime); // hệ số làm mượt độc lập với FPS

        // 1) XOAY TOÀN THÂN theo hướng vai - làm TRƯỚC khi xoay tay/chân, vì tay/chân
        //    được quy đổi dựa trên hướng (trục) hiện tại của toàn thân.
        if (rotateRoot) UpdateRootRotation();

        // 2) Xoay từng xương tay/chân
        foreach (var l in _links)
        {
            if (!l.valid) continue;
            if (!driveTorso && l.bone == HumanBodyBones.Spine) continue;

            if (!TryGetDirection(l, out Vector3 dirLocal)) continue;

            // Xoay từ hướng lúc nghỉ tới hướng mục tiêu (tính trong hệ toạ độ nhân vật)
            Quaternion delta = Quaternion.FromToRotation(l.restDirLocal, dirLocal);
            Quaternion targetWorld = transform.rotation * (delta * l.restRotLocalToRoot);

            l.t.rotation = Quaternion.Slerp(l.t.rotation, targetWorld, t);
        }
    }

    // Xoay cả GameObject nhân vật sao cho hướng "mặt" khớp với hướng đường nối 2 vai
    // ngoài đời thật (hông/thân quay theo người chơi quay).
    void UpdateRootRotation()
    {
        if (!GetPoint(L_SHOULDER, -1, out Vector3 ls)) return;
        if (!GetPoint(R_SHOULDER, -1, out Vector3 rs)) return;

        Vector3 d = rs - ls; // mét, hệ camera: x phải, y xuống, z ~ chiều sâu
        float x = (flipX ? d.x : -d.x);
        float z = (flipZ ? -d.z : d.z);
        Vector3 rightWorld = new Vector3(x, 0f, z); // chỉ lấy mặt phẳng ngang (không nghiêng/roll)
        if (rightWorld.magnitude < minLimbLength) return;
        rightWorld.Normalize();

        Vector3 forwardWorld = Vector3.Cross(rightWorld, Vector3.up);
        if (forwardWorld.sqrMagnitude < 1e-6f) return;
        forwardWorld.Normalize();

        // Hiệu chỉnh ở frame pose đầu tiên: coi tư thế lúc đó là "0 độ", khớp với hướng
        // ban đầu (T-pose) của nhân vật - để không bắt người chơi phải đứng đúng 1 hướng cố định.
        if (!_rootCalibrated)
        {
            _restForwardWorld = forwardWorld;
            _rootCalibrated = true;
            return;
        }

        // Xoay thêm đúng bằng phần CHÊNH LỆCH so với lúc hiệu chỉnh, tính từ hướng gốc của nhân vật.
        Quaternion calibDelta = Quaternion.FromToRotation(_restForwardWorld, forwardWorld);
        Quaternion targetRot = calibDelta * _restRootRotation;

        float tr = 1f - Mathf.Exp(-rootRotationSmoothing * Time.deltaTime);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, tr);
    }

    // Lấy hướng 3D thật (hệ toạ độ nhân vật, tự thích nghi khi thân đã xoay) của 1 chi
    // từ 2 khớp world-landmark. False nếu độ tin cậy thấp / chi quá ngắn (nhiễu).
    bool TryGetDirection(Link l, out Vector3 dirLocal)
    {
        dirLocal = Vector3.zero;

        if (!GetPoint(l.kFrom, l.kFrom2, out Vector3 a)) return false;
        if (!GetPoint(l.kTo, l.kTo2, out Vector3 b)) return false;

        Vector3 d = b - a;             // mét, gốc MediaPipe: x phải, y xuống, z ~ chiều sâu so với hông
        if (d.magnitude < minLimbLength) return false;

        // MediaPipe world-landmark -> hệ toạ độ THẾ GIỚI cố định theo camera (nhìn về +Z, phải là +X, lên là +Y).
        float x = (flipX ? d.x : -d.x);
        float y = (flipY ? d.y : -d.y);
        float z = (flipZ ? -d.z : d.z);
        Vector3 dirWorld = new Vector3(x, y, z).normalized;

        // Quy đổi sang hệ toạ độ CỤC BỘ của nhân vật - tự động bù trừ khi toàn thân đã xoay
        // (UpdateRootRotation chạy trước đoạn này trong cùng LateUpdate).
        dirLocal = transform.InverseTransformDirection(dirWorld);
        return true;
    }

    // Lấy điểm 3D (hoặc trung điểm của 2 khớp nếu idx2 >= 0). False nếu độ tin cậy thấp.
    bool GetPoint(int idx, int idx2, out Vector3 p)
    {
        p = Vector3.zero;
        if (!Read(idx, out Vector3 p1)) return false;
        if (idx2 < 0) { p = p1; return true; }

        if (!Read(idx2, out Vector3 p2)) return false;
        p = (p1 + p2) * 0.5f;
        return true;
    }

    bool Read(int idx, out Vector3 p)
    {
        int o = idx * 4;
        p = new Vector3(_kpts[o], _kpts[o + 1], _kpts[o + 2]);
        return _kpts[o + 3] >= minConfidence;
    }
}