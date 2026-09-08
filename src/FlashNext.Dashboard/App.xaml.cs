using System.Threading;

namespace FlashNext.Dashboard;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private DashboardSession? _session;
    private TrayService? _tray;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, "FlashNext", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            args.Handled = true;
        };
        _mutex = new Mutex(true, @"Local\FlashNextManager.Manager", out bool first);
        if (!first)
        {
            System.Windows.MessageBox.Show("FlashNext is already running for this user.", "FlashNext", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            Shutdown(4);
            return;
        }

        _session = new DashboardSession();
        try
        {
            await _session.InitializeAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "FlashNext failed to start", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        _tray = new TrayService(_session, ShowWindow, QuitAsync);
        MainWindow window = new(_session);
        window.Closing += (_, args) =>
        {
            args.Cancel = true;
            window.Hide();
        };
        MainWindow = window;
        window.Show();
    }

    private void ShowWindow()
    {
        if (MainWindow is not System.Windows.Window window) return;
        window.Show();
        window.Activate();
        window.WindowState = System.Windows.WindowState.Normal;
    }

    private async void QuitAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
        _tray?.Dispose();
        _mutex?.Dispose();
        Shutdown();
    }

    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_session is not null) await _session.DisposeAsync();
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
