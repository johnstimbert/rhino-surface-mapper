# Technical Design: .NET / Blazor Port of Rhino Surface Mapper

**Status:** Approved — all open questions resolved 2026-10-02; implementation starts at Phase 0
**Target runtime:** .NET 10, Windows 10/11 x64
**Reference architecture:** `TheUnofficialWythevilleApp` (Clean Architecture, in-house CQRS mediator, FluentValidation, feature-per-file)
**Source of truth for behaviour:** the preserved Python application in [`python/`](../../python)

---

## Overview

Rhino Surface Mapper is a portable Windows desktop companion for *Elite Dangerous* Surface Mining. It reads live game telemetry (`Status.json` and the Journal), records SRV tracks, radar coverage, mining deposits, rigs and markers onto per-PML planetary maps, persists them as portable JSON, and provides navigation through a transparent always-on-top overlay plus optional steering assistance that injects short keyboard pulses.

This design ports that application to .NET 10 using the Clean Architecture layering of `TheUnofficialWythevilleApp`, with Blazor for the UI hosted natively on Windows through **WPF + `BlazorWebView` (Blazor Hybrid)**. The existing Python application is preserved unchanged under `python/` and remains the reference implementation until the port reaches parity.

The port is **behaviour-preserving**: every constant, threshold, geometric formula and JSON key documented in this design is taken from the Python implementation and must be reproduced exactly. The Python test suite is the acceptance specification.

### Confirmed decisions

| Decision | Choice | Rationale |
|---|---|---|
| UI hosting | WPF + `BlazorWebView` | Native Win32 window handle, unrestricted P/Invoke, trivially hosts a second native overlay window, no local HTTP server or SignalR circuit |
| Navigation overlay | Separate native WPF transparent window with custom drawing | Closest parity with the current `QPainter` overlay; retains per-pixel transparency, `Topmost`, non-activating display and 2.4:1 resize behaviour |
| Persistence | Portable JSON (`MAPAS/*.json`, `options.json`) | Existing user maps must keep loading and saving byte-compatibly; the app must remain xcopy-portable |
| Logging | `Microsoft.Extensions.Logging` + custom rolling-file provider | No third-party dependency; structured logging, a mediator logging pipeline behaviour, and durable on-disk diagnostics |
| Scope | Full parity in one design | Phased delivery is still proposed below, but every subsystem is designed up front |

### Non-goals

- Cross-platform support. The telemetry, input, injection and overlay subsystems are Windows-only by nature.
- A web-hosted/multi-user deployment. The Blazor UI is an in-process rendering technology only; no HTTP server, authentication or database is introduced.
- Changing the saved-map or settings file formats.
- Obstacle detection. The application observes radar input and draws passive coverage; it must never claim to detect obstacles or to prove a game pulse was fired.

---

## Requirements

### Functional parity requirements

| # | Requirement |
|---|---|
| F1 | Poll `Status.json`, accept only SRV telemetry (`Flags & 0x04000000`), and derive position, heading, fuel reservoir/percentage/low flag |
| F2 | Resolve authoritative system identity from the Journal (`Location`, `FSDJump`) incrementally |
| F3 | Detect whether `EliteDangerous64.exe` is running and whether it is the foreground process |
| F4 | Maintain the map session: trail recording, radar coverage, deposits, rigs, marks, search route, navigation target, PML identity |
| F5 | Generate and advance the circular search route (13 points, 3 500 m radius, configurable azimuth) |
| F6 | Record radar pulses expanding at a configurable wave speed and accumulate coverage discs |
| F7 | Persist and load maps as portable JSON with full backward compatibility, including legacy inference |
| F8 | PML identification, naming, versioning (`v2`, `v3`, …) and John-Doe (`JD1`, `JD2`, …) allocation |
| F9 | Favourite / protected flags written into the map file while preserving file mtime; protected maps open mining-only |
| F10 | Map Library: system search, favourite/protected filters, grouped tree, read-only preview, details, open/flag editing |
| F11 | Interactive map canvas: pan, zoom, recentre, marker hit-testing, context menus, rig click placement |
| F12 | Dialogs: deposit, marker, new map, save (replace / new version), open (version selection, protected-map choice) |
| F13 | Options panel with all persisted preference keys, section reset, export/import of settings documents |
| F14 | Transparent always-on-top navigation overlay with drag/resize at fixed 2.4:1 aspect ratio |
| F15 | Steering assistance: decision algorithm, `SendInput` pulse injection, manual-input detection, arrival braking, watchdog release |
| F16 | Direction-test (turn trial) mode with CSV results under `logs/` |
| F17 | Surface-mining market subsystem (Spansh acquisition, INARA summary parsing, policy filtering, ranking, cached summaries) |
| F18 | English (en-GB) and Portuguese (pt-PT) localisation, applied after restart |
| F19 | Windows / Dark / Light themes for the shell, the map canvas and the Map Library |

### Non-functional requirements

| # | Requirement |
|---|---|
| N1 | Portable: `MAPAS/`, `options.json` and `logs/` live beside the executable; no registry or user-profile writes |
| N2 | Telemetry loop sustains a 50 ms poll and 16 ms radar/assist cadence without UI stalls |
| N3 | Map rendering remains interactive with thousands of trail points and hundreds of coverage discs |
| N4 | Saved-map JSON remains byte-compatible with the Python writer (UTF-8, unescaped non-ASCII, 2-space indent) |
| N5 | Detailed structured logging to rolling files with configurable levels, bounded disk usage, and no secrets or personal data |
| N6 | Input injection is fail-safe: any doubt releases the key; the watchdog is independent of the UI thread |
| N7 | Deterministic, headless-testable core: domain and application layers have no WPF, WebView or Win32 dependency |

---

## Solution structure

```
rhino-surface-mapper/
├── python/                                     # preserved original PySide6 application
├── docs/design/                                # this document
├── RhinoSurfaceMapper.sln
├── global.json                                 # pins the .NET 10 SDK
├── src/
│   ├── RhinoSurfaceMapper.Domain/              # entities, value objects, domain services, repository interfaces
│   ├── RhinoSurfaceMapper.Application/         # mediator, features (commands/queries), validators, app services
│   ├── RhinoSurfaceMapper.Infrastructure/      # JSON persistence, telemetry readers, market clients, logging provider
│   ├── RhinoSurfaceMapper.Platform.Windows/    # Win32/WinMM interop behind application interfaces
│   ├── RhinoSurfaceMapper.UI.Components/       # Razor class library: shared Blazor components and pages
│   └── RhinoSurfaceMapper.Desktop/             # WPF host: BlazorWebView shell, native overlay window, composition root
└── tests/
    ├── RhinoSurfaceMapper.Domain.Tests/
    ├── RhinoSurfaceMapper.Application.Tests/
    ├── RhinoSurfaceMapper.Infrastructure.Tests/
    ├── RhinoSurfaceMapper.Platform.Windows.Tests/
    └── RhinoSurfaceMapper.UI.Components.Tests/   # bUnit
```

### Dependency rule

```
Domain              → (no project references)
Application         → Domain
Infrastructure      → Domain
Platform.Windows    → Application (interfaces only), Domain
UI.Components       → Application, Domain
Desktop             → all of the above
```

`Platform.Windows` is a separate project rather than part of `Infrastructure` because its contents are unit-untestable P/Invoke surfaces. Keeping it isolated means `Infrastructure` stays fully testable, and the interop project can be covered by a small set of integration-style tests only.

`UI.Components` deliberately holds the Blazor pages/components so the shell project contains only hosting, lifetime and native-window code — mirroring the reference app's `Web.UI.Components` / `Web.UI.Public` split.

---

## Affected layers

### `RhinoSurfaceMapper.Domain`

No external dependencies. Contains the Qt-free rules currently in `mapper_core.py`, `map_pml.py`, `radar.py`, `steering.py`, `turn_trial.py` and the market models.

#### Entities and aggregate

| Type | Kind | Notes |
|---|---|---|
| `MapSession` | mutable aggregate root | The port of `MapperState`. Holds identity, geometry origin, collections, search/navigation state and session-only flags |
| `TrailPoint` | `sealed record` | `X`, `Y`, `Lat`, `Lon`, `T`, `BreakBefore` |
| `Deposit` | `sealed record` | `Id`, `Name`, `Size`, `Rigs`, `X`, `Y`, `Lat`, `Lon` |
| `Rig` | `sealed record` | `Id`, `X`, `Y`, `Lat`, `Lon` |
| `MapMark` | `sealed record` | `Id`, `Name`, `X`, `Y`, `Lat`, `Lon` |
| `RadarCoverageDisc` | `sealed record` | `X`, `Y`, `Radius` |
| `RouteHistoryEntry` | `sealed record` | `Number`, `X`, `Y`, `Status` |
| `PmlIdentity` | `sealed record` | `Id`, `CenterLat`, `CenterLon` |
| `MapIdentity` | `sealed record` | `System`, `Body`, `BodyKey` |
| `NavigationTarget` | `sealed record` | Kind (`Mark`/`Deposit`/`Rig`/`RoutePoint`), `Name`, `X`, `Y` |

`Id` is a runtime-only `Guid` (app-generated with `Guid.NewGuid()`, matching the reference convention) used for component keys, hit-testing and edit/delete addressing. **It is never serialised**, because the Python map format identifies markers positionally.

`MapSession` is intentionally mutable. The Python `MapperState` is a mutable session object whose generation counter, pause state and consumed-sample flags are inherently stateful; modelling it as an immutable record would force a rewrite of behaviour this port must preserve. All *values* it holds are immutable records, so UI rendering can snapshot collections cheaply.

#### Enums

`DepositSize` (`Pequeno`, `Medio`, `Grande`, `Enorme` — see decision D7 below for the persisted representation), `RouteStatus` (`Reached`, `Skipped`), `SteeringStatus` (`Stopped`, `Waiting`, `Slowing`, `Correcting`, `OnCourse`, `Easing`), `MapOpenMode` (`Editable`, `MiningOnly`).

#### Domain services (pure, static or stateless)

| Service | Ports |
|---|---|
| `PlanetGeometry` | `LocalFromGeographic` / `GeographicFromLocal` (`llxy`/`xyll`), `SurfaceDistance` (haversine), `DestinationPoint`, `HeadingError`, `AngleDelta` |
| `TelemetryProcessor` | `process_status` rules → returns `StatusUpdate` and mutates the session |
| `SearchRouteCalculator` | `search_total_points`, `update_next`, `skip_next` |
| `OverlayNavigationCalculator` | `overlay_navigation` text/colour/arrow rules and blink timing |
| `MapValidator` | `validate_map` normalisation and range rules |
| `PmlRules` | `CorrespondsToMap`, `SafeFilenameComponent`, `PmlFileName`, `PmlPath`, `InferLegacyPml`, `MatchingCandidates`, `NewestByPml`, `NextJohnDoeId`, `NextVersionPath` |
| `RadarPulseEngine` | `RadarPulse.tick` state machine |
| `SteeringDecisionEngine` | `SteeringAssist` observe/decide state machine |
| `TurnTrialRecorder` | trial accumulation and validity rules |
| `MarketPolicy`, `MarketRanking`, `CommodityCatalog` | market filtering, ranking and name normalisation |

All constants live in `Domain/Constants/`:

```csharp
public static class MapperConstants
{
    public const int SrvFlag = 0x04000000;
    public const int FuelLowFlag = 0x00080000;
    public const double DefaultRadiusMetres = 6_371_000.0;
    public const double SearchRadiusMetres = 3_500.0;
    public const double SearchSpacingMetres = 1_800.0;
    public const double TrailMinimumSpacingMetres = 10.0;
    public const double TrailBreakDistanceMetres = 100.0;
    public const double RouteArrivalMetres = 100.0;
    public const double NavigationArrivalMetres = 100.0;
    public const double OverlayBlinkDistanceMetres = 300.0;
    public const double OverlayBlinkIntervalSeconds = 0.45;
    public const double DepositMinimumSeparationMetres = 80.0;
    public const int PmlMatchDistanceMetres = 13_000;
}
```

`RadarConstants.WaveSpeedMetresPerSecond = 2000.0 / 3.0`, `SteeringConstants` (tolerance 3°, max pulse 0.8 s, max speed 15 m/s, 2.5 s staleness, 0.25–30 s sample window, 20°/s instability, 100 m jump, 0.3 m/s minimum, 60° travel divergence, 0.25 s cooldown, `/46.5*0.7` base duration, the 1/1.5/2/3 aggression bands, 0.08 s floor) and `TurnTrialConstants` follow the same pattern.

#### Repository interfaces (`Domain/Interfaces/`)

```csharp
public interface IMapRepository
{
    Task<MapSession> LoadAsync(string path, CancellationToken ct = default);
    Task SaveAsync(MapSession session, string path, bool updateSavedAt = true, CancellationToken ct = default);
    Task<bool> IsProtectedAsync(string path, CancellationToken ct = default);
    Task SetFlagsAsync(string path, bool favorite, bool protectedFlag, CancellationToken ct = default);
    Task<(string CreatedAt, string LastSavedAt)> ReadTimestampsAsync(string path, CancellationToken ct = default);
    IReadOnlyList<string> EnumerateMaps(string systemName);
    IReadOnlyList<string> EnumerateSystems();
}

public interface IPreferencesRepository
{
    Task<IReadOnlyDictionary<string, object?>> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(IReadOnlyDictionary<string, object?> preferences, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, object?>> ReadExportAsync(string path, CancellationToken ct = default);
    Task WriteExportAsync(string path, IReadOnlyDictionary<string, object?> settings, CancellationToken ct = default);
}

public interface IMarketSummaryCacheRepository { /* load/save/freshness */ }
```

### `RhinoSurfaceMapper.Application`

References `Domain` plus `FluentValidation`, `Microsoft.Extensions.DependencyInjection.Abstractions` and `Microsoft.Extensions.Logging.Abstractions`.

#### Mediator

`Application/Mediator/` is ported verbatim from the reference app: `ICommand<TResult>`, `ICommandHandler<TCommand,TResult>`, `IQuery<TResult>`, `IQueryHandler<TQuery,TResult>`, `IPipelineBehavior<TInput,TOutput>`, `Mediator` (reverse-ordered onion wrapping) and `IMediator` with explicit two-parameter dispatch:

```csharp
var response = await _mediator.SendCommandAsync<SaveMap.Command, SaveMap.Response>(command, ct);
```

Handlers are registered explicitly in `AddApplication()`; FluentValidation validators are auto-scanned. Every handler validates first and throws `ValidationException` on failure, exactly as in the reference.

Two pipeline behaviours are registered, outermost first:

1. `LoggingPipelineBehavior<,>` — ported from the reference (Debug on entry, Information with elapsed ms and `Result`, Warning when the result name contains `Forbidden`/`Denied`). Payloads are never logged.
2. `ExceptionLoggingBehavior<,>` — logs `Error` with the input type name and rethrows, so a failure is recorded even when a Blazor component swallows it.

#### Feature inventory

Feature folders follow the reference single-file pattern (`Command`/`Query`, `Validator`, `Handler`, `Response`, `Result` inside one `static class`).

```
Features/
├── Telemetry/      ProcessStatusSample, ResolveJournalIdentity, SetStatusPath
├── MapSession/     NewMap, LoadMap, SaveMap, SaveMapVersion, GetMapSummary, EnterMiningMode, ResolveUnsavedChanges
├── Markers/        CreateDeposit, UpdateDeposit, DeleteDeposit, PlaceRig, DeleteRig, CreateMark, UpdateMark, DeleteMark
├── Search/         StartSearch, StopSearch, SkipNextRoutePoint, GetRouteStatus
├── Navigation/     StartNavigation, StopNavigation, GetOverlayNavigation
├── Radar/          TickRadarPulse, GetRadarStatus
├── Pml/            IdentifyPml, CreatePml, ListPmlVersions, AllocateJohnDoeId
├── Library/        SearchSystems, GetLibraryTree, GetMapPreview, GetMapDetails, SetMapFlags
├── Preferences/    GetPreferences, UpdatePreference, ResetSection, ExportSettings, ImportSettings
├── Steering/       ToggleAssistance, EvaluateSteering, StartDirectionTest, GetAssistanceStatus
└── Market/         GetSurfaceMiningMarkets, RefreshGlobalSummary
```

Representative contract:

```csharp
public static class SaveMap
{
    public sealed record Command : ICommand<Response>
    {
        public required SaveMode Mode { get; init; }          // Replace | NewVersion | ExplicitPath
        public string? ExplicitPath { get; init; }
    }

    public sealed class Validator : AbstractValidator<Command> { /* path required for ExplicitPath */ }

    public sealed class Handler : ICommandHandler<Command, Response> { /* orchestration only */ }

    public sealed record Response
    {
        public required string? Path { get; init; }
        public required Result Result { get; init; }
    }

    public enum Result { Success, ReadOnly, NoMapCentre, PathRequired, WriteFailed }
}
```

Every handler returns a `Result` enum instead of throwing for expected outcomes — this is what drives both the UI state machine and the logging behaviour's level selection. Unexpected faults still throw.

#### Application services and interfaces (`Application/Interfaces/`)

| Interface | Implemented by | Purpose |
|---|---|---|
| `IMapSessionStore` | Application (singleton) | Owns the current `MapSession`, its `MapGeneration`, the current file path and a `SemaphoreSlim` guarding mutations |
| `IMapSessionNotifier` | Application | Publishes `SessionChanged`, `TelemetryUpdated`, `RadarChanged`, `NavigationChanged` events consumed by the UI and overlay |
| `IStatusTelemetryReader` | Infrastructure | `TryReadIfChanged(path, previousMtimeNs, force)` |
| `IJournalIdentityReader` | Infrastructure | Incremental identity reader |
| `IGameProcessService` | Platform.Windows | `IsGameRunning()`, `IsGameFocused()` |
| `IGameInputReader` | Platform.Windows | `IsPrimaryFireDown()`, `IsManualSteeringActive()`, bindings load |
| `IInputInjector` | Platform.Windows | `Pulse(direction, duration)`, `Brake()`, `ReleaseAll()` |
| `IClock` | Infrastructure | `UtcNow`, `MonotonicSeconds` — makes every timing rule testable |
| `IAppPaths` | Infrastructure | `BaseDirectory`, `MapsDirectory`, `OptionsPath`, `LogsDirectory` |
| `IMarketDataSource` | Infrastructure | Spansh/INARA acquisition |

`IClock` is required: the Python code reads `time.monotonic()` and `time.time()` directly in the radar, steering, overlay-blink and turn-trial algorithms. Injecting the clock is what makes those algorithms unit-testable to the same precision as the Python tests.

### `RhinoSurfaceMapper.Infrastructure`

References `Domain` only, plus `System.Text.Json` and `Microsoft.Extensions.*`.

#### Persistence

- `JsonMapRepository` — implements `IMapRepository`. Reads/writes through DTO records (`MapDocument`, `TrailPointDto`, …) so the on-disk shape is explicit and independent of the domain model.
  - Writes atomically: temp file in the destination directory → `File.Move(temp, target, overwrite: true)` (the `os.replace` equivalent) → temp cleanup in `finally`.
  - `SetFlagsAsync` restores `LastWriteTimeUtc`/`LastAccessTimeUtc` after the rewrite, matching `os.utime(..., ns=...)` semantics; .NET preserves 100 ns ticks which is sufficient for the library's "unchanged mtime" behaviour.
  - Refuses to save when the session is read-only or when the existing file has `"protected": true`, throwing `UnauthorizedAccessException` (the `PermissionError` equivalent).
- `JsonPreferencesRepository` — `options.json`; malformed/missing files degrade to an empty dictionary; export/import enforce `rhino_settings_version == 1` with a non-empty `settings` object and accept a UTF-8 BOM.
- `JsonSummaryCacheRepository` — versioned market summary cache with the strict `age < maxAge` freshness rule and atomic writes including `FileStream.Flush(flushToDisk: true)` for the `os.fsync` equivalent.

Shared serializer options guarantee N4:

```csharp
internal static readonly JsonSerializerOptions MapJson = new()
{
    WriteIndented = true,                                         // 2-space indent, matching json.dump(indent=2)
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,        // matching ensure_ascii=False
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
};
```

Numeric formatting must be invariant-culture and must round-trip doubles (`R`/shortest-round-trippable, which is the `System.Text.Json` default) so maps written by either implementation remain interchangeable.

#### Telemetry

- `StatusFileReader` — `IStatusTelemetryReader`. Change detection compares `File.GetLastWriteTimeUtc(...).Ticks` against the previous value (the `st_mtime_ns` analogue), reads the whole file as UTF-8 and parses with `JsonDocument`. Partial writes surface as `JsonException`; missing files surface as `FileNotFoundException`. No retry, no locking — identical to the Python contract, with the caller deciding how to react.
- `JournalIdentityReader` — latest `Journal.<ts>.<seq>.log` selection by (parsed timestamp, sequence, mtime); byte-offset incremental reads; `_pending` fragment retry for a truncated final line; file-shrink reset; only `Location` and `FSDJump` update identity; malformed records are skipped and logged at `Debug`.

#### Market

- `SpanshMarketClient` using a typed `HttpClient` (`BaseAddress = https://spansh.co.uk/api`, `Timeout = 25 s`) registered with `AddHttpClient<>` plus a resilience handler configured for transient faults only. Endpoints: `POST /systems/search` (page size 100, exact case-insensitive name match, count/short-page termination rules), `GET /system/{id64}`, `GET /station/{marketId}`. Fatal failures throw `SpanshException`; station-level failures become `MarketIssue` records.
- `InaraSummaryParser` — HTML parsing of columns 0/1/4 using **`AngleSharp`** (decision D3). The hand-written tokeniser of `inara.py` is not reproduced: INARA's markup changes without notice, and a real HTML parser fails predictably where a tokeniser fails silently. `AngleSharp` is confined to this one type so the dependency stays replaceable.
- `CommodityCatalog` — loads `SURFACE_MINING_COMMODITIES.json` (37 records, strict schema validation → `CatalogueFormatException`) and implements `NormalizeName` with `string.Normalize(NormalizationForm.FormKD)`, combining-mark stripping, alphanumeric filtering and `ToLowerInvariant`, plus the two canonical aliases.

#### Logging provider

`Infrastructure/Logging/` contains the rolling-file provider described in the dedicated section below.

### `RhinoSurfaceMapper.Platform.Windows`

All P/Invoke uses `LibraryImport` source-generated partial methods in `internal static partial class` wrappers, grouped per native library, with `SupportedOSPlatform("windows")`.

| Component | Native surface |
|---|---|
| `GameProcessService` | `CreateToolhelp32Snapshot` / `Process32FirstW` / `Process32NextW` / `CloseHandle` for the exact `EliteDangerous64.exe` match; `GetForegroundWindow`, `GetWindowThreadProcessId`, `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`, `QueryFullProcessImageNameW` for focus, with the foreground-handle cache |
| `BindingsFileReader` | Parses `%LOCALAPPDATA%\Frontier Developments\Elite Dangerous\Options\Bindings`: `StartPreset.4.start` → preset name → highest `MajorVersion`/`MinorVersion`/mtime `.binds` file → `BuggyPrimaryFireButton` primary/secondary bindings with modifiers |
| `KeyboardMouseStateReader` | `GetAsyncKeyState` with the documented virtual-key map (`Mouse_1`→1 … `Mouse_5`→6, letters/digits, `F1`–`F24` → `111 + n`, navigation keys); unsupported controls return null |
| `JoystickStateReader` | `joyGetNumDevs`, `joyGetDevCapsW`, `joyGetPosEx` over at most 16 devices, `JOY_RETURNALL = 0x00FF`, buttons 1–32 tested as `buttons & (1 << (n-1))`, name normalisation and the T.16000M (`VID 0x044F`/`PID 0xB10A`) and TWCS (`PID 0xB687`) special cases |
| `SendInputInjector` | `MapVirtualKeyW(vk, MAPVK_VK_TO_VSC)`, `SendInput` with `KEYEVENTF_SCANCODE (0x8)`, `KEYEVENTF_KEYUP (0x2)`, `KEYEVENTF_EXTENDEDKEY (0x1)` for VKs 33–40/45/46; requires a return value of exactly 1 |
| `LowLevelKeyboardMonitor` | `SetWindowsHookExW(WH_KEYBOARD_LL = 13)` on a dedicated STA-free thread with its own `PeekMessageW`/`TranslateMessage`/`DispatchMessageW` pump; ignores `LLKHF_INJECTED (0x10)`; always calls `CallNextHookEx` and never blocks input |
| `InjectionWatchdog` | Independent 5 ms timer thread that releases any held key on deadline expiry, focus loss, manual input or UI stall |

The hook callback delegate is stored in a static field to prevent collection, and the hook is unhooked in a `SafeHandle`-based finaliser path plus explicit shutdown.

Every injection precondition from the Python implementation is enforced in `SendInputInjector.CanInject(out reason)` and the refusal reason is logged at `Warning`: not Windows, hook unavailable, bindings unavailable, missing/duplicate steering keys, F8 conflicts, modifier or mouse steering, unreadable joystick axis/limits, game not focused, key already held, no scan code, `SendInput` != 1.

### `RhinoSurfaceMapper.UI.Components`

A Razor class library (`Microsoft.NET.Sdk.Razor`) holding every page and component. MudBlazor is **not** adopted: the reference app's component library targets a public web product, whereas this UI is a dense desktop tool whose dominant surface is a custom canvas. Plain Razor components with a scoped CSS design system keep the WebView payload small and the rendering predictable (decision D1).

```
Components/
├── Layout/            MainLayout, ThemeProvider, TitleBar
├── Map/               MapCanvas, MapCanvasInterop, MapContextMenu, MapFooterBadges, MapToolbar
├── Dialogs/           DepositDialog, MarkDialog, NewMapDialog, SaveMapDialog, OpenMapDialog, ProtectedMapDialog
├── Library/           MapLibrary, LibrarySearch, LibraryTree, MapPreview, MapDetails
├── Options/           OptionsPanel, OptionsSection, ColourField, MetresField, DegreesField
├── Steering/          AssistanceBar, DirectionTestPanel
├── Status/            InformationBar, RadarStatusBar, FuelIndicator
└── Services/          IMapRenderService, ThemeService, UiDispatcher
```

#### Map canvas

The map is rendered with an **HTML5 Canvas 2D context driven over JS interop**, not SVG and not individual Blazor elements:

- A render tick builds an immutable `MapScene` DTO (background/grid/coverage/trail/markers/overlays, already projected to world metres with style values resolved from the theme).
- The scene is pushed to a JS module (`map-canvas.js`) through `IJSObjectReference.InvokeVoidAsync`, serialised as packed `Float64Array`/`Float32Array` payloads for the high-cardinality arrays (trail points, coverage discs) to avoid per-point JSON overhead.
- JS owns pan/zoom gestures and the `requestAnimationFrame` draw loop, calling back into .NET only for semantic events (click, context-menu request, cursor world coordinates, zoom level changes).
- The world↔screen transform is implemented in both places from the same constants: `screenX = width/2 + (worldX - centerX) * scale`, `screenY = height/2 - (worldY - centerY) * scale`, initial scale `0.08`, zoom clamp `0.001–10`, wheel factor `1.15^(delta/120)` anchored under the cursor. The .NET copy is the authority used for hit-testing and tests.

Hit-testing stays in .NET (`MapHitTester` in Domain) so the Python rules are preserved exactly: marks first, deposit cards by full label bounds, then deposits/rigs/route points by nearest distance under 14 px, and the next search target.

Rationale for Canvas over SVG: the trail commonly holds thousands of points and the coverage layer hundreds of translucent discs; an SVG/DOM approach would create a node per primitive and collapse under the 20 Hz refresh. Canvas also matches the painter-ordered algorithm already proven in `MapView.paintEvent`.

#### Rendering cadence

The UI does not re-render on every telemetry sample. `IMapSessionNotifier` events are coalesced by a `PeriodicTimer`-driven presenter at ~20 Hz (50 ms) for the map and ~62 Hz (16 ms) for the overlay/assistance bar. Blazor `StateHasChanged` is only invoked for discrete state changes (enablement, labels, badges); continuous geometry goes straight to the canvas module.

### `RhinoSurfaceMapper.Desktop` (WPF host)

```
Desktop/
├── App.xaml / App.xaml.cs          # composition root, global exception handlers, single-instance guard
├── MainWindow.xaml                 # hosts <blazor:BlazorWebView>
├── Overlay/
│   ├── NavigationOverlayWindow.xaml
│   └── OverlayRenderer.cs          # DrawingContext painting
├── Hosting/
│   ├── TelemetryHostedService.cs   # 50 ms status poll + journal identity
│   ├── RadarHostedService.cs       # 16 ms radar input + pulse tick
│   ├── SteeringHostedService.cs    # 16 ms assistance loop
│   └── ShutdownCoordinator.cs
└── wwwroot/                        # index.html, css, map-canvas.js
```

- `Host.CreateApplicationBuilder` builds the DI container; `AddApplication()`, `AddInfrastructure(configuration)`, `AddWindowsPlatform()` and `AddWpfShell()` are the four registration entry points, mirroring the reference's `AddApplication()`/`AddInfrastructure()` convention.
- Configuration comes from `appsettings.json` beside the executable, then `options.json` (user preferences layered through a custom `IConfigurationSource`), then environment variables. Logging levels are therefore adjustable without a rebuild.
- `BlazorWebView` with `HostPage = "wwwroot/index.html"` and `RootComponents.Add<Routes>("#app")`.
- The three hosted services are `BackgroundService`s using `PeriodicTimer`; they execute off the UI thread, mutate the session under the store's semaphore, and marshal UI notifications through `UiDispatcher` (`Dispatcher.InvokeAsync` / `ComponentBase.InvokeAsync`).
- The game-process check is throttled to once per second, as today.

#### Navigation overlay window

```xml
<Window WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ResizeMode="NoResize"
        Width="360" Height="150" />
```

- `WS_EX_NOACTIVATE` is applied in `OnSourceInitialized` to reproduce `WA_ShowWithoutActivating`. The window is deliberately **not** click-through — matching current behaviour and the overlay tests.
- Drag and eight-direction resize are implemented over global mouse coordinates with the 2.4:1 ratio enforced and a 180 px minimum width (height `round(180/2.4)`).
- Painting is done in `OnRender` with `DrawingContext`: near-transparent fill, 2 px red border while hovered/dragging/resizing, target name in `#80C8FF`, heading and distance in the colours produced by `OverlayNavigationCalculator` (`#00CC44` ≤2°, `#FFD21C` ≤8°, `#FF3030` otherwise), arrow runs of three or five, blink at 0.45 s inside 300 m, and the assistance notice band (`#FFD21C` warning / `#A8D7EB` normal).
- Opacity and width follow the `overlay_opacity` and `overlay_width` preferences; position/size remain session state, as today.
- Visibility is driven solely by `MapSession.OverlayAllowed` plus overlay mode and pending-transition state — never by window focus.

---

## Data model and file compatibility

### Saved map document

The DTO writes exactly these keys, in this order, always:

```json
{
  "system": "", "body": "",
  "created_at": null, "last_saved_at": null,
  "favorite": false, "protected": false,
  "pml_id": "", "pml_center_lat": null, "pml_center_lon": null,
  "center_lat": 0.0, "center_lon": 0.0,
  "planet_radius": 6371000.0,
  "coverage_width_m": 2000.0, "scanner_range_m": 2000.0,
  "search_started": false,
  "datum_lat": null, "search_azimuth": 0, "datum_lon": null,
  "route_index": 0, "route_history": [],
  "points": [], "radar_coverage": [],
  "deposits": [], "rigs": [], "marks": []
}
```

Session-only fields (`mining_only`, `map_generation`, telemetry, navigation, `status_path`, `center_enabled`, overlay state) are never written.

Read-side defaults for absent keys: `system`/`body` `""`; `created_at`/`last_saved_at` `null`; `favorite`/`protected` `false`; `pml_id` `""`; `planet_radius` `6371000.0`; **`coverage_width_m` `500.0`** (note: differs from the write default); `scanner_range_m` `2000.0`; `search_started` `false`; `search_azimuth` `0`; `route_index` `0`; all collections `[]`. Legacy deposits without size/rigs become the smallest size / `1`. Markers missing `lat`/`lon` are reconstructed through `GeographicFromLocal`.

`MapValidator` enforces the full Python rule set — string identity fields, paired PML coordinates, finite ranges (`coverage_width_m` 100–5000, `scanner_range_m` 500–5000, radar radius 0–5000), exact-integer `search_azimuth` 0–359, `route_index` 0–13, complete datum when searching, deposit sizes from the four recognised values (decision D7), deposit rigs 1–6, route statuses `reached`/`skipped`, non-empty mark names — and a failed load must leave the previous session untouched (load into a candidate, validate, compute next target, then swap).

### D7 — All .NET-facing text in English; persisted deposit-size literals migrate from Portuguese to English on load

Superseding the original plan to keep `"Pequeno"/"Médio"/"Grande"/"Enorme"` as the permanent on-disk representation: **every string the .NET port owns — exception messages, validation messages, log messages, UI text — is English.** This includes the deposit-size literal written into `.json` map files.

- `DepositSizeCodec.Encode` writes the English canonical literals `"Small"`, `"Medium"`, `"Large"`, `"Huge"` going forward.
- `DepositSizeCodec.Decode` accepts **both** the legacy Portuguese literals and the new English ones, so a map saved by the original Python app (or by an un-migrated .NET build) still loads correctly.
- **Startup migration:** `Infrastructure` adds a `LegacyMapMigrationService` (a `IHostedService` that runs once, before the telemetry/radar/steering loops start). On startup it enumerates every map under `IAppPaths.MapsDirectory`, loads each through `IMapRepository.LoadAsync`, and — if any deposit used a legacy Portuguese literal — re-saves the file (same path, `updateSavedAt: false` so the user-visible timestamps are not disturbed) so the literal is rewritten in English. The migration is idempotent: a map with no legacy literals is loaded and compared but not rewritten. Each migrated file is logged at `Information` (`LogEvents` lifecycle band) with its path; a failed migration for one file is logged at `Warning` and does not stop the others (specific exceptions only — `IOException`, `UnauthorizedAccessException`, `MapValidationException` — never a bare `catch`). A summary line (`N of M maps migrated`) is logged once the pass completes.
- Exception/validation messages throughout `Domain` (e.g. `MapValidator`, `NumberCoercion`, `DepositSizeCodec`, `MapSession`) are in English; exact Python string parity is **no longer** a requirement for these (it was for behavioural/golden-file testing of accept/reject decisions, not for message text, and the two are independent — the golden-file and differential tests in "Testing strategy" compare saved-map bytes and domain state, never exception text).
- This decision is implemented in Phase 1 (Domain: English messages, codec encode/decode split) and Phase 2 (Infrastructure: `LegacyMapMigrationService`, wired into the Desktop host startup sequence ahead of the hosted telemetry/radar/steering services).

**Exact-boolean semantics:** `favorite`/`protected` must be real JSON booleans; `1`/`"true"` are rejected. `System.Text.Json` enforces this by default for `bool`, so the converter must not enable `AllowReadingFromString` for those properties.

**`search_azimuth` exact-int semantics:** Python rejects `bool` and non-integral floats. The DTO reads the raw `JsonElement` and requires `ValueKind == Number` with `TryGetInt32`, rejecting `3.0`-style values only where Python does (the validator rejects non-`int` types; `3.0` from JSON is accepted on load because JSON has no integer type distinction — this nuance is captured in the port's tests).

### `options.json`

A free-form flat dictionary. Unknown keys must be preserved across writes (the Python options panel only updates known keys). The port therefore loads into a `Dictionary<string, JsonElement>`, applies typed updates, and rewrites the whole document. Settings export/import uses `{"rhino_settings_version": 1, "settings": {...}}`.

The known key set is: `coverage_width_m`, `search_azimuth`, `scanner_range_m`, `radar_speed`, `wave_color`, `rhino_size`, `rhino_color`, `assist_speed`, `assist_pulse_ms`, `assist_tolerance_deg`, `scanner_group`, `bindings_path`, `map_background`, `grid_color`, `trail_color`, `coverage_color`, `map_text_scale`, `overlay_width`, `overlay_opacity`, `language`, `theme`, `font_family`, `font_size`, `panel_width`, `button_height`, `map_library_horizontal_split_ratio`, `map_library_vertical_split_ratio`, with the defaults and ranges documented in the options table of the Python inventory.

---

## Logging design

Logging is a first-class requirement (N5). The design has four parts.

### 1. Providers and sinks

`AddInfrastructureLogging()` registers:

| Provider | Purpose |
|---|---|
| `RollingFileLoggerProvider` (custom) | Durable structured logs under `<app>/logs/` |
| `DebugLoggerProvider` | Visual Studio output during development |
| `EventLogLoggerProvider` *(optional, off by default)* | Startup/shutdown failures only |

`RollingFileLoggerProvider` design:

- File name `rhino-YYYYMMDD.log`; rolls on date change and when the active file exceeds **10 MB** (`rhino-YYYYMMDD.001.log`, …).
- Retention: at most **14 days** and **100 MB** total; oldest files deleted on startup and on roll.
- Writes are queued to a `Channel<LogEntry>` (bounded, 10 000 entries, `BoundedChannelFullMode.DropWrite` with a dropped-message counter) and drained by a single background writer, so no game-loop thread ever blocks on disk I/O.
- Flush on `IHostApplicationLifetime.ApplicationStopping`, on `Fatal`-level entries, and every 2 seconds.
- Line format, one entry per line, pipe-delimited for greppability:
  ```
  2026-10-02T11:24:31.482Z | INF | RhinoSurfaceMapper.Application.Features.MapSession.SaveMap | map=Nervi 2 A [JD3] gen=7 | Handled SaveMap.Command in 12 ms | Result=Success
  ```
- Exceptions are written after the message, indented, including inner exceptions and stack traces.

### 2. Categories, levels and configuration

Default levels in `appsettings.json`, overridable by the user without a rebuild:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "RhinoSurfaceMapper.Desktop.Hosting.TelemetryHostedService": "Warning",
      "RhinoSurfaceMapper.Desktop.Hosting.RadarHostedService": "Warning",
      "RhinoSurfaceMapper.Infrastructure.Telemetry": "Warning",
      "Microsoft": "Warning"
    },
    "File": { "Directory": "logs", "RetentionDays": 14, "MaxFileBytes": 10485760, "MaxTotalBytes": 104857600 }
  }
}
```

Level policy:

| Level | Used for |
|---|---|
| `Trace` | Per-sample telemetry values, per-tick steering decisions, per-frame scene sizes. Never enabled by default |
| `Debug` | Handler entry, map validation details, journal line skips, binding resolution, cache hits/misses |
| `Information` | Lifecycle (startup, shutdown, versions), map open/save/new, PML allocation, search start/stop, navigation start/arrival, assistance enable/disable, every mediator dispatch result |
| `Warning` | Refused operations (read-only map, protected save), injection preconditions failing, telemetry staleness, Spansh station issues, dropped log entries, malformed `options.json` |
| `Error` | Handler exceptions, map write failures, journal/status read failures that stop progress, `SendInput` failures, JS interop failures |
| `Critical` | Unhandled exceptions reaching the global handlers, hook installation failure while assistance is enabled |

The two hot loops default to `Warning` precisely so that `Information` stays readable; a user diagnosing a telemetry problem raises a single category.

### 3. Structured context

- **Scopes.** The telemetry loop opens a scope carrying `{ map, system, body, generation }`; the steering loop adds `{ assistState, speed }`. Scope values are appended to the file line in the `key=value` segment shown above.
- **Message templates only.** No string interpolation in log calls, so structured values stay addressable: `_logger.LogInformation("Opened map {MapPath} (PML {PmlId}, {PointCount} points)", path, pmlId, points.Count);`
- **`EventId` catalogue.** `Domain/Constants/LogEvents.cs` defines stable ids per subsystem band — 1000 lifecycle, 2000 telemetry, 3000 map session, 4000 persistence, 5000 radar, 6000 steering/injection, 7000 market, 8000 UI/interop — so log analysis does not depend on message text.
- **High-performance logging.** All hot-path call sites use `[LoggerMessage]` source-generated partial methods, avoiding boxing and allocation when the level is disabled.
- **Guarding.** Hot loops wrap expensive payload construction in `if (_logger.IsEnabled(LogLevel.Trace))`.

### 4. Cross-cutting capture

- `LoggingPipelineBehavior<,>` logs every command/query with elapsed ms and `Result`, as in the reference app.
- `ExceptionLoggingBehavior<,>` logs and rethrows handler exceptions.
- Global handlers in `App.xaml.cs`: `DispatcherUnhandledException`, `AppDomain.CurrentDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` — each logs `Critical`, flushes the sink synchronously, then applies the existing behaviour (show a message, release injected keys, shut down).
- `ShutdownCoordinator` guarantees ordering: stop hosted services → `IInputInjector.ReleaseAll()` → unhook the keyboard hook → persist preferences and splitter ratios → flush logs → dispose.
- **Privacy:** logs contain file paths, in-game systems/bodies and timings only. Command payloads, key scan codes and bindings content are never logged above `Debug`, and nothing personal is recorded.

---

## Threading and concurrency model

| Thread | Work |
|---|---|
| WPF UI thread | Blazor rendering, dialogs, overlay painting, WebView interop |
| Telemetry loop | 50 ms `PeriodicTimer`: read `Status.json` if changed, journal identity, dispatch `ProcessStatusSample`, raise coalesced notifications |
| Radar loop | 16 ms: poll input state, `TickRadarPulse`, raise radar notifications |
| Steering loop | 16 ms: focus/F8/arrival/context checks, `EvaluateSteering`, `IInputInjector.Pulse` |
| Keyboard hook thread | Dedicated message pump for `WH_KEYBOARD_LL` |
| Watchdog thread | 5 ms key-release deadline enforcement |
| Log writer | Single background channel reader |

`IMapSessionStore` serialises all mutations behind a `SemaphoreSlim(1,1)`. Readers (rendering, overlay) take immutable snapshots — the collections are copy-on-write `ImmutableArray<T>` so a render never observes a torn state and never blocks the telemetry loop.

---

## Localisation

- `.resx` resources per translation context, replacing the Qt Linguist contexts one-for-one: `MapperWindow`, `MapView`, `MapLibraryWindow`, `DepositDialog`, `MarkDialog`, `LayoutOptions`, `SteeringUI`, `SteeringAssist`, `SteeringInput`, `TurnTrial`. Keeping the same context names makes the existing `rsm_pt_PT.ts` catalogue directly convertible and preserves the i18n tests' intent.
- `IStringLocalizer<TContext>` is injected into components and handlers that produce user-facing text.
- Default culture `en-GB`; `pt-PT` from `Resources/*.pt-PT.resx`. The culture is applied at startup from the `language` preference and, as today, a change takes effect after restart.
- Placeholders (`{count}`, `{system}`, `{body}`, `{error}`, `{pml_id}`) are preserved; a test asserts every pt-PT entry keeps the same placeholder set as its source string.
- A `tools/TsToResx` conversion utility, committed to the repository, produces the initial `.resx` files from `python/translations/rsm_pt_PT.ts` (decision D2). It is a console project excluded from the shipped output, kept so additional languages or a refreshed catalogue can be re-converted rather than hand-migrated.

---

## Code documentation standard

The Python application has been fully documented in English (module, class and method docstrings plus intent-level inline comments). The port must hold the same bar, expressed in C# terms.

- **XML documentation comments are mandatory** on every public type and public member: `<summary>` always; `<param>`, `<returns>`, `<exception>` and `<remarks>` whenever they carry information the signature does not. Internal and private members are documented when their purpose or invariants are not obvious.
- `<GenerateDocumentationFile>true</GenerateDocumentationFile>` is set in `Directory.Build.props`, and **CS1591** (missing XML comment on a publicly visible type or member) is promoted to an error for `Domain`, `Application` and `Infrastructure`. `Platform.Windows`, `UI.Components` and `Desktop` keep it as a warning, since generated and Razor-backed members cannot always be annotated.
- **Document the "why", not the "what".** A summary that restates the member name adds nothing; AGENTS.md forbids it. Summaries must capture purpose, invariants, units, coordinate conventions and failure behaviour.
- **Preserved constants must document their origin.** Every ported magic value carries a `<remarks>` or an inline comment naming the behaviour it protects — for example `SrvFlag = 0x04000000`, `DefaultRadiusMetres = 6_371_000`, the 13-point / 3 500 m / 1 800 m search route, the 10 m trail spacing and 100 m `break_before` threshold, `PmlMatchDistanceMetres = 13_000` (inclusive), the radar wave speed `2000.0 / 3.0` m/s, the 80 m minimum deposit separation, the ±2°/±8° overlay bands with 0.45 s blink inside 300 m, and the steering constants (3° tolerance, 0.8 s maximum pulse, 15 m/s default limit, 0.25 s cooldown, `/46.5*0.7` base duration, 1/1.5/2/3 aggression bands).
- **File-format compatibility rules are documented at the point of enforcement** — notably the asymmetric `coverage_width_m` defaults (read 500.0, write 2000.0), the fixed key set, `WriteIndented` with two-space indentation, `UnsafeRelaxedJsonEscaping`, invariant culture, atomic temp-file-and-replace, and mtime restoration on flag-only updates.
- **Safety-sensitive code carries an explicit contract.** `Platform.Windows` input observation and `SendInput` injection types document their preconditions, suppression conditions and the guarantee that injected events are never mistaken for manual input.
- **Razor components** document their purpose, parameters and the state they own in a leading `@*  *@` block; code-behind members use ordinary XML comments.
- **Tests are documented too:** a module-level summary of the behaviour under test, a class summary covering the fixture semantics, and a one-line summary on a test only where its name does not already state the invariant. Non-obvious arrangements and magic values are explained inline.
- An analyzer-backed build is the enforcement mechanism; documentation completeness is reviewed as part of each phase's exit criteria rather than deferred to the end.

---

## Testing strategy

Frameworks match the reference app: **xUnit + FluentAssertions + AutoFixture + Moq**, plus **bUnit** for components.

| Python test | .NET destination |
|---|---|
| `test_mapper_core.py`, `test_map_validation.py`, `test_navigation_marker.py` | `Domain.Tests/MapSession*`, `TelemetryProcessorTests`, `SearchRouteCalculatorTests`, `OverlayNavigationCalculatorTests` |
| `test_map_pml.py`, `test_map_protection.py` | `Domain.Tests/PmlRulesTests` |
| `test_radar.py`, `test_turn_trial.py`, `test_steering.py` | `Domain.Tests/RadarPulseEngineTests`, `TurnTrialTests`, `SteeringDecisionEngineTests` (+ injector tests in `Platform.Windows.Tests`) |
| `test_map_persistence.py`, `test_settings_persistence.py`, `test_maps_directory.py`, `test_options_path.py` | `Infrastructure.Tests/JsonMapRepositoryTests`, `JsonPreferencesRepositoryTests`, `AppPathsTests` |
| `test_elite_dangerous_*.py` | `Infrastructure.Tests/Telemetry*`, `Market*` |
| `test_map_operations.py`, `test_map_library_filters.py`, `test_map_preview_framing.py`, `test_layout_options.py` | `Application.Tests/Features/*` for rules; `UI.Components.Tests` for rendering/interaction |
| `test_qt_window.py`, `test_overlay_window.py` | `UI.Components.Tests` (bUnit) + `Desktop` smoke tests for overlay geometry/flags |
| `test_*_i18n.py` | `UI.Components.Tests/LocalizationTests` asserting context ownership and placeholder parity |

Additional port-specific tests:

- **Golden-file round-trip:** a corpus of real Python-written maps (including legacy ones) must load, re-save and compare byte-identical except for `last_saved_at`.
- **Cross-implementation differential:** a fixture runs the same telemetry sequence through the Python core and the .NET core and asserts identical trail points, route targets and overlay strings within `1e-9`.
- **Logging tests:** `LoggingPipelineBehavior` level selection; rolling/retention rules; payloads never logged.

Commands (from the repository root):

```powershell
dotnet build RhinoSurfaceMapper.sln
dotnet test                                                      # full suite
dotnet test tests\RhinoSurfaceMapper.Domain.Tests                # one project
dotnet test --filter "FullyQualifiedName~SearchRouteCalculator"  # one class
dotnet test --filter "DisplayName~starts_search_due_north"       # one test
```

---

## Packaging and distribution

- `dotnet publish -r win-x64 -c Release --self-contained` producing the portable folder layout the Beta already uses (`RhinoSurfaceMapper.exe` plus `MAPAS/`, `options.json` and `logs/` created beside it at first run).
- `AppPaths` resolves the base directory from `AppContext.BaseDirectory`, which is correct for both framework-dependent and single-file publishes — the equivalent of today's `sys.frozen` branch.
- Static web assets (`wwwroot`) and `.resx` satellite assemblies ship inside the publish folder; the `SURFACE_MINING_COMMODITIES.json` catalogue is an embedded resource, removing the PyInstaller `datas` maintenance burden.
- **WebView2 runtime** is a prerequisite. The Evergreen runtime is present on up-to-date Windows 10/11, but the installer/README must state the requirement and the app must fail gracefully with a clear message and a `Critical` log entry if `BlazorWebView` cannot initialise. *Risk R1.*
- Trimming and ReadyToRun are deferred; reflection in `System.Text.Json` converters and FluentValidation scanning must be audited first.

---

## Phased delivery

Although the design covers full parity, implementation should land in reviewable phases:

| Phase | Content | Exit criterion |
|---|---|---|
| 0 | Solution skeleton, DI entry points, mediator, logging provider, `IClock`, `IAppPaths` | `dotnet test` green; a log file is produced |
| 1 | Domain core: geometry, `MapSession`, telemetry processing, search route, validation, PML rules | All `test_mapper_core`/`test_map_pml`/`test_map_validation` invariants ported and green |
| 2 | Infrastructure: JSON map/preferences repositories, status and journal readers | Golden-file round-trip green |
| 3 | WPF shell + BlazorWebView + map canvas + telemetry loop (read-only map display) | Live telemetry draws a trail identical to the Python app |
| 4 | Map operations, dialogs, PML lifecycle, save/open flows | `test_map_operations` invariants ported |
| 5 | Map Library, preview, filters, flags, splitter persistence | `test_map_library_*` invariants ported |
| 6 | Options panel, theming, localisation | `test_layout_options` and i18n invariants ported |
| 7 | Overlay window and navigation | `test_overlay_window` and `test_navigation_marker` invariants ported |
| 8 | Radar input, radar pulse, steering assistance, direction test | `test_radar` and `test_steering` invariants ported |
| 9 | Market subsystem | `test_elite_dangerous_market_*` invariants ported |
| 10 | Packaging, differential testing against Python, documentation | Byte-compatible maps; parity sign-off |

---

## Resolved decisions

All design-blocking questions were answered on 2026-10-02. They are recorded here with their consequences; the sections above have been updated to match.

| # | Question | Decision | Consequence |
|---|---|---|---|
| D1 | Plain Razor + scoped CSS, or MudBlazor? | **Plain Razor + scoped CSS** | No MudBlazor dependency anywhere. `UI.Components` ships a small scoped-CSS design system; the dense desktop layout and the custom canvas stay under our control, and the WebView payload stays minimal |
| D2 | `.ts` → `.resx` as a committed tool or a manual migration? | **Committed tool `tools/TsToResx`** | A console project in `tools/`, excluded from the shipped output and from the installer, re-runnable when the catalogue changes or a language is added |
| D3 | INARA parsing: `AngleSharp` or a hand-written tokeniser? | **`AngleSharp`** | The one approved third-party dependency beyond the framework. Referenced only by `Infrastructure`, used only by `InaraSummaryParser`, which is kept behind an interface so the dependency remains replaceable |
| D4 | Ship the headless-browser INARA acquisition in-app? | **No — developer tool only** | The shipped application never spawns a browser. Acquisition stays a `tools/` script; the app consumes `inara_summary_cache_v1.json` if present and degrades gracefully when it is absent or stale |
| D5 | Does the Python app stay maintained during the port? | **Frozen at Beta 1** | `python/` is reference-only. Behavioural fixes are made in the .NET tree; any Python change must be deliberate and mirrored. Removes risk R8 as an ongoing concern |
| D6 | Should the overlay become click-through? | **Out of scope — keep parity** | The overlay stays interactive, exactly as the Qt implementation behaves. `WS_EX_TRANSPARENT` may be revisited post-parity as a separately designed opt-in preference |

### Outstanding prerequisite

The **WebView2 Runtime was not detected** on the current development machine. It must be installed (and its absence handled per risk R1) before Phase 3; Phases 0–2 are unaffected.

---

## Risks and mitigations

| # | Risk | Severity | Mitigation |
|---|---|---|---|
| R1 | WebView2 runtime missing or failing to initialise leaves a blank window | High | Detect at startup, show a native WPF error dialog with the download link, log `Critical`; document the prerequisite in the README |
| R2 | Canvas rendering through JS interop cannot sustain 20 Hz with large maps | High | Packed binary arrays instead of JSON; JS-owned `requestAnimationFrame`; dirty-region scene diffing; prototype in Phase 3 with a worst-case map before committing. Fallback: SkiaSharp rendering into an `Image` surface or a `WriteableBitmap` hosted beside the WebView |
| R3 | Floating-point or formatting drift makes maps non-interchangeable with the Python version | High | Invariant culture everywhere, shortest-round-trippable doubles, and the golden-file + differential tests gate every phase |
| R4 | `SendInput` injection misbehaving could steer the player's SRV unexpectedly | High | Port every precondition verbatim, keep the watchdog on an independent thread, release on any doubt, log every refusal, and cover with the ported `test_steering` suite before enabling the feature |
| R5 | GC pauses or `Dispatcher` contention stall the 16 ms loops | Medium | Hot paths are allocation-free (`[LoggerMessage]`, pooled buffers, struct samples); loops run off the UI thread; server GC disabled, concurrent GC enabled |
| R6 | WinMM joystick API is legacy and limited to 32 buttons / 16 devices | Medium | Preserve exact parity first (users' bindings depend on it); consider RawInput or DirectInput as a later, separately designed enhancement |
| R7 | Low-level keyboard hook flagged by anti-cheat or AV | Medium | Hook is observation-only and always calls `CallNextHookEx`; document the behaviour; keep the feature opt-in and disabled by default |
| R8 | Two implementations diverge during a long port | Medium | Python feature work is frozen at Beta 1 (decision D5); the differential test is run in CI against both trees |
| R9 | Qt-specific UI nuances (elided cursor text, preview framing maths, splitter ratio fallbacks) are subtly lost | Low | These are covered by explicit ported tests (`test_map_preview_framing`, `test_map_library_splitter_persistence`) rather than visual inspection |
| R10 | Mutable `MapSession` shared across loops invites race conditions | Medium | Single mutation gate (`SemaphoreSlim`), immutable collections for readers, and no mutation from the UI thread outside handlers |
```
