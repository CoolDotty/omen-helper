using System;
using System.Threading.Tasks;
using HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums;
using OmenHelper.Application.State;
using OmenHelper.Domain.Graphics;
using OmenHelper.Infrastructure.Bios;

namespace OmenHelper.Application.Services;

internal sealed class GraphicsModeService
{
    private readonly SharedSessionState _state;
    private readonly OmenBiosClient _biosClient;

    public GraphicsModeService(
        SharedSessionState state,
        OmenBiosClient biosClient)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _biosClient = biosClient ?? throw new ArgumentNullException(nameof(biosClient));
    }

    public async Task<bool> SetGraphicsModeAsync(GraphicsSwitcherMode mode)
    {
        try
        {
            _state.LastGraphicsRequestMode = mode.ToString();
            _state.LastGraphicsRequestReturnCode = null;

            if (!IsGraphicsModeSupported(mode))
            {
                _state.Log("Graphics mode " + mode + " is not supported on this platform.");
                _state.RaiseStateChanged();
                return false;
            }

            int returnCode = await _biosClient.SetGraphicsModeAsync(mode).ConfigureAwait(false);
            _state.LastGraphicsRequestReturnCode = returnCode;
            _state.Log("Requested graphics mode change to " + mode + ". BIOS return code: " + returnCode + ".");
            RefreshGraphicsMode();
            _state.RaiseStateChanged();
            return returnCode == 0;
        }
        catch (Exception ex)
        {
            _state.Log("Graphics mode change failed: " + ex.Message);
            return false;
        }
    }

    public void RefreshGraphicsMode()
    {
        try
        {
            _state.CurrentGraphicsMode = _biosClient.GetGraphicsModeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _state.Log("Graphics mode read failed: " + ex.Message);
            _state.CurrentGraphicsMode = GraphicsSwitcherMode.Unknown;
        }
    }

    public void RefreshGraphicsSupport()
    {
        try
        {
            SystemDesignDataInfo systemDesignDataInfo = _biosClient.GetSystemDesignDataInfoAsync().GetAwaiter().GetResult();
            _state.GraphicsModeSwitchBits = systemDesignDataInfo.RawGpuModeSwitch;
            _state.GraphicsModeSwitchReadSucceeded = systemDesignDataInfo.ReadSucceeded;
            RefreshGraphicsSupportFromProbe(systemDesignDataInfo);
            _state.Log(
                "Graphics support refresh: BIOS bits=0x" + _state.GraphicsModeSwitchBits.ToString("X2") +
                ", readSucceeded=" + _state.GraphicsModeSwitchReadSucceeded +
                ", supported=" + _state.GraphicsModeSwitchSupported +
                ", uma=" + _state.GraphicsSupportsUma +
                ", hybrid=" + _state.GraphicsSupportsHybrid + ".");
        }
        catch (Exception ex)
        {
            _state.Log("Graphics support read failed: " + ex.Message);
            _state.GraphicsModeSwitchBits = 0;
            _state.GraphicsModeSwitchReadSucceeded = false;
            RefreshGraphicsSupportFromFallback();
            _state.Log("Graphics support refresh: BIOS bits=0x00, readSucceeded=False, supported=False, uma=False, hybrid=False.");
        }
    }

    public void RefreshGraphicsSupportFromProbe(SystemDesignDataInfo systemDesignDataInfo)
    {
        if (systemDesignDataInfo.ReadSucceeded && systemDesignDataInfo.SupportsGraphicsSwitching)
        {
            _state.GraphicsModeSwitchSupported = true;
            _state.GraphicsSupportsHybrid = systemDesignDataInfo.HasHybridSlot;
            _state.GraphicsSupportsUma = systemDesignDataInfo.HasIntegratedSlot;
            _state.GraphicsNeedsReboot = true;
            return;
        }

        _state.GraphicsModeSwitchSupported = false;
        _state.GraphicsSupportsHybrid = false;
        _state.GraphicsSupportsUma = false;
        _state.GraphicsNeedsReboot = false;
    }

    public void RefreshGraphicsSupportFromFallback()
    {
        _state.GraphicsModeSwitchSupported = false;
        _state.GraphicsSupportsHybrid = false;
        _state.GraphicsSupportsUma = false;
        _state.GraphicsNeedsReboot = false;
    }

    public bool IsGraphicsModeSupported(GraphicsSwitcherMode mode)
    {
        switch (mode)
        {
            case GraphicsSwitcherMode.Hybrid:
                return _state.GraphicsModeSwitchSupported;
            case GraphicsSwitcherMode.UMAMode:
                return _state.GraphicsModeSwitchSupported;
            default:
                return false;
        }
    }
}
