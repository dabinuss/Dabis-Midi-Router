using System.Windows;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MidiRouter.Core;
using MidiRouter.UI.DependencyInjection;
using MidiRouter.UI.ViewModels;
using Serilog;

namespace MidiRouter.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\DabisMidiRouter.SingleInstance";
    private const string ActivationEventName = @"Local\DabisMidiRouter.Activate";

    private IHost? _host;
    private string _appDataPath = string.Empty;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private BackgroundTrayController? _trayController;
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private bool _exitRequested;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DabisMidiRouter");

            Directory.CreateDirectory(_appDataPath);
            Directory.CreateDirectory(Path.Combine(_appDataPath, "logs"));

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _singleInstanceCoordinator = new SingleInstanceCoordinator(SingleInstanceMutexName, ActivationEventName);
            if (!_singleInstanceCoordinator.TryAcquirePrimary(OnActivationRequested))
            {
                SingleInstanceCoordinator.SignalPrimaryInstance(ActivationEventName);
                Shutdown(0);
                return;
            }

            _host = Host.CreateDefaultBuilder()
                .UseSerilog((_, _, loggerConfiguration) =>
                {
                    loggerConfiguration
                        .MinimumLevel.Information()
                        .WriteTo.File(
                            Path.Combine(_appDataPath, "logs", "midirouter-.log"),
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: 14);
                })
                .ConfigureServices(services =>
                {
                    services.AddMidiRouterCore(options =>
                    {
                        options.ConfigPath = Path.Combine(_appDataPath, "config.json");
                    });

                    services.AddMidiRouterUi();
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            await _host.StartAsync();

            _mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
            await _mainViewModel.InitializeAsync();

            _mainWindow = _host.Services.GetRequiredService<MainWindow>();
            _mainWindow.Closing += OnMainWindowClosing;
            _trayController = new BackgroundTrayController(_mainWindow, _mainViewModel.Settings, RequestExit);

            var backgroundArg = e.Args.Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
            var startHidden = backgroundArg || _mainViewModel.Settings.StartMinimized;

            if (startHidden && _trayController.ShouldKeepRunningInBackground)
            {
                _trayController.HideWindowToBackground(showBalloonHint: false);
            }
            else
            {
                _mainWindow.Show();
                if (startHidden)
                {
                    _mainWindow.WindowState = WindowState.Minimized;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application startup failed");
            System.Windows.MessageBox.Show(
                $"Die Anwendung konnte nicht gestartet werden:{Environment.NewLine}{ex.Message}",
                "Dabis Midi Router",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);

            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            _trayController?.Dispose();
            _trayController = null;

            if (_host is not null)
            {
                if (_mainViewModel is not null)
                {
                    await _mainViewModel.FlushPendingSaveAsync();
                }

                await _host.StopAsync(TimeSpan.FromSeconds(3));
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Application shutdown failed");
            if (!string.IsNullOrWhiteSpace(_appDataPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.Combine(_appDataPath, "logs"));
                }
                catch
                {
                }
            }
        }
        finally
        {
            _singleInstanceCoordinator?.Dispose();
            _singleInstanceCoordinator = null;

            Log.CloseAndFlush();
        }

        base.OnExit(e);
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRequested)
        {
            return;
        }

        if (_trayController is not null && _trayController.ShouldKeepRunningInBackground)
        {
            e.Cancel = true;
            _trayController.HideWindowToBackground(showBalloonHint: true);
            return;
        }

        _exitRequested = true;
        Shutdown();
    }

    private void RequestExit()
    {
        _exitRequested = true;

        if (_mainWindow is not null)
        {
            _mainWindow.Closing -= OnMainWindowClosing;
            _mainWindow.Close();
        }

        Shutdown();
    }

    public void RequestFullExit()
    {
        RequestExit();
    }

    private void OnActivationRequested()
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_trayController is not null)
            {
                _trayController.ShowWindow();
                return;
            }

            _mainWindow?.Show();
            _mainWindow?.Activate();
        });
    }
}
