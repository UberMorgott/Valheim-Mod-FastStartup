using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace FastStartup.Core
{
    /// <summary>BepInEx\config\FastStartup.cfg. A patcher has no BaseUnityPlugin.Config, so the file is opened directly.</summary>
    internal static class Config
    {
        public static ConfigEntry<bool> ProfilerEnabled { get; private set; }

        public static void Load()
        {
            var file = new ConfigFile(Path.Combine(Paths.ConfigPath, "FastStartup.cfg"), true);
            ProfilerEnabled = file.Bind("Profiler", "Enabled", true,
                "Record a startup trace (process start -> main menu) and write BepInEx\\FastStartup\\trace.json (Chrome trace) " +
                "and summary.txt. Read once at launch.");
        }
    }
}
