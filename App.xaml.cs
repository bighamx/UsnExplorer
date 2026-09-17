using System.Windows;

namespace UsnExplorer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 无头自检模式: 不创建窗口, 跑完直接退出。用于在没有 UI 的环境里验证引擎。
        if (e.Args.Length > 0 && e.Args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            var codes = e.Args.Skip(1).ToArray();
            Environment.ExitCode = SelfTest.Run(codes);
            Shutdown(Environment.ExitCode);
            return;
        }

        base.OnStartup(e);

        // 全局兜底: UI 线程未处理异常不要静默吞掉
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                args.Exception.ToString(),
                "UsnExplorer 未处理的错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
