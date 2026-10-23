// PlayerActionController.cs
// Di chuyển nhân vật ĐÚNG BẰNG độ dịch chuyển thực tế của người chơi mỗi frame
// (move_dx/move_dz, tính bằng mét, do action_server.py suy ra từ vị trí + kích
// thước vai trên ảnh camera). Vì đây là ĐỘ DỊCH (delta) chứ không phải vận tốc
// theo trạng thái, đứng yên thì dx/dz = 0 -> nhân vật đứng yên theo, không còn
// trôi vô hạn về 1 hướng như bản dùng action rời rạc (move_left/move_right) trước đây.
//
// jump/punch/kick vẫn là sự kiện rời rạc (bắn 1 lần kèm cooldown từ phía Python).
// crouch là trạng thái liên tục, dùng để giảm tốc độ di chuyển lúc đang cúi.

using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PlayerActionController : MonoBehaviour
{
    [Header("Tham chiếu")]
    public UdpActionReceiver receiver;
    public Animator animator;            // có thể để trống nếu chưa có animation

    [Header("Vật lý")]
    public float gravity = -18f;
    public float jumpForce = 6f;

    [Header("Hiệu chỉnh chiều di chuyển")]
    [Tooltip("Bật nếu bước sang phải ngoài đời mà nhân vật lại đi sang trái")]
    public bool flipX = false;
    [Tooltip("Bật nếu tiến lại gần camera mà nhân vật lại lùi ra sau")]
    public bool flipZ = false;
    public float crouchSpeedMultiplier = 0.5f;

    // Trạng thái chỉ đọc - Obstacle dùng để chấm va chạm
    public bool IsCrouching { get; private set; }
    public bool IsGrounded => _controller.isGrounded;
    public float LastJumpTime { get; private set; } = -999f;
    public float LastPunchTime { get; private set; } = -999f;
    public float LastKickTime { get; private set; } = -999f;
    public float LastCrouchTime { get; private set; } = -999f;

    private CharacterController _controller;
    private Vector3 _velocity;
    private Vector3 _pendingMove; // cộng dồn move_dx/dz nhận được giữa 2 lần Update, áp dụng 1 lần cho mượt

    void Awake()
    {
        _controller = GetComponent<CharacterController>();
    }

    void OnEnable()
    {
        if (receiver != null) receiver.OnActionReceived += HandleAction;
    }

    void OnDisable()
    {
        if (receiver != null) receiver.OnActionReceived -= HandleAction;
    }

    // Được gọi mỗi khi có gói UDP mới (có thể vài lần giữa 2 frame Unity) - chỉ cộng dồn, áp dụng ở Update()
    private void HandleAction(ActionPacket p)
    {
        float dx = (flipX ? -p.move_dx : p.move_dx);
        float dz = (flipZ ? -p.move_dz : p.move_dz);
        float mult = p.crouch ? crouchSpeedMultiplier : 1f;
        _pendingMove += new Vector3(dx, 0, dz) * mult;

        if (p.events == null) return;
        foreach (var e in p.events)
        {
            switch (e)
            {
                case "jump":
                    LastJumpTime = Time.time;
                    if (_controller.isGrounded)
                    {
                        _velocity.y = jumpForce;
                        if (animator != null) animator.SetTrigger("Jump");
                    }
                    break;
                case "punch":
                    LastPunchTime = Time.time;
                    if (animator != null) animator.SetTrigger("Punch");
                    break;
                case "kick":
                    LastKickTime = Time.time;
                    if (animator != null) animator.SetTrigger("Kick");
                    break;
            }
        }

        IsCrouching = p.crouch;
        if (p.crouch) LastCrouchTime = Time.time;
        if (animator != null) animator.SetBool("IsCrouching", p.crouch);
    }

    void Update()
    {
        if (_controller.isGrounded && _velocity.y < 0)
            _velocity.y = -2f;
        _velocity.y += gravity * Time.deltaTime;

        Vector3 move = _pendingMove + new Vector3(0, _velocity.y, 0);
        _controller.Move(move);
        _pendingMove = Vector3.zero;
    }
}