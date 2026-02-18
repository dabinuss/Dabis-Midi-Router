# Dabis Midi Router

WPF-Anwendung auf .NET 8 fuer MIDI-Routing und Monitoring unter Windows (Windows MIDI Services).

## Projektstruktur

```text
.
|-- MidiRouter.sln
|-- src/
|   |-- MidiRouter.App/
|   |-- MidiRouter.Core/
|   `-- MidiRouter.UI/
`-- tests/
    `-- MidiRouter.Core.Tests/
```

## Tech-Stack

- .NET 8
- WPF
- CommunityToolkit.Mvvm
- Serilog
- Hardcodet.NotifyIcon.Wpf

## Build und Test

```powershell
dotnet restore .\MidiRouter.sln
dotnet build .\MidiRouter.sln
dotnet test .\MidiRouter.sln
```

## Konfiguration

Die App speichert ihre Konfiguration standardmaessig unter:

`%AppData%/DabisMidiRouter/config.json`

## Projektkonzept

Die Planungsdatei liegt unter `concept.md`.
