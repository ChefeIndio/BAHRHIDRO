using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BahrHidro.Core.Catalog
{
    public sealed class CatalogoItemSyncDto
    {
        public string Codigo { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string Sistema { get; set; } = "Água Fria";
        public string Material { get; set; } = string.Empty;
        public string Bitolas { get; set; } = string.Empty;
        public string CaminhoRfa { get; set; } = string.Empty;
        public bool EhTubo { get; set; }
    }

    public static class CatalogoRevitBridge
    {
        public static Func<CatalogoItemSyncDto, string>? InserirNoRevitCallback { get; set; }
        public static Func<IEnumerable<CatalogoItemSyncDto>, string>? SincronizarTubosTemplateCallback { get; set; }

        private static string ArquivoPersistencia => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Autodesk\Revit\Addins\2026\SmartHidro\CatalogoPersonalizado.json");

        public static void SalvarItensManuais(IEnumerable<CatalogoItemSyncDto> itens)
        {
            try
            {
                string? dir = Path.GetDirectoryName(ArquivoPersistencia);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(itens, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ArquivoPersistencia, json);
            }
            catch { }
        }

        public static List<CatalogoItemSyncDto> CarregarItensManuais()
        {
            try
            {
                if (!File.Exists(ArquivoPersistencia)) return new List<CatalogoItemSyncDto>();
                string json = File.ReadAllText(ArquivoPersistencia);
                return JsonSerializer.Deserialize<List<CatalogoItemSyncDto>>(json) ?? new List<CatalogoItemSyncDto>();
            }
            catch
            {
                return new List<CatalogoItemSyncDto>();
            }
        }
    }
}
