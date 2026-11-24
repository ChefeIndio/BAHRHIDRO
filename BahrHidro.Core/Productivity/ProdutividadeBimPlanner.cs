using System;
using System.Collections.Generic;

namespace BahrHidro.Core.Productivity
{
    public sealed class ProdutividadeBimOpcoes
    {
        public bool CortarTubosComerciais { get; set; } = true;
        public bool InverterLuvasFluxo { get; set; } = true;
        public bool AdicionarBuchaLatao { get; set; } = true;
        public bool NumerarPecasEConexoes { get; set; } = true;
        public bool NumerarColunasVerticais { get; set; } = true;
        public bool NumerarCaixasEPocosCX { get; set; } = true;
        public bool TracejarTubosVistaAtual { get; set; } = false;
        public bool UnificarParametrosETabelas { get; set; } = true;
        public bool GerarRamaisPorAmbiente { get; set; } = false;
    }

    public static class ProdutividadeBimPlanner
    {
        // Callback registrado pelo BahrHidro.Revit na inicialização do Revit 2026
        public static Func<ProdutividadeBimOpcoes, string>? ExecutarProdutividadeCallback { get; set; }

        /// <summary>
        /// Retorna o comprimento comercial padrão da barra (em metros) conforme o material do tubo:
        /// PVC = 6,0 m | Cobre = 5,0 m | CPVC / PPR = 3,0 m.
        /// </summary>
        public static (double ComprimentoMetros, string MaterialNormalizado) ObterBarraComercialMetros(string? nomeTipoOuMaterial)
        {
            string s = (nomeTipoOuMaterial ?? string.Empty).ToLowerInvariant();

            if (s.Contains("cpvc") || s.Contains("aquatherm") || s.Contains("ppr") || s.Contains("termofus"))
                return (3.0, "CPVC/PPR (3,0 m)");

            if (s.Contains("cobre") || s.Contains("copper") || s.Contains("eluma"))
                return (5.0, "Cobre (5,0 m)");

            // Padrão PVC Soldável / Esgoto / Série R
            return (6.0, "PVC (6,0 m)");
        }

        public static string ObterPrefixoColuna(string? nomeSistema)
        {
            string s = (nomeSistema ?? string.Empty).ToLowerInvariant();
            if (s.Contains("quente") || s.Contains("hot") || s.Contains("aq")) return "CAQ";
            if (s.Contains("ventil") || s.Contains("vent") || s.Contains("ve")) return "CV";
            if (s.Contains("esgoto") || s.Contains("sanit") || s.Contains("drain") || s.Contains("es")) return "TQ";
            if (s.Contains("pluvial") || s.Contains("chuva") || s.Contains("storm") || s.Contains("ap")) return "AP";
            return "CAF";
        }
    }
}
