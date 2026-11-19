using System;
using System.IO;
using System.Text.Json;

namespace BahrHidro.UI
{
    public sealed class OfficeProfileSettings
    {
        public string? PreferredPipeTypeName { get; set; }
        public string? PreferredSystemTypeName { get; set; }
        public double DefaultTopOffsetMeters { get; set; } = 2.60;
        public double DefaultBottomOffsetMeters { get; set; } = -0.15;
        public double DefaultWasteMarginPercent { get; set; } = 10.0;
        public bool AvoidSteelFrameClashes { get; set; } = true;
        public bool InsertValvesAutomatically { get; set; } = true;
    }

    public static class OfficeProfileManager
    {
        private static readonly string ProfileFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BahrHidro");

        private static readonly string ProfileFilePath = Path.Combine(ProfileFolder, "office_profile.json");

        public static OfficeProfileSettings Load()
        {
            try
            {
                if (File.Exists(ProfileFilePath))
                {
                    string json = File.ReadAllText(ProfileFilePath);
                    var loaded = JsonSerializer.Deserialize<OfficeProfileSettings>(json);
                    if (loaded != null)
                        return loaded;
                }
            }
            catch { }

            return new OfficeProfileSettings();
        }

        public static void Save(OfficeProfileSettings settings)
        {
            try
            {
                Directory.CreateDirectory(ProfileFolder);
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ProfileFilePath, json);
            }
            catch { }
        }
    }
}
