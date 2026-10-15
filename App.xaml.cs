using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WeLinkMessageExporter.Services;

namespace WeLinkMessageExporter;

public partial class App : Application
{
    private int _showingError;
    private int _handledDispatcherErrors;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try { CrashReporter.Exception("AppDomain.UnhandledException", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString())); }
            catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashReporter.Exception("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
        DispatcherUnhandledException += HandleDispatcherException;
        CrashReporter.Trace("Application starting");

        try
        {
            base.OnStartup(e);
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            CrashReporter.Trace("MainWindow.Show completed");
        }
        catch (Exception ex)
        {
            CrashReporter.Exception("Application startup", ex);
            ShowError("程序启动失败", ex);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CrashReporter.Trace("Application exit code=" + e.ApplicationExitCode);
        base.OnExit(e);
    }

    private void HandleDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashReporter.Exception("DispatcherUnhandledException", e.Exception);
        // An accidental exception in a command handler must not terminate the entire app.
        e.Handled = true;
        if (Interlocked.Increment(ref _handledDispatcherErrors) <= 3)
            ShowError("界面运行异常（程序未自动退出）", e.Exception);
        else
            CrashReporter.Trace("Further error dialogs suppressed");
    }

    private void ShowError(string title, Exception ex)
    {
        if (Interlocked.Exchange(ref _showingError, 1) == 1) return;
        try
        {
            MessageBox.Show($"{ex.Message}\n\n详细堆栈已保存：\n{CrashReporter.FilePath}", title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* The UI itself might be unavailable. */ }
        finally { Interlocked.Exchange(ref _showingError, 0); }
    }
}
