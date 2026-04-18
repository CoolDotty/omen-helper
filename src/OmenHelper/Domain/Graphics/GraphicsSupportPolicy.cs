namespace OmenHelper.Domain.Graphics;

internal static class GraphicsSupportPolicy
{
    public static string FormatDisplayName(string currentGraphicsMode)
    {
        if (string.IsNullOrWhiteSpace(currentGraphicsMode))
        {
            return "<unknown>";
        }

        if (string.Equals(currentGraphicsMode, HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums.GraphicsSwitcherMode.UMAMode.ToString(), System.StringComparison.OrdinalIgnoreCase))
        {
            return "Integrated";
        }

        if (string.Equals(currentGraphicsMode, HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums.GraphicsSwitcherMode.Hybrid.ToString(), System.StringComparison.OrdinalIgnoreCase))
        {
            return "Hybrid";
        }

        return currentGraphicsMode;
    }
}
