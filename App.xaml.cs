using System.Threading;
using System.Windows;

namespace Nightforge;

public partial class App : Application
{
    private const string MutexName = "Nightforge_SingleInstance_Mutex";
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("幻夜工坊已经在运行了，请查看任务栏托盘图标。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // 忽略：进程退出时释放失败不影响结果
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
