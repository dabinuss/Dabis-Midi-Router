using MidiRouter.Core.Config;

namespace MidiRouter.Core.Tests;

public class JsonConfigStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsConfiguration()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "MidiRouterTests", Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(tempDirectory, "config.json");

        var store = new JsonConfigStore(new ConfigStoreOptions
        {
            ConfigPath = configPath
        });

        var config = new AppConfig
        {
            ActiveProfileName = "Studio",
            LogBufferSize = 7000,
            StartMinimized = true,
            Profiles =
            [
                new RoutingProfile
                {
                    Name = "Studio",
                    Routes =
                    [
                        new RouteConfig
                        {
                            SourceEndpointId = "loop:1",
                            TargetEndpointId = "loop:2",
                            Enabled = true,
                            Channels = [1, 2]
                        }
                    ]
                }
            ]
        };

        await store.SaveAsync(config);
        var loaded = await store.LoadAsync();

        Assert.Equal("Studio", loaded.ActiveProfileName);
        Assert.Equal(7000, loaded.LogBufferSize);
        Assert.True(loaded.StartMinimized);
        Assert.Single(loaded.Profiles);
        Assert.Single(loaded.Profiles[0].Routes);
        Assert.Equal("loop:1", loaded.Profiles[0].Routes[0].SourceEndpointId);
    }

    [Fact]
    public async Task LoadAsync_RecoversFromCorruptedJsonAndCreatesBackup()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "MidiRouterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var configPath = Path.Combine(tempDirectory, "config.json");

        await File.WriteAllTextAsync(configPath, "{ invalid json");

        var store = new JsonConfigStore(new ConfigStoreOptions
        {
            ConfigPath = configPath
        });

        var loaded = await store.LoadAsync();

        Assert.Equal("Default", loaded.ActiveProfileName);
        Assert.True(File.Exists(configPath));

        var backupFile = Directory
            .GetFiles(tempDirectory, "config.json.corrupt-*.bak")
            .SingleOrDefault();
        Assert.False(string.IsNullOrWhiteSpace(backupFile));
    }
}
