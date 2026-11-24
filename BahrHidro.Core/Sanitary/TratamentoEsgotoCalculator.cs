using System;

namespace BahrHidro.Core.Sanitary
{
    public enum PadraoOcupacaoEsgoto
    {
        ResidenciaAltoPadrao,
        ResidenciaMedioPadrao,
        ResidenciaBaixoPadrao,
        AlojamentoProvisorio,
        EscritorioComercio,
        EscolaLongaPermanencia
    }

    public sealed class TratamentoEsgotoInput
    {
        public int NumeroContribuintes { get; set; } = 5;
        public PadraoOcupacaoEsgoto PadraoOcupacao { get; set; } = PadraoOcupacaoEsgoto.ResidenciaMedioPadrao;
        public double TemperaturaMesMaisFrioC { get; set; } = 18.0;
        public int IntervaloLimpezaAnos { get; set; } = 1;
        public double CoeficienteInfiltracaoSolo { get; set; } = 60.0; // L/m2.dia
        public double DiametroSumidouroM { get; set; } = 1.50;
    }

    public sealed class TratamentoEsgotoResultado
    {
        public double ContribuicaoDiariaPessoaL { get; set; }
        public double LodoFrescoPessoaL { get; set; }
        public double VazaoTotalDiariaL { get; set; }
        public double TempoDetencaoDias { get; set; }
        public int TaxaAcumulacaoLodoK { get; set; }

        // Fossa Séptica (NBR 7229)
        public double VolumeFossaCalculadoL { get; set; }
        public double VolumeFossaAdotadoL { get; set; }
        public double FossaCilindricaDiametroM { get; set; }
        public double FossaCilindricaAlturaUtilM { get; set; }
        public double FossaPrismaticaLarguraM { get; set; }
        public double FossaPrismaticaComprimentoM { get; set; }
        public double FossaPrismaticaAlturaUtilM { get; set; }

        // Filtro Anaeróbio (NBR 13969)
        public double VolumeFiltroCalculadoL { get; set; }
        public double VolumeFiltroAdotadoL { get; set; }
        public double FiltroCilindricoDiametroM { get; set; }
        public double FiltroAlturaLeitoM { get; set; }

        // Sumidouro (NBR 13969)
        public double AreaInfiltracaoNecessariaM2 { get; set; }
        public double SumidouroDiametroAdotadoM { get; set; }
        public double SumidouroAlturaUtilM { get; set; }
        public string AlertaNormativo { get; set; } = string.Empty;
    }

    public static class TratamentoEsgotoCalculator
    {
        public static TratamentoEsgotoResultado Calcular(TratamentoEsgotoInput input)
        {
            int n = Math.Max(1, input.NumeroContribuintes);
            int anosLimpeza = Math.Clamp(input.IntervaloLimpezaAnos, 1, 5);
            double ci = Math.Max(10.0, input.CoeficienteInfiltracaoSolo);
            double dSumidouro = Math.Max(0.90, input.DiametroSumidouroM);

            var (c, lf) = ObterContribuicoesTabela1(input.PadraoOcupacao);
            double qDiaria = n * c;
            double td = ObterTempoDetencaoTabela2(qDiaria);
            int k = ObterTaxaAcumulacaoTabela3(anosLimpeza, input.TemperaturaMesMaisFrioC);

            // 1. Tanque Séptico (ABNT NBR 7229/1993: V = 1000 + N * (C * Td + K * Lf))
            double vFossaCalc = 1000.0 + n * (c * td + k * lf);
            double vFossaAdotado = Math.Max(1250.0, Math.Ceiling(vFossaCalc / 250.0) * 250.0);
            double vFossaM3 = vFossaAdotado / 1000.0;

            // Geometria Cilíndrica (D >= 1,10 m; h >= 1,20 m; D <= 2h)
            double hCil = vFossaM3 <= 3.0 ? 1.30 : (vFossaM3 <= 6.0 ? 1.60 : 1.90);
            double dCil = Math.Sqrt((4.0 * vFossaM3) / (Math.PI * hCil));
            if (dCil < 1.10)
            {
                dCil = 1.10;
                hCil = Math.Max(1.20, vFossaM3 / (Math.PI * Math.Pow(dCil / 2.0, 2)));
            }
            dCil = Math.Ceiling(dCil * 20.0) / 20.0; // arredonda para múltiplo de 5 cm
            hCil = Math.Ceiling(hCil * 20.0) / 20.0;

            // Geometria Prismática Retangular (B >= 0,80 m; L = 2*B; 1,20 <= h <= 2,20 m)
            double hPris = vFossaM3 <= 4.0 ? 1.30 : 1.60;
            double areaBase = vFossaM3 / hPris;
            double bPris = Math.Max(0.80, Math.Sqrt(areaBase / 2.0));
            bPris = Math.Ceiling(bPris * 20.0) / 20.0;
            double lPris = Math.Ceiling((2.0 * bPris) * 20.0) / 20.0;

            // 2. Filtro Anaeróbio (ABNT NBR 13969/1997: Vu = 1,6 * N * C * Td, mín. 1000 L, h_leito = 1,20 m)
            double vFiltroCalc = 1.6 * n * c * td;
            double vFiltroAdotado = Math.Max(1000.0, Math.Ceiling(vFiltroCalc / 250.0) * 250.0);
            double hLeito = 1.20;
            double dFiltro = Math.Sqrt((4.0 * (vFiltroAdotado / 1000.0)) / (Math.PI * hLeito));
            dFiltro = Math.Max(0.95, Math.Ceiling(dFiltro * 20.0) / 20.0);

            // 3. Sumidouro (ABNT NBR 13969/1997: A = (N * C) / Ci)
            double areaInf = qDiaria / ci;
            double areaFundo = Math.PI * Math.Pow(dSumidouro / 2.0, 2);
            double areaLateralNec = Math.Max(0.0, areaInf - areaFundo);
            double hSumidouro = areaLateralNec / (Math.PI * dSumidouro);
            hSumidouro = Math.Max(1.00, Math.Ceiling(hSumidouro * 10.0) / 10.0);

            string alerta = string.Empty;
            if (ci < 30.0)
                alerta = "Solo de baixa permeabilidade (Ci < 30 L/m².dia): considere substituir o sumidouro por Vala de Infiltração ou Vala de Filtração (NBR 13969).";
            else if (hSumidouro > 3.20)
                alerta = "Profundidade elevada: divida em 2 sumidouros em paralelo ou aumente o diâmetro para manter distância mínima de 1,50 m acima do lençol freático.";
            else
                alerta = "Manter distância mínima vertical de 1,50 m entre o fundo do sumidouro e o nível máximo do aquífero (NBR 13969).";

            return new TratamentoEsgotoResultado
            {
                ContribuicaoDiariaPessoaL = c,
                LodoFrescoPessoaL = lf,
                VazaoTotalDiariaL = qDiaria,
                TempoDetencaoDias = td,
                TaxaAcumulacaoLodoK = k,
                VolumeFossaCalculadoL = Math.Round(vFossaCalc, 1),
                VolumeFossaAdotadoL = vFossaAdotado,
                FossaCilindricaDiametroM = dCil,
                FossaCilindricaAlturaUtilM = hCil,
                FossaPrismaticaLarguraM = bPris,
                FossaPrismaticaComprimentoM = lPris,
                FossaPrismaticaAlturaUtilM = hPris,
                VolumeFiltroCalculadoL = Math.Round(vFiltroCalc, 1),
                VolumeFiltroAdotadoL = vFiltroAdotado,
                FiltroCilindricoDiametroM = dFiltro,
                FiltroAlturaLeitoM = hLeito,
                AreaInfiltracaoNecessariaM2 = Math.Round(areaInf, 2),
                SumidouroDiametroAdotadoM = dSumidouro,
                SumidouroAlturaUtilM = hSumidouro,
                AlertaNormativo = alerta
            };
        }

        private static (double C, double Lf) ObterContribuicoesTabela1(PadraoOcupacaoEsgoto padrao)
        {
            return padrao switch
            {
                PadraoOcupacaoEsgoto.ResidenciaAltoPadrao => (160.0, 1.0),
                PadraoOcupacaoEsgoto.ResidenciaMedioPadrao => (130.0, 1.0),
                PadraoOcupacaoEsgoto.ResidenciaBaixoPadrao => (100.0, 1.0),
                PadraoOcupacaoEsgoto.AlojamentoProvisorio => (80.0, 1.0),
                PadraoOcupacaoEsgoto.EscritorioComercio => (50.0, 0.20),
                PadraoOcupacaoEsgoto.EscolaLongaPermanencia => (50.0, 0.20),
                _ => (130.0, 1.0)
            };
        }

        private static double ObterTempoDetencaoTabela2(double qDiariaLitros)
        {
            if (qDiariaLitros <= 1500.0) return 1.00;
            if (qDiariaLitros <= 3000.0) return 0.92;
            if (qDiariaLitros <= 4500.0) return 0.83;
            if (qDiariaLitros <= 6000.0) return 0.75;
            if (qDiariaLitros <= 7500.0) return 0.67;
            if (qDiariaLitros <= 9000.0) return 0.58;
            return 0.50;
        }

        private static int ObterTaxaAcumulacaoTabela3(int anos, double tempC)
        {
            int faixaTemp = tempC <= 10.0 ? 0 : (tempC <= 20.0 ? 1 : 2);
            int[,] tabelaK =
            {
                { 94,  65,  57 }, // 1 ano
                { 134, 105, 97 }, // 2 anos
                { 174, 145, 137 }, // 3 anos
                { 214, 185, 177 }, // 4 anos
                { 254, 225, 217 }  // 5 anos
            };
            return tabelaK[anos - 1, faixaTemp];
        }
    }
}
