To "crack" the remaining mysteries of the HP OMEN ecosystem, you should move beyond simple log tailing and into Active Instrumentation and Deep Static Analysis.

  Since you already have the DLLs in /dlldumps, here are the professional-grade techniques to deconstruct the control logic:

  1. The "Decompile and Patch" Strategy (Static Analysis)
  You've identified PipeClientV3 and ConnectedPipeVerifier as the gatekeepers.
   * Tools: dnSpyEx (https://github.com/dnSpyEx/dnSpyEx) or ILSpy (https://github.com/icsharpcode/ILSpy).
   * The Goal: Open HP.Omen.Core.Common.dll and find the ConnectedPipeVerifier class. Look for the method that performs the signature check (likely using WinVerifyTrust or GetProcessImageFileName).
   * The Hack: On a development machine, you can actually patch the HP DLL to always return true for the signature check, then restart the HP service. If your app suddenly starts receiving the state updates, you’ve confirmed
     that the signature is the only barrier.

  2. API Hooking (Dynamic Analysis)
  Instead of guessing what your app is doing wrong, watch what the official OMEN UI is doing right in real-time.
   * Tools: Frida (https://frida.re/) or Microsoft Detours (https://github.com/microsoft/Detours).
   * The Goal: Hook the OmenHsaClient.ExecuteBiosWmiCommandThruDriverAsync method inside the official OMENCommandCenter.exe.
   * What to Look For: Are there "handshake" calls before a mode change? For example, does it call a "Get Capabilities" or "Begin Transaction" command (Type 45/46) that you are skipping? Most HP firmware controls require a
     specific sequence of "Read-Modify-Write" to the 128-byte settings blob.

  3. WMI Tracing and Sniffing
  The ExecuteBiosWmiCommand is ultimately a wrapper around a WMI call or a direct Driver IOCTL.
   * WMI Explorer: Use WMI Explorer (https://github.com/vinaypamnani/wmie2) to browse the root\HP\InstrumentedBIOS namespace. Many "private" HP methods are exposed here.
   * Event Viewer: Enable WMI Activity Tracing in Windows Event Viewer (Applications and Services Logs -> Microsoft -> Windows -> WMI-Activity -> Trace). This will show you the exact WMI queries and methods the HP service is
     calling under the hood.

  4. Process Monitor (ProcMon) "Differential Analysis"
  This is the most effective "low-tech" way to find hidden state.
   1. Open ProcMon (https://learn.microsoft.com/en-us/sysinternals/downloads/procmon).
   2. Filter for HP.Omen.Background.PerformanceControl.exe.
   3. Start a capture, change the mode in the official OMEN UI, and stop the capture.
   4. Look for Registry Writes or File Writes in HKCU\Software\HP or %LOCALAPPDATA%.
   5. The Reveal: Often, the service doesn't just send a pipe message; it updates a "CurrentMode" registry key. If you can't get the pipe message, you can just poll that registry key to get the current state.

  5. I/O Control (IOCTL) Sniffing
  If you want to go to the driver level:
   * Tools: Device Monitoring Studio (https://www.hhdsoftware.com/device-monitoring-studio) or IRPStack (https://github.com/stievie/irpstack).
   * The Goal: Monitor traffic to \Device\HpSAMM (the HP System Access Manager Module).
   * Why? Sometimes the WMI calls fail because the driver expects a specific "Session ID" or "Security Token" passed in the IOCTL buffer that isn't visible in the high-level C# code.

  6. Reverse-Engineering the "Settings Blob" (Types 45/46)
  Your logs show 131080 / Type 45 (Read 128 bytes) and Type 46 (Write 128 bytes).
   * This is almost certainly a State Structure.
   * By taking "snapshots" of this 128-byte buffer in different modes (Eco vs. Performance), you can map out which offsets correspond to PL1 limits, fan offsets, and GPU TGP.
   * If you crack this structure, you can bypass the "SetMode" command and just write the raw hardware state yourself.

  My recommendation for your next move: Use dnSpy to look at HP.Omen.Background.PerformanceControl.Entry.RestoreStorageSettings. That method likely defines the structure of the 128-byte blob, which is the "master key" to the
  hardware.
  
  What I’d recommend instead (highest leverage, lowest risk):
  
    - Do differential “state mapping” via BIOS blob (45/46): with OMEN BG running, switch modes (Eco/Default/Perf/Unleashed), then immediately read commandType=45 (128 bytes) and save snapshots; diff them to discover which
      offsets actually change per mode.
    - Build readback that doesn’t depend on PipeClientV3: prefer commandType=38 (max fan read), 40 (system design data), 45 (status/settings blob), and any stable registry breadcrumbs you can confirm via ProcMon.
    - Use ProcMon as your “truth oracle”: capture only around a mode change and look for deterministic registry/file writes you can safely poll (only if you can confirm they track reality).
    - Turn on WMI Activity tracing to see exactly which namespaces/methods the HP stack calls; use that to confirm whether your standalone path is missing a prerequisite call/sequence (without patching anything).
    - Focus your code on availability + safety: detect Local\\OMENCC_PIPE_PerformanceControlFg<SessionId> and clearly surface “HP BG required / unavailable” rather than trying to force writes that you can’t verify.
    - Deprioritize IOCTL sniffing unless you’re ready for kernel-driver tooling and lots of false leads; it’s rarely the shortest path to a safe user-facing utility.
