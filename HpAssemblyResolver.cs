using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OmenHelper;

internal static class HpAssemblyResolver
{
    private static readonly string[] SearchDirectories =
    {
        @"C:\Program Files\HP\SystemOptimizer",
        @"C:\Program Files\HP\Overlay",
        @"C:\Program Files\HP\OmenInstallMonitor",
        @"C:\Program Files\HP\KeyboardRemap"
    };

    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        _registered = true;
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        AssemblyName requestedName = new AssemblyName(args.Name);
        string fileName = requestedName.Name + ".dll";

        foreach (string directory in SearchDirectories.Where(Directory.Exists))
        {
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return Assembly.LoadFrom(candidate);
            }
        }

        return null;
    }
}
