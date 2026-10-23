// UdpActionReceiver.cs
// Lắng nghe UDP từ action_server.py (Python + MediaPipe Pose), parse JSON,
// và phát sự kiện tư thế mới nhất cho PoseRetargeter.
//
// Gắn script này vào 1 GameObject bất kỳ trong scene (ví dụ: "NetworkManager").

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

[Serializable]
public struct ActionPacket
{
    public double ts;
    public float[] kpts;      // 12 khớp x [x, y, z, visibility] = 48 số (mét, gốc ở giữa hông - MediaPipe world landmarks)
    public float move_dx;     // mét, độ dịch ngang THỰC trong frame này (dương = sang phải theo ảnh camera)
    public float move_dz;     // mét, độ dịch sâu THỰC trong frame này (dương = lại gần camera)
    public bool crouch;       // trạng thái liên tục: đang cúi hay không
    public string[] events;   // sự kiện rời rạc trong frame này: "jump" | "punch" | "kick" (thường rỗng)
}

public class UdpActionReceiver : MonoBehaviour
{
    [Header("Kết nối")]
    public int listenPort = 5065;

    [Header("Trạng thái (chỉ đọc)")]
    public double lastPacketTime;
    public bool hasPose;

    // Sự kiện: PoseRetargeter đăng ký nghe khi có gói tin mới
    public event Action<ActionPacket> OnActionReceived;

    private UdpClient _udpClient;
    private Thread _receiveThread;
    private volatile bool _running;

    // Hàng đợi thread-safe để chuyển dữ liệu từ thread nền -> main thread
    private readonly ConcurrentQueue<ActionPacket> _queue = new ConcurrentQueue<ActionPacket>();

    void Start()
    {
        try
        {
            _udpClient = new UdpClient();
            // Cho phép bind lại cổng dù còn 1 socket cũ (từ phiên Play trước) chưa được hệ điều hành giải phóng hết.
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));
        }
        catch (Exception ex)
        {
            Debug.LogError(
                $"[UdpActionReceiver] Không mở được cổng {listenPort}: {ex.Message}\n"
                + "Kiểm tra xem có tiến trình Unity/script khác đang giữ cổng này không (xem netstat), "
                + "hoặc đổi sang listenPort khác."
            );
            return; // Không khởi động thread nếu bind thất bại
        }

        _running = true;
        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
        _receiveThread.Start();
        Debug.Log($"[UdpActionReceiver] Đang lắng nghe cổng {listenPort}...");
    }

    private void ReceiveLoop()
    {
        var endpoint = new IPEndPoint(IPAddress.Any, listenPort);
        while (_running)
        {
            try
            {
                byte[] data = _udpClient.Receive(ref endpoint);
                string json = System.Text.Encoding.UTF8.GetString(data);
                ActionPacket packet = JsonUtility.FromJson<ActionPacket>(json);
                _queue.Enqueue(packet);
            }
            catch (SocketException)
            {
                // Xảy ra khi Close() được gọi lúc đang Receive() -> bỏ qua khi đang dừng
                if (_running) Debug.LogWarning("[UdpActionReceiver] SocketException khi đang nhận dữ liệu.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UdpActionReceiver] Lỗi parse gói tin: {ex.Message}");
            }
        }
    }

    void Update()
    {
        // Xử lý tất cả gói tin đã nhận được kể từ frame trước, trên main thread
        while (_queue.TryDequeue(out ActionPacket packet))
        {
            lastPacketTime = packet.ts;
            hasPose = packet.kpts != null && packet.kpts.Length > 0;
            OnActionReceived?.Invoke(packet);
        }
    }

    void OnDestroy()
    {
        _running = false;
        _udpClient?.Close();      // làm Receive() thoát ra
        _receiveThread?.Join(200);
    }
}