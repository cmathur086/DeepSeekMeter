using System.IO;

namespace DeepSeekMeter;

/// <summary>
/// 轻量诊断日志。
///
/// Release 构建会把 Debug.WriteLine 整体移除，出问题时应用内没有任何线索。
/// 这里保留一个滚动日志（超过 256 KB 就重开），只记录请求 URL 与状态码 —— 绝不记录 Token。
/// </summary>
internal static class Diag
{
    private static readonly object Gate = new();

    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepSeekMeter", "diag.log");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                var dir = System.IO.Path.GetDirectoryName(LogPath)!;
                System.IO.Directory.CreateDirectory(dir);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024)
                    File.Delete(LogPath);
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 诊断失败绝不影响主流程
        }
    }
}
