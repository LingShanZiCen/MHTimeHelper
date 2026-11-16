using System.IO;
using System.Text;

namespace Nightforge.Services;

/// <summary>
/// 更新诊断日志：写在程序目录下的 update.log。
///
/// 为什么单独留一份：自动更新失败时，界面上那几行日志一关程序就没了，
/// 用户想反馈「为什么没更新成功」也没有凭据。这里把检查、下载、替换每一步都落盘，
/// 下次出问题直接看这个文件即可。写入失败（目录只读等）一律忽略，绝不影响正常使用。
/// </summary>
public static class UpdateLog
{
    /// <summary>日志文件路径（与 exe 同级）。</summary>
    public static string Path => System.IO.Path.Combine(ConfigService.AppDirectory, "update.log");

    /// <summary>超过这个体积就截断重来，避免无限增长。</summary>
    private const long MaxBytes = 512 * 1024;

    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                string path = Path;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Delete(path);
                }

                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 写不了就算了，不能让日志影响主流程
        }
    }
}
