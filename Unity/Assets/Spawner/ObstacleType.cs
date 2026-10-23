// ObstacleType.cs
// 5 loại chướng ngại vật - mỗi loại ứng với đúng 1 hành động người chơi cần làm
// (map thẳng vào trạng thái/sự kiện đã có sẵn từ action_server.py).
public enum ObstacleType
{
    Jump,        // vật cản dưới đất -> phải nhảy qua
    Crouch,      // thanh chắn ngang đầu -> phải cúi xuống
    DodgeLeft,   // vật lao tới -> phải né sang TRÁI (đứng lệch trái) để tránh
    DodgeRight,  // vật lao tới -> phải né sang PHẢI (đứng lệch phải) để tránh
    PunchWall,   // bức tường -> phải đấm vỡ
}
