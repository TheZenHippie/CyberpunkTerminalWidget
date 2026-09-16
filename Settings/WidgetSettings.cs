using System;
using System.IO;
using System.Text.Json;

namespace CyberpunkTerminalWidget.Settings
{
    public class WidgetSettings
    {
        public string Shell { get; set; } = "PowerShell";
        public string FontFamily { get; set; } = "Cascadia Code, Consolas, Courier New";
        public double FontSize { get; set; } = 12.0;
        public string FontColorHex { get; set; } = "#00FF66";
        public double WindowOpacity { get; set; } = 0.75;
        public double FontOpacity { get; set; } = 1.0;
        public string? BackgroundImagePath { get; set; }

        public bool RainbowBorderEnabled { get; set; } = false;
        public double BorderWidth { get; set; } = 4.0;

        public bool CrtScanlinesEnabled { get; set; } = false;
        public double ScanlineThickness { get; set; } = 3.0;

        public bool CrtGlitchEnabled { get; set; } = false;
        public double GlitchChance { get; set; } = 15.0;

        public bool CrtSnowEnabled { get; set; } = false;
        public double SnowAmount { get; set; } = 25.0;

        public bool AlwaysOnTop { get; set; } = false;
        public bool WindowShadow { get; set; } = true;

        public double? WindowWidth { get; set; } = 720;
        public double? WindowHeight { get; set; } = 460;
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }

        private static readonly object FileLock = new object();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private static string GetSettingsFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "CyberpunkTerminalWidget");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            return Path.Combine(folder, "settings.json");
        }

        public static WidgetSettings Load()
        {
            lock (FileLock)
            {
                try
                {
                    string path = GetSettingsFilePath();
                    if (File.Exists(path))
                    {
                        string json = File.ReadAllText(path);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            var settings = JsonSerializer.Deserialize<WidgetSettings>(json, JsonOptions);
                            if (settings != null)
                            {
                                return settings;
                            }
                        }
                    }
                }
                catch { }

                return new WidgetSettings();
            }
        }

        public void Save()
        {
            lock (FileLock)
            {
                try
                {
                    string path = GetSettingsFilePath();
                    string tempPath = path + ".tmp";
                    string json = JsonSerializer.Serialize(this, JsonOptions);
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, path, overwrite: true);
                }
                catch { }
            }
        }
    }
}

