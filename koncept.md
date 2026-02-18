# MidiRouter – Projektplan

> WPF-App als Ersatz für loopMIDI + MIDI-OX, basierend auf **Windows MIDI Services**.

---

## Technologie-Stack

| Komponente | Technologie |
|---|---|
| Framework | .NET 8 + WPF |
| MIDI API | Windows MIDI Services SDK (NuGet: `Microsoft.Windows.Devices.Midi2`) |
| Virtuelle Ports | WMS Loopback Endpoints (kein Drittanbieter-Treiber nötig) |
| MVVM | CommunityToolkit.Mvvm |
| Tray Icon | Hardcodet.NotifyIcon.Wpf |
| Config | System.Text.Json → `%AppData%/MidiRouter/config.json` |
| Logging | Serilog |

---

## Architektur

```
┌──────────────────────────────────────────────────┐
│                    WPF UI (MVVM)                 │
│  ┌────────────┐  ┌────────────┐  ┌────────────┐ │
│  │  Routing    │  │  Monitor   │  │  Settings  │ │
│  │  Matrix     │  │  View      │  │  View      │ │
│  └──────┬─────┘  └──────┬─────┘  └──────┬─────┘ │
│         └────────────────┼───────────────┘       │
│                    ViewModel Layer                │
├──────────────────────────────────────────────────┤
│                    MidiEngine (Core)             │
│  ┌──────────────┐ ┌──────────┐ ┌──────────────┐ │
│  │ WMS Session  │ │ Router   │ │ Traffic      │ │
│  │ & Endpoint   │ │ Matrix   │ │ Analyzer     │ │
│  │ Manager      │ │          │ │              │ │
│  └──────────────┘ └──────────┘ └──────────────┘ │
├──────────────────────────────────────────────────┤
│           Windows MIDI Services API              │
│  ┌────────────┐ ┌────────────┐ ┌──────────────┐ │
│  │ Hardware   │ │ Loopback   │ │ Device       │ │
│  │ Endpoints  │ │ Endpoints  │ │ Watcher      │ │
│  └────────────┘ └────────────┘ └──────────────┘ │
└──────────────────────────────────────────────────┘
```

### Projekt-Struktur

```
MidiRouter/
├── MidiRouter.sln
├── src/
│   ├── MidiRouter.App/              # WPF Startup, DI, Tray, Single Instance
│   ├── MidiRouter.Core/             # Engine, Routing-Logik, Models
│   │   ├── Engine/                  #   MidiSession, Endpoint-Management
│   │   ├── Routing/                 #   Matrix, Channel-Filter, Message-Filter
│   │   ├── Monitoring/              #   Traffic-Stats, Message-Log
│   │   └── Config/                  #   Serialisierung, Profile
│   └── MidiRouter.UI/               # Views, ViewModels, Custom Controls
│       ├── Views/
│       ├── ViewModels/
│       └── Controls/                #   RoutingMatrixGrid, TrafficMeter
└── tests/
    └── MidiRouter.Core.Tests/
```

---

## Phase 1 – Core Engine & Grundgerüst

**Ziel:** App startet, verbindet sich mit WMS, zeigt Ports, Routing funktioniert.

### WMS-Integration
- MidiSession öffnen und Lifecycle managen
- Alle verfügbaren Endpoints auflisten (Hardware + Loopback)
- MidiEndpointDeviceWatcher für Hot-Plug (Geräte erkennen/entfernen zur Laufzeit)
- Loopback Endpoints erstellen und löschen über WMS API
- Endpoints öffnen, MIDI-Nachrichten empfangen und senden

### Routing Matrix
- Datenmodell: Liste von Routes (Source Endpoint → Target Endpoint)
- Nachrichtenweiterleitung im dedizierten Thread (nicht UI-Thread)
- Channel-Filter pro Route (Channels 1–16 einzeln aktivierbar)
- Message-Type-Filter pro Route (Note, CC, Program Change, Pitchbend, SysEx, Clock)

### App-Grundgerüst
- WPF-Projekt mit MVVM-Struktur (CommunityToolkit.Mvvm)
- Dependency Injection Setup
- Hauptfenster mit Navigation (Routing / Monitor / Settings)
- Config laden/speichern (JSON)

### Ergebnis Phase 1
MIDI-Daten fließen von Endpoint A nach Endpoint B mit konfigurierbaren Filtern. Virtuelle Ports können erstellt werden. Noch keine hübsche UI nötig – funktionale DataGrids reichen.

---

## Phase 2 – Monitoring & UI

**Ziel:** Live-Überwachung funktioniert, UI ist benutzbar und übersichtlich.

### Live Monitor
- Traffic-Statistik pro Endpoint: Messages/s, Bytes/s, aktive Channels
- Scrollender Message-Log mit Farbkodierung nach Typ:
  ```
  12:04:32.847  Loopback1  Ch.1   NoteOn    C4   Vel:92
  12:04:32.851  Loopback1  Ch.1   NoteOff   C4   Vel:0
  12:04:33.002  USB-Keys   Ch.10  CC#7      Val:64
  ```
- Ring-Buffer für Performance (kein unbegrenztes Wachstum)
- UI-Update gedrosselt (max 30fps), virtualisiertes ListView
- Pause/Resume, Clear, Filter nach Endpoint/Channel/Typ
- Export als CSV

### Routing Matrix UI
- Grid-Ansicht: Inputs als Spalten, Outputs als Zeilen, Checkboxen
- Pro Zelle aufklappbar: Channel-Filter, Message-Type-Filter
- Drag & Drop oder Rechtsklick-Menü für häufige Aktionen
- Visuelle Anzeige ob Daten fließen (Activity-Indikator pro Route)

### Port-Management UI
- Liste aller Endpoints (Hardware + Loopback) mit Status
- Loopback Endpoints erstellen/löschen/umbenennen
- WMS Health-Check beim Start (ist der Service aktiv?)

### Ergebnis Phase 2
Die App ist visuell fertig und benutzbar. Man sieht was passiert, kann Routing konfigurieren und Ports verwalten.

---

## Phase 3 – System-Integration & Polish

**Ziel:** App verhält sich wie ein professionelles System-Tool.

### Autostart & Tray
- System Tray Icon mit Kontextmenü (Show/Hide, Mute All, Exit)
- Minimiert in Tray starten (Kommandozeilen-Arg `--minimized`)
- Autostart via Registry (`HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`)
- Single Instance (Mutex) – zweiter Start bringt bestehendes Fenster in den Vordergrund

### Konfiguration & Profile
- Mehrere Routing-Profile speichern und wechseln
- Import/Export als `.midirouter`-Datei
- Config-Migration bei App-Updates
- Letzte Konfiguration beim Start wiederherstellen

### Klassische Programmfunktionen
- Settings-Dialog: Theme (Dark/Light), Tray-Verhalten, Log-Größe, Startup-Optionen
- About-Dialog mit Version
- Keyboard-Shortcuts für häufige Aktionen
- Error Handling: Saubere Fehlermeldungen wenn WMS nicht verfügbar
- Fallback-Hinweis für Windows 10 User (WMS nicht verfügbar)

### Feinschliff
- Alle Endpoints reconnecten nach Sleep/Resume
- Graceful Shutdown (alle Ports sauber schließen)
- Performance-Optimierung des Monitoring bei hohem Durchsatz
- Installer (MSIX oder Inno Setup)

### Ergebnis Phase 3
Fertige App. Startet mit Windows, sitzt im Tray, überlebt Sleep/Wake, lässt sich verteilen.

---

## WMS API – Wichtige Konzepte

| WMS Konzept | Verwendung in MidiRouter |
|---|---|
| `MidiSession` | Eine Session pro App-Instanz, Lifecycle an App gebunden |
| `MidiEndpointDeviceWatcher` | Hot-Plug: Erkennt neue/entfernte Geräte zur Laufzeit |
| `MidiEndpointDeviceInformation` | Metadaten der Endpoints (Name, Typ, Gruppen) |
| Loopback Endpoints | Ersatz für loopMIDI – virtuelle Ports über WMS API erstellen |
| UMP (Universal MIDI Packet) | Internes Nachrichtenformat – WMS übersetzt MIDI 1.0 ↔ UMP automatisch |
| Multi-Client | Jeder Endpoint kann von mehreren Apps gleichzeitig genutzt werden |
| Message Timestamps | Eingehende Nachrichten haben Timestamps für präzises Logging |

---

## Abhängigkeiten & Voraussetzungen

- Windows 11 24H2+ mit aktivem Windows MIDI Services
- .NET 8 Runtime
- Windows MIDI Services SDK Runtime & Tools (Out-of-Band Installation)
- Beim ersten Start: Prüfen ob WMS aktiv ist (`midicheckservice.exe` oder API-Check)