// ScoreManager.cs
// Đếm số lần né thành công / trúng chướng ngại vật + combo.
// Hiển thị tạm bằng OnGUI (góc trên trái) để test ngay, sau này thay bằng UI Canvas.
// Gắn vào 1 GameObject bất kỳ (ví dụ "GameManager").

using UnityEngine;

public class ScoreManager : MonoBehaviour
{
    public static ScoreManager Instance { get; private set; }

    public int hits;      // số lần vượt qua thành công
    public int misses;    // số lần bị trúng
    public int combo;
    public int bestCombo;
    public string lastResult = "";

    void Awake()
    {
        Instance = this;
    }

    public void ResetScore()
    {
        hits = 0;
        misses = 0;
        combo = 0;
        bestCombo = 0;
        lastResult = "";
    }

    public void Report(bool success, ObstacleType type)
    {
        if (success)
        {
            hits++;
            combo++;
            bestCombo = Mathf.Max(bestCombo, combo);
            lastResult = $"OK - {type}";
        }
        else
        {
            misses++;
            combo = 0;
            lastResult = $"TRÚNG - {type}";
        }
    }

    void OnGUI()
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
        style.normal.textColor = Color.white;
        GUI.Label(new Rect(15, 10, 600, 30), $"Né được: {hits}   Trúng: {misses}", style);
        GUI.Label(new Rect(15, 40, 600, 30), $"Combo: {combo} (cao nhất {bestCombo})", style);
        GUI.Label(new Rect(15, 70, 600, 30), lastResult, style);
    }
}