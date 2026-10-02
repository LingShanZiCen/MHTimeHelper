using System.Diagnostics;
using System.Threading;
using System.Windows;
using Nightforge.Models;
using Nightforge.Services;

namespace Nightforge;

public partial class App : Application
{
    private const string MutexName = "Nightforge_SingleInstance_Mutex";
    private Mutex? _mutex;
    private bool _mutexReleased;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string[] args = e.Args ?? [];
        bool cleanupRun = HasArg(args, SilentUpdateService.CleanupArg);

        // 上一轮已经下好新版本时，先换文件再启动。
        // 必须赶在创建单实例互斥体之前完成：新版本是被本进程拉起来的，互斥体还占着就会被误判成「已经在运行」。
        if (!cleanupRun && TrySwapToPendingUpdate())
        {
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("幻夜工坊已经在运行了，请查看任务栏托盘图标。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        if (cleanupRun)
        {
            // 由旧版本拉起的新版本：后台清理上一版备份与 update 残留，不阻塞界面
            _ = System.Threading.Tasks.Task.Run(() => SilentUpdateService.CleanupOldFiles());
        }

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    /// <summary>
    /// 存在已下载好的新版本时完成替换。返回 true 表示替换成功（本进程应立即退出，新版本已拉起）。
    /// 任何异常都当作「这次不换」，照常启动旧版本，不影响用户使用。
    /// </summary>
    private static bool TrySwapToPendingUpdate()
    {
        try
        {
            AppConfig cfg = ConfigService.Load();
            if (!SilentUpdateService.HasPendingUpdate(cfg))
            {
                return false;
            }

            if (SilentUpdateService.ApplyPendingUpdate() is not null)
            {
                return false;
            }

            SilentUpdateService.ClearPendingState(cfg);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasArg(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 重启自身（新版本已下载完成时用）。先放开互斥体再拉起新进程，否则新进程会被当成重复运行。
    /// </summary>
    public static void RestartApplication()
    {
        if (Current is App app)
        {
            app.Restart();
        }
    }

    private void Restart()
    {
        ReleaseMutex();

        string? exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe))
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = ConfigService.AppDirectory
                });
            }
            catch
            {
                // 拉不起来就退出，用户再点一次图标同样会生效（待应用状态还在）
            }
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseMutex();
        base.OnExit(e);
    }

    private void ReleaseMutex()
    {
        if (_mutexReleased)
        {
            return;
        }

        _mutexReleased = true;
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // 忽略：进程退出时释放失败不影响结果
        }

        _mutex?.Dispose();
        _mutex = null;
    }
}
