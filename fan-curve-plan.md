# Per-Mode BIOS Fan Curve System

  ## Summary

  Add a background-controlled, BIOS/WMI-backed fan curve system that drives the existing 131080 / 46 fan target blob instead of the current fixed-RPM dropdown path. The new system will:

  - store separate CPU, GPU, and chassis curves per performance mode
  - render three small side-by-side interactive charts in the main UI
  - pool temperature reads to at most once per second on a worker thread
  - use a 5-second moving average plus asymmetric hysteresis to avoid breathing
  - keep the UI thread out of telemetry polling and BIOS writes
  - continue using the confirmed firmware path only: write payload[0] and payload[1] as rpm / 100, clamped to 0..65

  Assumptions chosen to make the spec complete:

  - curves are persisted per performance mode
  - saved curves auto-start after app initialization
  - the existing max-fan checkbox and fixed-RPM dropdown remain visible for debugging only
  - Max fan overrides the curve loop
  - using the old fixed-RPM dropdown pauses or disables the curve loop until the user re-enables custom curves
  - the requested “chassis curve” is implemented as a chassis-temperature floor applied to both CPU and GPU targets, because the confirmed firmware blob exposes only two writable fan target bytes

  ## Implementation Changes

  ### Domain and runtime model

  Add fan-curve types that keep policy out of the UI:

  - FanCurveProfile: fixed RPM points for temperatures 50, 55, 60, 65, 70, 75, 80, 85, 90
  - FanCurveSet: CPU curve, GPU curve, chassis curve, GPU-linked flag, enabled flag
  - FanCurveStore: per-performance-mode mapping for Eco, Balanced, Performance, Unleashed
  - FanCurveEvaluationResult: averaged temps, desired CPU/GPU RPM, applied CPU/GPU RPM, source mode, link state, chassis override usage
  - FanCurveDefaults: generates per-mode defaults

  Use these invariants everywhere:

  - RPM values: 0..6500, step 100
  - temperature points: 50..90, step 5
  - temps below 50 use the 50C point for CPU/GPU evaluation
  - temps above 90 use the 90C point
  - GPU linked mode means gpuRpm = max(0, cpuRpm - 200) at every point
  - chassis default curve evaluates to 0 for <50C, then 6500 for >=50C
  - chassis output is not a third firmware target; it is a floor: finalCpu = max(cpuDesired, chassisDesired), finalGpu = max(gpuDesired, chassisDesired)

  Default curves:

  - CPU curve defaults should start from the current mode’s existing firmware minimum at 50C
  - CPU curve should ramp upward in simple 100-RPM steps to 6500 by 90C
  - GPU default is linked to CPU with -200 RPM
  - chassis default is the override described above

  ### Firmware write path

  Keep raw BIOS transport in the BIOS client and move fan-curve orchestration into application services:

  - add a dedicated fan-target write helper that still uses the existing 131080 / 46 blob path
  - build a 128-byte payload with only byte 0 and byte 1 set from the quantized CPU/GPU targets
  - continue leaving the remaining blob bytes zero unless future reverse engineering proves otherwise
  - treat a write as successful only when ExecuteResult == true and ReturnCode == 0
  - keep diagnostics for last blob hash, changed bytes, return code, and applied targets

  Important behavior:

  - performance mode changes must switch to that mode’s saved curve set immediately
  - after a performance mode change, recompute and reapply the curve target using the new mode’s active curve
  - max-fan mode suspends curve writes until max-fan is turned off
  - old fixed-RPM debug writes suspend the curve loop so the two control paths do not fight

  ### Background telemetry and control loop

  Move periodic telemetry and control decisions off the WinForms timer path into a controller-owned worker loop:

  - start a long-running background task after initialization
  - poll temperatures and fan RPM at most once every second
  - reuse a single telemetry pass per tick for both UI state and curve control
  - update shared state from the worker thread, then raise state-changed events for the UI to marshal
  - stop the worker cleanly with cancellation on app close

  Per 1-second tick:

  1. read CPU/GPU/chassis temperatures once
  2. read current CPU/GPU fan RPM once
  3. append the latest temps into a 5-sample rolling window
  4. compute 5-second moving averages for CPU, GPU, and chassis
  5. evaluate active per-mode curves from averaged temps
  6. apply hysteresis before changing the output level
  7. if the effective CPU/GPU targets changed after hysteresis, issue one BIOS write
  8. update diagnostics/state with desired targets, applied targets, averages, and last write reason

  Hysteresis rule:

  - keep an applied temperature anchor per curve source
  - ramp up only when averaged temp reaches at least currentAnchor + 5C
  - ramp down only when averaged temp reaches at most currentAnchor - 10C
  - between those bounds, keep the previously applied level
  - after CPU/GPU/chassis hysteresis is resolved independently, combine them with the chassis floor rule and quantize the final CPU/GPU targets to 100-RPM steps

  Write coalescing and debounce:

  - no BIOS write unless the quantized CPU/GPU target pair changed
  - chart dragging updates UI immediately but does not write on every mouse move
  - commit edited curve values to the runtime on drag end; for click or keyboard edits, debounce activation by about 300-500 ms
  - if the user makes several edits quickly, only the last curve state becomes active

  ### UI

  Replace the current fan-speed dropdown as the primary UX with a compact fan-curve editor row while leaving the old controls visible as debug-only.

  Add a new “Fan Curves” section to the performance panel with:

  - three small charts in one row: CPU, GPU, Chassis
  - each chart sized to fit the existing main window side-by-side without horizontal scrolling
  - fixed X positions for the 9 temperature points
  - vertical-only point dragging with snap-to-grid
  - lightweight axis labels so the charts remain compact
  - live readout for hovered or selected point: Temp / RPM
  - a checkbox above or inside the GPU chart: Link to CPU (-200 RPM)
  - when linked, GPU chart interaction is disabled and the rendered line is derived from CPU
  - a top-level enable checkbox for the fan-curve runtime so debug controls can still be used intentionally
  - status text showing current averaged temps and current desired/applied CPU/GPU targets

  Implementation choice:

  - use a custom-painted WinForms control for the chart rather than adding a charting dependency
  - the control should expose fixed-point data and change events only; it should not know about BIOS or telemetry

  ### Persistence and state

  Replace the single fan-minimum text persistence with a structured fan-curve settings file in local app data.

  Persist:

  - enabled flag
  - per-performance-mode CPU curve points
  - per-performance-mode GPU curve points or link flag
  - per-performance-mode chassis curve points
  - last-selected link state per mode

  Storage format:

  - use one JSON file under the existing local state directory
  - keep the old fixed-RPM preference file for backward compatibility during migration, but stop treating it as the main manual control
  - on first run after upgrade, seed per-mode CPU defaults from the existing mode minimums; do not attempt to auto-convert one fixed-RPM preference into a full curve

  State and diagnostics additions:

  - active fan-curve mode
  - whether curve runtime is enabled
  - GPU linked/unlinked state
  - pooled temperature timestamp
  - 5-second averaged CPU/GPU/chassis temps
  - desired CPU/GPU RPM before chassis floor
  - final applied CPU/GPU RPM
  - last curve-write timestamp and reason
  - whether chassis override changed the final target

  ## Interfaces and Types

  Add or extend these internal interfaces/types:

  - FanCurvePoint, FanCurveProfile, FanCurveSet, FanCurveStore
  - FanCurveDefaults
  - FanCurveEvaluator
  - FanCurveRuntimeState
  - HardwareTelemetrySnapshot containing raw temps, averaged temps, fan RPMs, timestamp, and source text
  - fan persistence DTOs for JSON load/save
  - UI state DTO fields for averaged temps, applied targets, desired targets, linked state, and curve enabled state

  Existing boundaries should remain intact:

  - presentation owns only chart rendering and user input forwarding
  - application owns polling, evaluation, debounce, and orchestration
  - infrastructure owns BIOS transport and persistence
  - domain owns quantization, defaults, hysteresis rules, and curve math

  ## Test Plan

  Add a test project and cover the logic-heavy parts first.

  Core tests:

  - RPM validation accepts only 0..6500 in 100-RPM steps
  - temperature grid is exactly 50..90 in 5C steps
  - GPU linked mode always mirrors CPU minus 200, clamped at 0
  - chassis default evaluates to 0 below 50C and 6500 at 50C+
  - chassis floor raises CPU/GPU outputs only when higher than their own desired targets
  - CPU/GPU curve evaluation clamps below 50C and above 90C
  - per-mode persistence round-trips exactly
  - switching performance modes swaps to the correct saved curve set

  Control-loop tests:

  - telemetry reads are pooled so temperature acquisition happens at most once per second
  - 5-sample moving average behaves correctly with partial windows during startup
  - ramp-up occurs only after +5C beyond the current anchor
  - ramp-down occurs only after -10C below the current anchor
  - no BIOS write occurs when the quantized CPU/GPU target pair is unchanged
  - rapid chart edits coalesce into one effective runtime update
  - max-fan mode suppresses curve writes
  - using the old fixed-RPM debug path pauses the curve runtime

  UI tests and manual verification:

  - three charts fit side-by-side at the current minimum window width
  - GPU chart is visibly disabled while linked
  - dragging points snaps to valid RPM steps only
  - UI remains responsive while telemetry and BIOS writes run
  - diagnostics show averaged temps, desired targets, applied targets, and last blob result
  - with live hardware, confirm the curve system writes the same 131080 / 46 path as the old dropdown and that applied fan behavior is stable without visible breathing

  ## Assumptions

  - The fan target blob remains limited to CPU byte 0 and GPU byte 1; no third writable chassis target is assumed.
  - The background control loop is the source of truth for fan targets once custom curves are enabled.
  - Existing debug fan controls remain available for now, but custom curves are the normal control path.
  - A custom-drawn WinForms chart is preferred over introducing a new chart package.
  - No attempt is made to infer or read back the currently active fan curve from BIOS; the curve is app-managed and write-only, with validation coming from return codes, diagnostics, and observed fan behavior.
