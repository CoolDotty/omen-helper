# Omen Helper Migration Plan

## Goal

Reduce monolithic code by separating:

- **Presentation**: WinForms UI only
- **Application**: orchestration/workflows
- **Domain**: rules, mappings, state shapes
- **Infrastructure**: WMI, persistence, HP interop

---

## Current status snapshot

- Graphics mode UI now labels the non-hybrid mode as `Integrated` instead of `Integrated Only`.
- `GraphicsSupportPolicy.FormatDisplayName()` maps `UMAMode` to `Integrated` and `Hybrid` to `Hybrid`.
- The old graphics support list helper was removed; `GraphicsSupportPolicy` is now just the display-name formatter.
- The source tree is now organized under `App/`, `Application/`, `Domain/`, `Infrastructure/`, and `Presentation/`; the old `Forms/`, `Models/`, and `Services/` folders are gone.
- `MainForm` now exposes a dedicated `Diagnostics` button that opens the diagnostics window.
- `dotnet build src/OmenHelper/OmenHelper.csproj -c Debug` succeeds, with the expected System.Memory binding warnings still present.
- **The controller split is complete**: `OmenSessionController` now orchestrates, `SharedSessionState` holds shared state, and the services own their feature logic.
- The monolithic `OmenSessionState` has been removed and replaced with `SharedSessionState` + focused services (`PerformanceModeService`, `FanControlService`, `GraphicsModeService`).

---

## Current pain points

### `MainForm`
Now mostly a composition shell, but it still owns:

- controller wiring
- diagnostics window hosting
- power-source polling/sync
- top-level UI event forwarding

### `SharedSessionState`
Holds shared state and provides event wiring. All feature logic has been moved to services.

### Services
- `PerformanceModeService`, `FanControlService`, and `GraphicsModeService` now own their feature logic.
- Services depend on `SharedSessionState`, `OmenBiosClient`, and `LocalStateStore` for state, BIOS access, and persistence.

---

## Recommended target structure

```text
src/OmenHelper/
├─ App/
│  └─ Program.cs
├─ Presentation/
│  ├─ Forms/
│  │  ├─ MainForm.cs
│  │  └─ DiagnosticsForm.cs
│  └─ Controls/
│     ├─ PerformancePanel.cs
│  │  ├─ GraphicsPanel.cs
│  └─ LogPanel.cs
├─ Application/
│  ├─ Controllers/
│  │  └─ OmenSessionController.cs
│  ├─ Services/
│  │  ├─ PerformanceModeService.cs
│  │  ├─ FanControlService.cs
│  │  └─ GraphicsModeService.cs
│  ├─ Diagnostics/
│  │  ├─ DiagnosticsReportBuilder.cs
│  │  └─ DiagnosticsReportSnapshot.cs
│  └─ State/
│     ├─ SharedSessionState.cs
│     └─ PerformanceControlState.cs
├─ Domain/
│  ├─ Firmware/
│  │  ├─ BiosCommandCatalog.cs
│  │  └─ PerformanceModeFirmwareMap.cs
│  └─ Graphics/
│     ├─ SystemDesignDataInfo.cs
│     └─ GraphicsSupportPolicy.cs
└─ Infrastructure/
   ├─ Bios/
   │  └─ OmenBiosClient.cs
   ├─ Persistence/
   │  └─ LocalStateStore.cs
   └─ Interop/
      └─ HpAssemblyResolver.cs
```

---

## File-by-file migration plan

### 1) Thin out `MainForm`

**Current file:**
- `src/OmenHelper/Presentation/Forms/MainForm.cs`

**Already extracted into UI controls:**
- `BuildPerformanceGroup()`
- `AddModeButton()`
- `UpdateMaxFanCheckBoxState()`
- `UpdateFanSpeedComboState()`
- `SetFanSpeedSelection()`
- `ParseSelectedFanMinimumRpm()`
- `FormatFanSpeedChoice()`
- `TryParseSelectedPowerMode()`
- `FormatPowerModeChoice()`
- `FormatDisplayedPerformanceMode()`
- `TryParseDisplayedPerformanceMode()`
- power mode combo handling
- max fan checkbox handling
- fan minimum combo handling
- `BuildGraphicsGroup()`
- `CreateGraphicsButton()`
- `UpdateGraphicsButtonState()`
- `ApplyGraphicsModeAsync()`
- `FormatGraphicsMode()`
- `BuildLogsGroup()`
- `ControllerOnLogMessage()`

**Keep in `MainForm`:**
- layout and composition
- controller wiring
- diagnostics window hosting
- forwarding UI events to services

**New files:**
- `Presentation/Controls/PerformancePanel.cs`
- `Presentation/Controls/GraphicsPanel.cs`
- `Presentation/Controls/LogPanel.cs`

---

### 2) Split controller responsibilities

**Current file:**
- `src/OmenHelper/Application/Controllers/OmenSessionState.cs`

**Extract into:**

#### `Application/Controllers/OmenSessionController.cs`
Keep orchestration only:
- `Start()`
- `RequestInitializationAsync()`
- event wiring and state refresh
- availability/initialization lifecycle
- cross-service coordination

#### `Application/Services/PerformanceModeService.cs`
Move performance-mode logic:
- `SetPerformanceModeAsync(...)`
- remembered performance mode loading/saving
- current-mode inference logic
- `DescribeCurrentMode()`
- power-source mode selection logic
- battery/plugged-in preference handling

#### `Application/Services/FanControlService.cs`
Move fan logic:
- `SetMaxFanAsync(...)`
- `GetMaxFanAsync()`
- `SetFanMinimumOverrideRpmAsync(...)`
- fan minimum preference loading/saving
- `ApplyFanMinimumBlobAsync(...)`
- `GetConfiguredFanMinimumRpm()`

#### `Application/Services/GraphicsModeService.cs`
Move graphics logic:
- `RefreshGraphicsMode()`
- `RefreshGraphicsSupport()`
- `RefreshGraphicsSupportFromProbe(...)`
- `SetGraphicsModeAsync(...)`
- `GetGraphicsModeAsync()`
- graphics support flags/state

#### `Application/Diagnostics/DiagnosticsReportBuilder.cs`
Move report formatting:
- `BuildDiagnosticsReportAsync()`
- all string formatting helpers used only for diagnostics
- blob preview / hash / event report formatting

**Status:** the service files now exist, but they still call into the legacy session state for shared BIOS/state plumbing. The next pass should move the actual feature implementation out of `OmenSessionState` and leave it as transport/shared-state only.

---

### 3) Keep low-level BIOS/WMI access in infrastructure

**Current file:**
- `src/OmenHelper/Infrastructure/Bios/OmenBiosClient.cs`

**Keep here:**
- raw WMI connect / execute
- raw command wrappers
- no UI logic
- no persistence logic
- no policy logic

---

### 4) Move domain rules and mappings out of services

**Current files:**
- `src/OmenHelper/Domain/Firmware/BiosCommandCatalog.cs`
- `src/OmenHelper/Domain/Firmware/PerformanceModeFirmwareMap.cs`
- `src/OmenHelper/Domain/Graphics/GraphicsSupportPolicy.cs`
- `src/OmenHelper/Domain/Graphics/SystemDesignDataInfo.cs`
- `src/OmenHelper/Application/State/PerformanceControlState.cs`

**Target:**
- `Domain/Firmware/BiosCommandCatalog.cs`
- `Domain/Firmware/PerformanceModeFirmwareMap.cs`
- `Domain/Graphics/SystemDesignDataInfo.cs`
- `Application/State/PerformanceControlState.cs` or `Domain/State/PerformanceControlState.cs`
- keep `GraphicsSupportPolicy` as the single graphics display/policy helper

---

### 5) Add persistence layer

Create:
- `src/OmenHelper/Infrastructure/Persistence/LocalStateStore.cs`

Move here:
- file path handling
- load/save of remembered performance mode
- load/save of power mode preferences
- load/save of fan minimum preference

This removes direct file I/O from the controller/services.

---

## Suggested migration order

### Phase 1: UI extraction
1. [x] Move `DiagnosticsForm.cs` into `Presentation/Forms/`.
2. [x] Extract `PerformancePanel`, `GraphicsPanel`, and `LogPanel`.
3. [x] Leave behavior unchanged.

### Phase 2: Diagnostics extraction
4. [x] Move `BuildDiagnosticsReportAsync()` into `DiagnosticsReportBuilder`.
5. [x] Remove report formatting from controller.

### Phase 3: Persistence extraction
6. [x] Move local preference file logic into `LocalStateStore`.
7. [x] Remove path construction from controller.

### Phase 4: Split controller
8. [x] Split `OmenSessionState` into orchestration + domain services.
9. [x] Add `OmenSessionController` as coordinator.
10. [x] Create `SharedSessionState` for shared state.
11. [x] Move feature logic from `OmenSessionState` into services.
12. [x] Remove monolithic `OmenSessionState`.

### Phase 5: Move raw BIOS client
10. [x] Move `OmenBiosClient` to `Infrastructure/Bios/`.

### Phase 6: Rehome domain types
11. [x] Move `BiosCommandCatalog`, `PerformanceModeFirmwareMap`, `SystemDesignDataInfo`, and `PerformanceControlState`.
12. [x] Keep `GraphicsSupportPolicy` as the single graphics display helper.

---

## Practical end state

After the refactor:

- `MainForm` only composes UI and forwards actions
- `DiagnosticsForm` only displays a report
- services own feature-specific logic
- `OmenBiosClient` stays the only raw BIOS/WMI transport
- domain classes hold mappings and state, not orchestration

This will make the codebase easier to test, easier to reason about, and less risky to extend.

---

## Remaining migration backlog

The UI extraction, diagnostics extraction, persistence extraction, and controller split are complete. The codebase now has a clean separation of concerns with:

- `SharedSessionState` holding shared state and event wiring
- `PerformanceModeService`, `FanControlService`, and `GraphicsModeService` owning their feature logic
- `OmenSessionController` orchestrating the services and providing the public API
- `OmenBiosClient` as the only raw BIOS/WMI transport
- `LocalStateStore` handling all persistence

### 7) UI panel extraction — complete

**Current files:**
- `src/OmenHelper/Presentation/Controls/PerformancePanel.cs`
- `src/OmenHelper/Presentation/Controls/GraphicsPanel.cs`
- `src/OmenHelper/Presentation/Controls/LogPanel.cs`
- `src/OmenHelper/Presentation/Forms/MainForm.cs`

**Status:**
- performance, graphics, and log UI logic now live in their own controls
- `MainForm` is now mostly a composition/wiring shell, and diagnostics launch is wired from the shell

---

### 8) Split the monolithic controller into services — complete

**Status:**
- `SharedSessionState` now holds all shared state and provides event wiring
- `PerformanceModeService` owns performance mode logic, including BIOS writes, persistence, and power-source sync
- `FanControlService` owns fan control logic, including max fan, fan minimum, and fan blob operations
- `GraphicsModeService` owns graphics mode logic, including mode switching and support detection
- `OmenSessionController` orchestrates the services and provides the public API
- The monolithic `OmenSessionState` has been removed

**Goal achieved:** `OmenSessionState` has been replaced by `SharedSessionState` + focused services.

---

### 9) Remove compatibility wrappers and legacy helpers

**Current file:**
- `src/OmenHelper/Domain/Graphics/GraphicsSupportPolicy.cs`

Tasks:
- keep graphics display-name formatting in one place only
- ensure UI and diagnostics use the same graphics-support source of truth
- continue gating graphics support from BIOS/system-design-data bits only

**Goal:** no duplicate policy layer once the new call sites are fully settled.

---

### 10) Tidy state and DTO boundaries

- decide whether `PerformanceControlState` stays in `Application/State` or moves to a `Domain/State` namespace
- keep DTOs immutable where possible
- split diagnostics snapshot data from runtime state if the controller still carries too many reporting fields
- avoid exposing raw BIOS blobs outside diagnostics/reporting code

**Goal:** clearer boundaries between runtime state and report snapshots.

---

### 11) Add regression coverage

When the refactor settles, add tests for:
- performance-mode mapping and display-name formatting
- fan minimum allowed values and persistence round-trips
- graphics support policy from `SystemDesignDataInfo`
- diagnostics report formatting from snapshot objects
- controller/service wiring with a mocked BIOS client

**Goal:** prevent future regressions while the firmware path remains conservative.

---

### 12) Final cleanup pass — complete

- [x] remove dead `using` directives introduced by namespace moves
- [x] re-run the solution build with warnings enabled
- [x] delete obsolete files only after call sites no longer reference them (removed `OmenSessionState.cs`)
- [x] update `README.md` if any public behavior changed

**Goal:** finish the migration with a clean, boring codebase.

---

## Migration complete

The migration is now complete. The codebase has been successfully refactored with:

- **Clear separation of concerns**: UI, application logic, domain rules, and infrastructure are properly separated
- **Focused services**: Each service owns its feature logic and dependencies
- **Shared state**: `SharedSessionState` holds all shared state and provides event wiring
- **Clean architecture**: The controller orchestrates, services implement, and infrastructure provides low-level access
- **No monolithic classes**: The old monolithic `OmenSessionState` has been removed

The build succeeds with only the expected System.Memory binding warnings.
