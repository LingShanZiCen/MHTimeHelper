using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Interop;

namespace Nightforge.Services;

/// <summary>全局热键：注册到主窗口句柄，通过 WM_HOTKEY 派发。</summary>
public sealed class HotkeyService : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly HwndSource? _source;
    private readonly Dictionary<int, (string Gesture, Action Action)> _actions = [];
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyService(System.Windows.Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>注册一条热键；失败时通过 error 返回原因。</summary>
    public bool Register(string gesture, Action action, out string error)
    {
        error = string.Empty;
        var (modifiers, vk) = Parse(gesture);
        if (vk == 0)
        {
            error = $"无法解析快捷键「{gesture}」";
            return false;
        }

        int id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_hwnd, id, modifiers | NativeMethods.MOD_NOREPEAT, vk))
        {
            error = $"快捷键「{gesture}」注册失败，可能已被其他程序占用";
            return false;
        }

        _actions[id] = (gesture, action);
        return true;
    }

    public void UnregisterAll()
    {
        foreach (int id in _actions.Keys.ToList())
        {
            NativeMethods.UnregisterHotKey(_hwnd, id);
        }

        _actions.Clear();
        _nextId = 1;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_actions.TryGetValue(id, out var entry))
            {
                handled = true;
                try
                {
                    entry.Action();
                }
                catch
                {
                    // 热键回调异常不应影响主循环
                }
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>把「Ctrl+Win+T」这类手势解析为 Win32 修饰符与虚拟键码。</summary>
    public static (uint Modifiers, uint VirtualKey) Parse(string gesture)
    {
        uint modifiers = 0;
        uint vk = 0;

        if (string.IsNullOrWhiteSpace(gesture))
        {
            return (0, 0);
        }

        foreach (string part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    break;
                case "ALT":
                    modifiers |= NativeMethods.MOD_ALT;
                    break;
                case "SHIFT":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= NativeMethods.MOD_WIN;
                    break;
                default:
                    vk = ToVirtualKey(part);
                    break;
            }
        }

        return (modifiers, vk);
    }

    private static uint ToVirtualKey(string key)
    {
        string k = key.Trim().ToUpperInvariant();
        if (k.Length == 0)
        {
            return 0;
        }

        if (k.Length == 1)
        {
            char ch = k[0];
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return ch;
            }
        }

        if (k.Length is 2 or 3 && k[0] == 'F' && int.TryParse(k[1..], out int fn) && fn is >= 1 and <= 24)
        {
            return (uint)(0x70 + fn - 1);
        }

        return k switch
        {
            "SPACE" or "SPACEBAR" => 0x20,
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "BACKSPACE" or "BKSP" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "`" or "~" => 0xC0,
            "-" or "_" => 0xBD,
            "=" or "+" => 0xBB,
            "[" or "{" => 0xDB,
            "]" or "}" => 0xDD,
            "\\" or "|" => 0xDC,
            ";" or ":" => 0xBA,
            "'" or "\"" => 0xDE,
            "," or "<" => 0xBC,
            "." or ">" => 0xBE,
            "/" or "?" => 0xBF,
            _ => 0
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();
        _source?.RemoveHook(WndProc);
    }
}

/// <summary>供界面展示用的辅助方法。</summary>
internal static class ProcessHelper
{
    public static void StartExternal(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir
        };
        Process.Start(psi);
    }
}
