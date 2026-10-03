using Microsoft.Build.Locator;

namespace XamlG.Workspaces;

internal static class MSBuildBootstrapper
{
    private static readonly object Gate = new();

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (MSBuildLocator.IsRegistered) return;
            if (!MSBuildLocator.CanRegister)
                throw new InvalidOperationException("MSBuild assemblies were loaded before toolchain registration. Register Microsoft.Build.Locator before creating the workspace host.");
            MSBuildLocator.RegisterDefaults();
        }
    }
}
