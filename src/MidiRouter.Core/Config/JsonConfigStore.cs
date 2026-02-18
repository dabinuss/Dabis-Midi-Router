using System.Text.Json;

namespace MidiRouter.Core.Config;

public sealed class JsonConfigStore(ConfigStoreOptions options) : IConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigDirectory();

        if (!File.Exists(options.ConfigPath))
        {
            var defaultConfig = new AppConfig();
            await SaveAsync(defaultConfig, cancellationToken).ConfigureAwait(false);
            return defaultConfig;
        }

        await using var stream = File.OpenRead(options.ConfigPath);
        var config = await JsonSerializer.DeserializeAsync<AppConfig>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        return config ?? new AppConfig();
    }

    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        EnsureConfigDirectory();

        await using var stream = File.Create(options.ConfigPath);
        await JsonSerializer.SerializeAsync(stream, config, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private void EnsureConfigDirectory()
    {
        var directory = Path.GetDirectoryName(options.ConfigPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
