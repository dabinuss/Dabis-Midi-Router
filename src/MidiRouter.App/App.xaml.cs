using System.Windows;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MidiRouter.Core;
using MidiRouter.UI.DependencyInjection;
using MidiRouter.UI.ViewModels;
using Serilog;

namespace MidiRouter.App;

public partial class App : Application
{
    private IHost? _host;
    private string _appDataPath = string.Empty;

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

            var mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
            await mainViewModel.InitializeAsync();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application startup failed");
            MessageBox.Show(
                $"Die Anwendung konnte nicht gestartet werden:{Environment.NewLine}{ex.Message}",
                "Dabis Midi Router",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
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
            Log.CloseAndFlush();
        }

        base.OnExit(e);
    }
}
