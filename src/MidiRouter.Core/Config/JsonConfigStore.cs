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

        try
        {
            await using var stream = File.OpenRead(options.ConfigPath);
            var config = await JsonSerializer.DeserializeAsync<AppConfig>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            return config ?? new AppConfig();
        }
        catch (JsonException)
        {
            BackupCorruptedConfig();
            var defaultConfig = new AppConfig();
            await SaveAsync(defaultConfig, cancellationToken).ConfigureAwait(false);
            return defaultConfig;
        }
    }

    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        EnsureConfigDirectory();

        var targetPath = options.ConfigPath;
        var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, config, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private void EnsureConfigDirectory()
    {
        var directory = Path.GetDirectoryName(options.ConfigPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private void BackupCorruptedConfig()
    {
        if (!File.Exists(options.ConfigPath))
        {
            return;
        }

        var backupPath = $"{options.ConfigPath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
        File.Move(options.ConfigPath, backupPath, overwrite: true);
    }
}
