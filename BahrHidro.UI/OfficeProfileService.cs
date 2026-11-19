using System;
using System.IO;
using System.Text.Json;

namespace BahrHidro.UI
{
    public sealed class OfficeProfileConfig
    {
        public string ProfileName { get; set; } = "Padrão Escritório LSF - ABNT NBR 5626 / 8160";
        public double AlturaLavatorioM { get; set; } = 0.60;
        public double AlturaVasoSanitarioM { get; set; } = 0.20;
        public double AlturaChuveiroM { get; set; } = 2.10;
        public double AlturaPiaCozinhaM { get; set; } = 0.60;
        public double AlturaTanqueMaquinaM { get; set; } = 0.90;

        public double MaxFuroAlmaPercent { get; set; } = 50.0;
        public double DistanciaMinimaGuiaCm { get; set; } = 25.0;
        public double FolgaFuroMm { get; set; } = 6.0;

        public double VelocidadeMaximaMs { get; set; } = 3.0;
        public double PressaoMinimaDinamicaMca { get; set; } = 1.0;
        public double ComprimentoVaraTuboM { get; set; } = 6.0;
    }

    public static class OfficeProfileService
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static string DefaultProfilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BahrHidro");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "office_profile.json");
            }
        }

        public static OfficeProfileConfig LoadCurrent()
        {
            try
            {
                string path = DefaultProfilePath;
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var loaded = JsonSerializer.Deserialize<OfficeProfileConfig>(json, JsonOpts);
                    if (loaded != null)
                        return loaded;
                }
            }
            catch { }

            return new OfficeProfileConfig();
        }

        public static void SaveCurrent(OfficeProfileConfig config)
        {
            string json = JsonSerializer.Serialize(config, JsonOpts);
            File.WriteAllText(DefaultProfilePath, json);
        }

        public static void ExportToFile(OfficeProfileConfig config, string filePath)
        {
            string json = JsonSerializer.Serialize(config, JsonOpts);
            File.WriteAllText(filePath, json);
        }

        public static OfficeProfileConfig ImportFromFile(string filePath)
        {
            string json = File.ReadAllText(filePath);
            var loaded = JsonSerializer.Deserialize<OfficeProfileConfig>(json, JsonOpts)
                ?? throw new InvalidOperationException("O arquivo JSON selecionado não possui um perfil válido do Bahr Hidro.");
            SaveCurrent(loaded);
            return loaded;
        }
    }
}
