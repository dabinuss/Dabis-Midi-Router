namespace MidiRouter.Core.Config;

public sealed class AppConfig
{
    public string ActiveProfileName { get; set; } = "Default";

    public int LogBufferSize { get; set; } = 5000;

    public bool StartMinimized { get; set; }

    public HostingMode HostingMode { get; set; } = HostingMode.TrayHost;

    public bool AutoStartHost { get; set; }

    public List<RoutingProfile> Profiles { get; set; } =
    [
        new RoutingProfile { Name = "Default" }
    ];
}
