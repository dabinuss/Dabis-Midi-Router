# MidiRouter

WPF app skeleton for a Windows MIDI Services based router/monitor.

## Stack

- .NET 8
- WPF
- MVVM Toolkit (`CommunityToolkit.Mvvm`)
- Serilog
- Hardcodet.NotifyIcon.Wpf

## Repository layout

```text
MidiRouter/
├── MidiRouter.sln
├── src/
│   ├── MidiRouter.App/
│   ├── MidiRouter.Core/
│   └── MidiRouter.UI/
└── tests/
    └── MidiRouter.Core.Tests/
```

## Current state

This repo contains a runnable phase-1 foundation:

- DI bootstrapped WPF host startup
- JSON config store under `%AppData%/MidiRouter/config.json`
- Route matrix model + filtering primitives
- Endpoint catalog abstraction with in-memory sample endpoints
- Basic Routing / Monitor / Settings views and viewmodels
- Unit tests for route filtering and config persistence

## Build and test

```powershell
dotnet restore .\MidiRouter.sln
dotnet build .\MidiRouter.sln
dotnet test .\MidiRouter.sln
```

## WMS SDK note

The package `Microsoft.Windows.Devices.Midi2` was not available from the default NuGet feed during setup.
The project therefore includes abstractions/placeholders for endpoint/session wiring. Add the official WMS SDK package/feed once available in your environment.

## Source concept

`koncept.md` in this repository mirrors your project plan.
