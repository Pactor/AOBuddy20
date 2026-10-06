using System;
using System.IO;
using Newtonsoft.Json;

namespace AOBuddyMonitor
{
    /// <summary>
    /// The monitor's own little config, monitor.json beside the exe (created with defaults on first run).
    ///   host / port   where the bot's BotApi listens (same PC: 127.0.0.1, AccountInfo.BotApiPort — 5592)
    ///   pluginDir     the bot's working folder, for GameData/Nav/&lt;pf&gt;/ (terrain) and nav/&lt;pf&gt;.json (footsteps);
    ///                 the default walks up from the exe until it finds the solution's shared Build/ folder
    ///                 (AOBuddy20: the whole solution builds flat into it — bot exe, GameData and nav all in
    ///                 one place), so it works from tools/AOBuddyMonitor/bin/Debug and from a published
    ///                 folder dropped in the repo.
    /// </summary>
    public sealed class MonitorConfig
    {
        public string Host = "127.0.0.1";
        public int Port = 5592;
        public string PluginDir = "";

        public string Base => $"http://{Host}:{Port}";

        public static MonitorConfig Load()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "monitor.json");
            MonitorConfig c;
            try { c = File.Exists(path) ? JsonConvert.DeserializeObject<MonitorConfig>(File.ReadAllText(path)) ?? new MonitorConfig() : new MonitorConfig(); }
            catch { c = new MonitorConfig(); }
            if (string.IsNullOrEmpty(c.PluginDir)) c.PluginDir = FindPluginDir();
            try { File.WriteAllText(path, JsonConvert.SerializeObject(c, Formatting.Indented)); } catch { }
            return c;
        }

        // tools/AOBuddyMonitor/bin/Debug → up four is the repo root; keep walking a while anyway
        // so a published folder anywhere inside the repo also finds it.
        private static string FindPluginDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
            {
                // AOBuddy20 builds the whole solution flat into <repo>/Build: the bot exe, its
                // GameData\Nav\<pf>\ terrain and its nav\<pf>.json footsteps all live there.
                string build = Path.Combine(dir.FullName, "Build");
                if (Directory.Exists(Path.Combine(build, "GameData", "Nav"))) return build;
            }
            // A fresh clone's Build exists but has no extracted GameData yet — still the right folder
            // (the extractor writes it there, and nav/ appears when the bot first walks).
            dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
            {
                string build = Path.Combine(dir.FullName, "Build");
                if (File.Exists(Path.Combine(build, "AOBuddy20.dll"))) return build;
            }
            return "";
        }
    }
}