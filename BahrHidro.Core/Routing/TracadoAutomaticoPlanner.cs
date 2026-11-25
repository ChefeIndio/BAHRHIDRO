using System;
using System.Collections.Generic;

namespace BahrHidro.Core.Routing
{
    public sealed class PontoTracadoItem
    {
        public long ElementIdValue { get; set; }
        public string TipoAparelho { get; set; } = "Lavatório";
        public double AlturaPontoMetros { get; set; } = 0.60;
        public double DiametroMm { get; set; } = 25.0;
    }

    public static class TracadoAutomaticoPlanner
    {
        public static Func<IList<PontoTracadoItem>, string>? GerarTracado3DCallback { get; set; }
        public static Func<string>? DetectarAguaFriaCallback { get; set; }

        public static double ObterAlturaNormativaMetros(string? tipoOuNome)
        {
            string s = (tipoOuNome ?? string.Empty).ToLowerInvariant();
            if (s.Contains("chuveiro") || s.Contains("ducha") || s.Contains("shower")) return 2.10;
            if (s.Contains("vaso") || s.Contains("bacia") || s.Contains("sanit") || s.Contains("wc") || s.Contains("toilet")) return 0.20;
            if (s.Contains("caixa") && s.Contains("acopl")) return 0.20;
            if (s.Contains("valvula") || s.Contains("válvula") || s.Contains("descarga")) return 1.10;
            if (s.Contains("tanque") || s.Contains("maquina") || s.Contains("máquina") || s.Contains("lavar")) return 0.90;
            if (s.Contains("pia") || s.Contains("cozinha") || s.Contains("sink")) return 1.00;
            if (s.Contains("mictorio") || s.Contains("mictório")) return 1.05;
            return 0.60; // Lavatório / Bidê padrão
        }
    }
}
