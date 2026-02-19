using System.ComponentModel;
using System.Drawing;
using MidiRouter.UI.ViewModels;
using WinForms = System.Windows.Forms;

namespace MidiRouter.App;

internal sealed class BackgroundTrayController : IDisposable
{
    private readonly MainWindow _mainWindow;
    private readonly SettingsViewModel _settings;
    private readonly Action _requestExit;
    private readonly WinForms.NotifyIcon _notifyIcon;

    public BackgroundTrayController(
        MainWindow mainWindow,
        SettingsViewModel settings,
        Action requestExit)
    {
        _mainWindow = mainWindow;
        _settings = settings;
        _requestExit = requestExit;

        var menu = new WinForms.ContextMenuStrip();
        _ = menu.Items.Add("Dabis Midi Router oeffnen", null, (_, _) => ShowWindow());
        _ = menu.Items.Add("Komplett beenden", null, (_, _) => _requestExit());

        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "Dabis Midi Router",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = _settings.RunInBackground
        };

        _notifyIcon.DoubleClick += (_, _) => ShowWindow();
        _settings.PropertyChanged += OnSettingsChanged;
        ApplyAutoStart();
    }

    public bool ShouldKeepRunningInBackground => _settings.RunInBackground;

    public void HideWindowToBackground(bool showBalloonHint)
    {
        if (!_settings.RunInBackground)
        {
            return;
        }

        _mainWindow.Hide();
        EnsureTrayVisibility();

        if (showBalloonHint)
        {
            _notifyIcon.BalloonTipTitle = "Dabis Midi Router";
            _notifyIcon.BalloonTipText = "Die App laeuft im Hintergrund weiter.";
            _notifyIcon.ShowBalloonTip(2500);
        }
    }

    public void ShowWindow()
    {
        _mainWindow.Show();
        if (_mainWindow.WindowState == System.Windows.WindowState.Minimized)
        {
            _mainWindow.WindowState = System.Windows.WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.RunInBackground))
        {
            EnsureTrayVisibility();

            if (!_settings.RunInBackground && !_mainWindow.IsVisible)
            {
                ShowWindow();
            }
        }

        if (e.PropertyName is nameof(SettingsViewModel.AutoStartHost))
        {
            ApplyAutoStart();
        }
    }

    private void EnsureTrayVisibility()
    {
        _notifyIcon.Visible = _settings.RunInBackground;
    }

    private void ApplyAutoStart()
    {
        try
        {
            WindowsAutoStartManager.Apply(_settings.AutoStartHost);
        }
        catch
        {
            // Startup registration errors should not crash the UI host.
        }
    }

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
