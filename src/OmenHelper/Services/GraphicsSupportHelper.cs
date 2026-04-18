using System.Collections.Generic;
using OmenHelper.Models;

namespace OmenHelper.Services;

internal static class GraphicsSupportHelper
{
    internal static IEnumerable<string> BuildSupportedGraphicsModeList(PerformanceControlState state)
    {
        if (state != null && state.GraphicsModeSwitchSupported)
        {
            yield return "Hybrid";
            yield return "UMA";
        }
    }

    internal static string FormatDisplayName(string currentGraphicsMode)
    {
        if (string.Equals(currentGraphicsMode, HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums.GraphicsSwitcherMode.UMAMode.ToString(), System.StringComparison.OrdinalIgnoreCase))
        {
            return "UMA";
        }

        if (string.Equals(currentGraphicsMode, HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums.GraphicsSwitcherMode.Hybrid.ToString(), System.StringComparison.OrdinalIgnoreCase))
        {
            return "Hybrid";
        }

        return currentGraphicsMode;
    }
}
