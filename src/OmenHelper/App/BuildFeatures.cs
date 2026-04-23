using System;

namespace OmenHelper;

internal static class BuildFeatures
{
    private const string DeveloperToolsEnvironmentVariable = "OMENHELPER_ENABLE_DEVTOOLS";

    public static bool DeveloperToolsEnabled
    {
        get
        {
#if DEBUG
            return true;
#else
            string value = Environment.GetEnvironmentVariable(DeveloperToolsEnvironmentVariable);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
#endif
        }
    }
}
