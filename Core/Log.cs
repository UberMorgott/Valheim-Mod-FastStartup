using System;
using BepInEx.Logging;

namespace FastStartup.Core
{
    /// <summary>Single BepInEx log source for the patcher; every hook error goes through <see cref="Guard"/>.</summary>
    internal static class Log
    {
        private static readonly ManualLogSource Source = Logger.CreateLogSource("FastStartup");

        public static void Info(string message) => Source.LogInfo(message);

        public static void Warning(string message) => Source.LogWarning(message);

        public static void Error(string message) => Source.LogError(message);

        /// <summary>Runs <paramref name="action"/> and logs instead of throwing: the profiler must never break the game.</summary>
        public static void Guard(string where, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Error($"{where} failed: {e}");
            }
        }
    }
}
