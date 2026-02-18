namespace MidiRouter.Core.Config;

public sealed class ConfigStoreOptions
{
    public string ConfigPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DabisMidiRouter",
        "config.json");
}
