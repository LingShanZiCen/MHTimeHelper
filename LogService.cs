using System.IO;
using System.Text;

namespace Nightforge.Services;

/// <summary>运行日志：全程内存累积，可导出为 UTF-8 文本文件。</summary>
public sealed class LogService
{
    private readonly List<string> _lines = [];
    private readonly object _lock = new();

    public event Action<string>? LineAdded;

    public void Add(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_lock)
        {
            _lines.Add(line);
        }

        try
        {
            LineAdded?.Invoke(line);
        }
        catch
        {
            // 界面未就绪时忽略
        }
    }

    public void AddSeparator()
    {
        string line = new('-', 46);
        lock (_lock)
        {
            _lines.Add(line);
        }

        try
        {
            LineAdded?.Invoke(line);
        }
        catch
        {
            // 同上
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_lock)
        {
            return _lines.ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _lines.Clear();
        }
    }

    public void Export(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("幻夜工坊 运行日志");
        sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('=', 46));

        foreach (string line in Snapshot())
        {
            sb.AppendLine(line);
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
}
