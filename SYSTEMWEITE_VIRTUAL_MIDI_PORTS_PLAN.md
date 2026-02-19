# Plan: Systemweite virtuelle MIDI-Ports fuer Dabis Midi Router

Stand: 19. Februar 2026

## 1. Zielbild und harte Anforderungen

## Muss-Ziele
- Die App erzeugt echte, systemweit sichtbare virtuelle MIDI-Ports (nicht nur app-interne Platzhalter).
- Andere Programme (DAW, VST-Hosts, Tools) koennen die Ports direkt sehen und nutzen.
- Port-Erstellung, Umbenennen, Loeschen und Routing funktionieren stabil unter Last.
- Routing kann dauerhaft im Hintergrund laufen, auch wenn die UI geschlossen ist.
- Verhalten ist fuer Nutzer eindeutig (Port-Typ, Persistenzverhalten, Fehlerzustand).
- Keine Datenverluste, kein unkontrolliertes Feedback-Loop, keine UI-Freezes.

## Sicherheits- und Qualitaetsziele
- Sichere Eingabevalidierung (Portnamen, IDs, max. Anzahl Ports).
- Definierte Fehlergrenzen (Timeouts, Retries, Rollback auf letzte gueltige Konfiguration).
- Nachvollziehbares Logging/Auditing fuer Port-Lifecycle und Routing-Aktionen.
- Laststabilitaet bei hohem MIDI-Durchsatz.

## Nicht-Ziele
- Kein proprietaerer Kernel-Treiber-Eigenbau in diesem Projekt.
- Kein unsicheres Direkt-Patching unbekannter Systemdateien ohne API/Schema-Validierung.

---

## 2. Technische Fakten (verifiziert)

## Im aktuellen Code
- `CreateLoopbackEndpointAsync` erzeugt aktuell interne Loopback-Deskriptoren im Katalog (kein OS-Device-Create).
- `WinRtMidiSession` behandelt Loopback intern ueber Spiegelung (`PacketReceived`) statt als echte System-Endpoint-Registration.
- Ergebnis: Port ist aktuell kein global nutzbarer Windows-MIDI-Port fuer Drittprogramme.
- Beim Beenden der UI wird der Host gestoppt; dadurch endet Routing aktuell immer mit dem App-Fenster.

## Plattformlage (relevant fuer Architektur)
- Windows MIDI Services stellt ein modernes API/Service-Modell inkl. Loopback/Virtual-Device-Konzepte bereit.
- Klassische `Windows.Devices.Midi`-Nutzung alleine loest keine robuste, app-gesteuerte, moderne Virtual-Port-Verwaltung.
- Dokumentierte Besonderheit: transient erzeugte Loopback-Endpoints werden beim Service-Neustart entfernt; persistente Konfigurations-Loopbacks haben abweichende Lifecycle-Regeln.

---

## 3. Architekturentscheidung (entscheidend vor Implementierung)

Es gibt 3 realistische Wege. Vor der Hauptentwicklung muss einer verbindlich festgelegt werden.

## Option A: Windows MIDI Services App SDK (empfohlen als Primärpfad)
- Portverwaltung ueber MIDI Services APIs (Loopback/Virtual Device Manager).
- Systemweite Sichtbarkeit fuer andere Apps, sofern Endpoint-Typ und Lifecycle passend gewaehlt sind.
- Bessere Zukunftssicherheit (MIDI 2.0/UMP-Stack, offizieller Plattformpfad).

## Option B: Externer Virtual-Driver-Ansatz (Fallback)
- Integration gegen Drittanbieter-Treiber/Runtime.
- Kann robuste Persistenz liefern, aber bringt externe Abhaengigkeiten, Lizenz-/Support-Risiken.

## Option C: Weiterhin app-internes Loopback (nicht akzeptabel)
- Erfuellt Kernanforderung nicht.

## Entscheidungsvorschlag
- Primär: Option A.
- Fallback: Option B nur falls harte Persistenzanforderungen (z. B. Port muss ohne laufende App bestehen) mit Option A in der Zielumgebung nicht sauber erfuellbar sind.

---

## 4. Zielarchitektur (nach Umstellung)

## 4.1 Abstraktionsschicht
Neue zentrale Schnittstelle (oder erweiterte bestehende) fuer echte Virtual-Port-Faehigkeiten:
- `IVirtualMidiEndpointProvider`
- `Capabilities` (CanCreate, CanRename, CanDelete, PersistenceMode, RequiresServiceRuntime, RequiresAdminForPersistentConfig)
- Methoden:
  - `CreatePortAsync(...)`
  - `RenamePortAsync(...)`
  - `DeletePortAsync(...)`
  - `EnumeratePortsAsync(...)`
  - `GetHealthAsync(...)`

`IMidiEndpointCatalog` bleibt Konsum-Schicht fuer UI/Monitoring, bezieht Daten aber aus dem Provider.

## 4.2 Endpoint-Datenmodell erweitern
`MidiEndpointDescriptor` erweitern um:
- `BackendType` (WindowsMidiServices, LegacyWinRt, ExternalDriver)
- `Scope` (System, UserSession, AppHosted)
- `Persistence` (Transient, PersistedByService, PersistedByAppDefinition)
- `CanRename`, `CanDelete`, `CanRouteAsSource`, `CanRouteAsTarget`
- `NativeEndpointId` (plattformseitige ID)
- `DisplayDiagnostics` (optional, UI-freundlich)

## 4.3 Persistenzmodell erweitern
In `config.json` (oder separater Datei):
- `VirtualPorts[]`
  - `Id`
  - `Name`
  - `BackendType`
  - `DesiredState`
  - `PersistenceIntent` (z. B. `KeepAcrossAppRestart`, `KeepAcrossRebootIfSupported`)
  - `CreatedAt`, `LastKnownNativeId`
- Versionierung via `ConfigVersion` + Migrationspfad.

## 4.4 Routingkern
- Routing arbeitet nur mit aufgeloesten, gueltigen realen Endpoint-IDs.
- Bei Endpoint-Ausfall:
  - Route in `Degraded` markieren
  - automatische Rebind-Logik bei Wiedererscheinen
  - kein stilles Verwerfen ohne Status.

## 4.5 Dauerbetrieb (Background-Architektur)
- Architektur wird in zwei Prozesse getrennt:
  - `Control UI` (WPF) fuer Bedienung und Visualisierung.
  - `Routing Host` fuer dauerhaftes Routing ohne sichtbares Fenster.
- Der `Routing Host` wird als konfigurierbarer Modus umgesetzt:
  - `Tray-Background-Process` (Standard fuer Desktop-Nutzer).
  - `Windows Service` (optional fuer Always-On-Setups).
- Kommunikation UI <-> Host erfolgt ueber lokales IPC:
  - Named Pipes fuer Commands/Status.
  - Striktes Command-Schema (versioniert), keine dynamischen Ausdruecke.
- Exklusivitaet:
  - Genau eine aktive Host-Instanz (`single-host lock`).
  - UI verbindet sich nur auf laufende Instanz oder startet sie kontrolliert.
- Host-Lifecycle:
  - Start mit Windows/Login optional.
  - Graceful shutdown mit Config-Flush und Port-Cleanup-Policy.
  - Crash-Recovery mit Restart-Backoff.

---

## 5. Umsetzungsplan (vollstaendig, in Reihenfolge)

## Phase 0: Discovery-Spikes (Pflicht, bevor produktiver Umbau startet)
1. SDK-/Runtime-Kompatibilitaet pruefen
- Exakte Mindestversionen fuer Windows MIDI Services + App SDK definieren.
- Erkennen und sauberes Melden, falls Runtime fehlt/veraltet ist.

2. POC A (Loopback-Endpunkt)
- Per SDK-Endpunkt erstellen.
- Sichtbarkeit in mind. 3 Fremdprogrammen pruefen (z. B. MIDI-OX, REAPER, Ableton/Bitwig).
- Verhalten bei App-Neustart, Service-Neustart, Reboot messen.

3. POC B (Virtual Device)
- Erstellung, Enumerierung, Send/Receive, Lifetime verifizieren.
- Latenz und Stabilitaet unter Last testen.

4. Persistenzwahrheit feststellen
- Exakt dokumentieren, was auf dieser Plattformkombination wirklich persistent ist:
  - ueber App-Neustart
  - ueber Service-Neustart
  - ueber Reboot
  - ohne laufende App

5. Entscheidungsgate
- Architektur final fixieren (A-only oder A+B-Fallback).
- Entscheidung mit Messwerten festhalten.

## Deliverable Phase 0
- `docs/adr/ADR-virtual-midi-backend.md`
- Messprotokoll mit Screenshots/Fundstellen.

## Phase 1: Contract- und Config-Refactor
1. Domain-Modelle erweitern (`MidiEndpointDescriptor`, Config-Schema).
2. Migration implementieren (alte Config -> neue Config, ohne Datenverlust).
3. Capability-basierte UI-Felder vorbereiten (Rename/Delete nur falls erlaubt).
4. Legacy-Kompatibilitaet: bestehende Routen validiert uebernehmen, ungueltige in Warnliste.

## Phase 2: Windows MIDI Services Backend implementieren
1. Neues Backendmodul in `MidiRouter.Core/Engine/Backends/WindowsMidiServices`.
2. Endpoint-Discovery + Watcher robust anbinden.
3. Echte Port-Lifecycle-Operationen (Create/Rename/Delete) via SDK.
4. Fehlerklassen definieren:
- `BackendUnavailable`
- `PermissionDenied`
- `NameConflict`
- `UnsupportedPersistenceMode`
- `TransientLifecycleLimit`
5. Retries mit Backoff fuer temporäre Servicefehler.
6. Atomare Update-Reihenfolge: erst Backend-Aktion, dann Config-Persistenz, sonst Rollback.

## Phase 3: Session- und Routing-Integration
1. `WinRtMidiSession` modernisieren oder neue `WindowsMidiServicesSession` einfuehren.
2. Verbindungsmultiplexing:
- Pro Endpoint exakt eine Input-Subscription.
- Output-Handles poolen.
3. Backpressure:
- Bounded Channels statt unbounded bei hohen Datenraten.
- Messbare Dropping-Policy (nur falls unvermeidbar) mit Telemetrie.
4. Feedback-Loop-Schutz:
- Route-Ketten-/Echo-Erkennung (Source==Target direkt/indirekt).
- Optionaler Anti-Loop-Guard (TTL/Hop-Counter fuer intern geroutete Pakete).

## Phase 3b: Dauerbetrieb als Background-Host
1. Hosting splitten:
- `MidiRouter.Host` (headless) einfuehren.
- `MidiRouter.App` wird Control-UI mit Start/Attach-Logik.
2. IPC-Schicht:
- Command/Reply-Protokoll definieren (`GetState`, `ApplyConfig`, `CreatePort`, `Health`).
- Authentisierung auf lokale Benutzer-Session begrenzen.
3. Betriebsmodi:
- `Start minimiert in Tray` (Standard).
- `Autostart bei Login`.
- Optional `Windows Service Mode` mit separater Installation.
4. Reliability:
- Heartbeat zwischen UI und Host.
- Host-Selbstschutz bei Deadlocks (watchdog + dump/log marker).
5. Sicherheit:
- Servicekonto-Konzept (`LocalService` bevorzugt, keine unnötigen Admin-Rechte).
- Harter Schutz gegen unautorisierte IPC-Clients.

## Phase 4: UI/UX (Nutzerfuehrung und Transparenz)
1. Port-Erstellungsmasken mit Modusauswahl:
- `Systemweit (wenn unterstuetzt)`
- `App-gehostet`
2. Portliste zeigt klar:
- Typ-Badge (`System`, `Virtuell`, `App-gehostet`)
- Persistenz-Badge (`Persistent`, `Nur waehrend App aktiv`, `Transient`)
- Zustand (`Online`, `Degraded`, `Fehler`)
3. Konfliktdialoge:
- Namenskonflikte
- nicht verfuegbare Persistenzart
4. Betriebsstatus im Header:
- `Gespeichert`
- `Aenderungen ausstehend`
- `Backend nicht verfuegbar`

## Phase 5: Security Hardening
1. Eingabevalidierung
- Portname-Whitelist (Laenge, Steuerzeichen, reservierte Namen blocken).
- Maximalanzahl Ports pro Profil/gesamt.

2. Dateisicherheit
- Config und ggf. Endpoint-Store nur in erwarteten Verzeichnissen.
- Keine benutzerkontrollierten Pfade fuer writes.
- Atomare Schreibvorgaenge + Backup bei Parse-Fehlern.

3. Rechte/Privilegien
- Aktionen, die erhoehte Rechte brauchen, explizit kennzeichnen.
- Kein stilles Elevation-Verhalten.

4. Robustheit gegen schadhafte MIDI-Daten
- SysEx-Groessenlimits konfigurierbar.
- Defensive Parser fuer unbekannte/ungueltige Messages.

5. Logging/Audit
- Strukturierte Logs fuer Port-Lifecycle + Fehlversuche.
- Keine sensitiven Nutzerdaten in Logs.

## Phase 6: Performance-Engineering
1. Zielwerte definieren
- End-to-end Routing-Latenz (p50/p95/p99)
- CPU-Last bei definierten Message-Rates
- Speichergrenzen bei Dauerlast

2. Optimierungen
- Allokationen im Hotpath minimieren (`ArrayPool<byte>`, Reuse-Strategien).
- Keine Blocking-I/O im Message-Thread.
- Batch-Verarbeitung fuer UI-Log-Update (bereits teilweise vorhanden).

3. Benchmarks
- Synthetic MIDI load tests (Burst + sustained).
- Langzeittest (>= 8h) inkl. Port hot-plug.

## Phase 7: Teststrategie (vollstaendig)

## Unit-Tests
- Port lifecycle success/failure/rollback.
- Config migration tests (N->N+1).
- Capability mapping tests.

## Integrationstests (Windows only)
- Real backend smoke tests (create/list/rename/delete).
- Service-restart resilience.
- Reboot-survival testplan (manuell + dokumentiert).

## E2E-Tests
- App erstellt Port, DAW erkennt Port, Route aktiv, Daten fliessen korrekt.
- Gleichzeitige Port-Operationen + Routing unter Last.

## Negative Tests
- Backend nicht installiert.
- Zugriff verweigert.
- Beschädigte Config.
- Namenskonflikte / max. Portzahl erreicht.

## Regression-Tests
- Bestehende Hardwareports weiterhin sichtbar und routbar.
- Monitoring bleibt performant.

## Phase 8: Rollout, Migration, Betrieb
1. Feature Flag
- `VirtualPortBackend=Legacy|WindowsMidiServices|External`
- Schnell abschaltbar ohne Neuinstallation.
- `HostingMode=InProcess|TrayHost|WindowsService`
- `AutoStartHost=true|false`

2. Stufenrollout
- Canary build intern
- Pilot mit 3-5 realen DAW-Setups
- Dann breiter Release

3. Telemetrie/Observability
- Erfolgsquote Port-Creation
- Fehlercodes und Wiederholungen
- Durchschnittliche Routinglatenz

4. Rollback-Plan
- Bei kritischen Fehlern auf Legacy-ReadOnly-Modus zurueck (kein Port-Create, nur Routing existierender Ports).
- Config bleibt vorwaerts-/abwaertskompatibel durch Versioning und Backup.
- Bei Host-Problemen Fallback auf `InProcess`-Modus fuer schnellen Restore.

---

## 6. Detaillierte Akzeptanzkriterien

Ein Release ist nur dann freigabefaehig, wenn alle Punkte erfuellt sind:

1. Functional
- Erstellter virtueller Port ist in mindestens 3 Drittprogrammen sichtbar.
- Route von/zu virtuellem Port funktioniert ohne manuelle Nacharbeit.
- Rename/Delete verhalten sich konsistent zur gewaehlten Persistenzart.

2. Persistence
- Verhalten fuer App-Neustart, Service-Neustart, Reboot ist dokumentiert und entspricht UI-Anzeige.
- Keine "Geisterports" in Config oder UI.

3. Safety
- Kein Crash bei fehlender Runtime, sondern klarer Status + Handlungsanweisung.
- Keine stillen Datenverluste ohne Nutzerhinweis.

4. Performance
- p95 Latenz und CPU innerhalb vorher definierter Grenzwerte.
- Kein unbegrenztes Speicherwachstum im 8h-Dauerlauf.

5. UX
- Nutzer erkennt immer, ob Port systemweit/persistent/app-hosted ist.
- Save-State und Backend-Status sind jederzeit sichtbar und korrekt.

6. Dauerbetrieb
- Routing bleibt aktiv, wenn UI geschlossen wird (bei aktiviertem Host-Modus).
- Nach Benutzer-Login oder Systemstart kommt der Host in den definierten Zielzustand.
- UI kann sich jederzeit an laufenden Host anhaengen und aktuellen Zustand anzeigen.
- Kontrollierter Stop verhindert Konfigurationsverlust und haengende Endpoints.

---

## 7. Konkrete ToDo-Checkliste (umsetzungsbereit)

## Architektur/Backend
- [ ] ADR finalisieren (Backendwahl + Persistenzsemantik)
- [ ] Neue Provider-Schnittstellen einfuehren
- [ ] Windows MIDI Services Backend implementieren
- [ ] Optionaler Fallback-Backendadapter definieren

## Core
- [ ] Endpoint-Descriptor erweitern
- [ ] Config-Schema versionieren + Migrationen
- [ ] Routing-Worker mit Backpressure absichern
- [ ] Anti-Loop-Guards implementieren

## Background-Host
- [ ] Headless Host-Projekt anlegen (`MidiRouter.Host`)
- [ ] IPC-Protokoll und Versionierung definieren
- [ ] Single-Instance Lock + Heartbeat implementieren
- [ ] Tray-Host Modus mit Autostart umsetzen
- [ ] Optionalen Windows-Service-Modus implementieren
- [ ] Sichere Rechte-/Konto-Defaults und Installationsskript bereitstellen

## UI
- [ ] Portmodus-Auswahl + Hinweise
- [ ] Persistenz-/Typ-Badges
- [ ] Fehlerdialoge fuer Lifecycle-Operationen
- [ ] Backend-Health im Header

## Testing/QA
- [ ] Unit-Testpaket fuer Lifecycle/Migration
- [ ] Windows-Integrationstests mit realem Backend
- [ ] E2E-Matrix mit DAWs/Tools
- [ ] Last- und Langzeittests

## Betrieb
- [ ] Feature-Flag + Rollback
- [ ] Telemetrie/KPIs
- [ ] Release-Runbook und Known-Issues

---

## 8. Risiken und Gegenmassnahmen

- Risiko: Gewuenschte Persistenzart wird von gewaehltem Endpoint-Typ nicht garantiert.
  - Gegenmassnahme: Fruhes Discovery-Gate + klare UI-Semantik + ggf. Fallback-Backend.

- Risiko: Hohe Last fuehrt zu UI-Lag oder Paketverlust.
  - Gegenmassnahme: Bounded queue, batching, perf budgets, soak tests.

- Risiko: Backend/Service nicht vorhanden oder veraltet.
  - Gegenmassnahme: Runtime-Check beim Start, gefuehrte Fehlermeldung mit Upgrade-Hinweis.

- Risiko: Namenskonflikte/inkonsistente Endpoint-IDs.
  - Gegenmassnahme: deterministische ID-Mapping-Strategie + Conflict Resolver.

- Risiko: Host und UI laufen gleichzeitig in konkurrierenden Modi.
  - Gegenmassnahme: Single-Host-Lock, klarer Attach-Flow, harter Moduswechsel nur nach sauberem Stop.

- Risiko: IPC wird von falschem Prozess missbraucht.
  - Gegenmassnahme: lokale ACLs, Session-Bindung, erlaubte Command-Liste, keine Codeausfuehrung ueber IPC.

- Risiko: Service startet zu frueh/spaet und Ports fehlen beim DAW-Start.
  - Gegenmassnahme: definierte Startabhaengigkeiten, Retry mit Backoff, klarer Health-Status.

---

## 9. Empfohlene Umsetzungsreihenfolge (realistisch)

1. Phase 0 komplett abschliessen (ohne diesen Schritt kein Build-Out).
2. Phase 1+2 in einem Branch als vertikaler Slice (Create/List mit echtem Backend).
3. Phase 3 Routing-Integration mit Lasttests.
4. Phase 3b Background-Host umsetzen und stabilisieren.
5. Phase 4 UX-Finalisierung.
6. Phase 5-7 Hardening + QA-Freigabe.
7. Phase 8 Rollout in Wellen.

---

## 10. Referenzen (aktuell geprueft)

- Windows MIDI Services Hauptdoku:
  - https://microsoft.github.io/MIDI/
- Knowledge Base / Overview:
  - https://microsoft.github.io/MIDI/kb/overview/
- SDK Referenz (Namespace/Uebersicht):
  - https://microsoft.github.io/MIDI/sdk-ref/
- Loopback Endpoint Manager (SDK):
  - https://microsoft.github.io/MIDI/sdk-ref/class_microsoft_1_1_windows_1_1_devices_1_1_midi2_1_1_endpoints_1_1_loopback_1_1_midi_loopback_endpoint_manager.html
- Loopback Endpoint Creation Config (SDK):
  - https://microsoft.github.io/MIDI/sdk-ref/class_microsoft_1_1_windows_1_1_devices_1_1_midi2_1_1_endpoints_1_1_loopback_1_1_midi_loopback_endpoint_creation_config.html
- Virtual Device Manager (SDK):
  - https://microsoft.github.io/MIDI/sdk-ref/class_microsoft_1_1_windows_1_1_devices_1_1_midi2_1_1_endpoints_1_1_virtual_devices_1_1_midi_virtual_device_manager.html
- Release Notes / Known Issues:
  - https://github.com/microsoft/MIDI/releases
- UWP MIDI Einstieg (historischer API-Kontext):
  - https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/midi

