using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BahrHidro.Core.Detailing
{
    public sealed class DetalhamentoFinoOpcoes
    {
        public bool ReorganizarTagsSemSobreposicao { get; set; } = true;
        public bool AplicarPadraoGraficoABNT { get; set; } = true;
        public bool GerarElevacoesParedes120 { get; set; } = true;
        public bool InserirNotasTecnicasEBOM { get; set; } = true;
    }

    public sealed class TagBox2D
    {
        public long ElementIdValue { get; set; }
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }

        public double Width => Math.Max(0.15, MaxX - MinX);
        public double Height => Math.Max(0.08, MaxY - MinY);

        public bool Intersects(TagBox2D other, double margin = 0.04)
        {
            return !(MaxX + OffsetX + margin < other.MinX + other.OffsetX ||
                     MinX + OffsetX - margin > other.MaxX + other.OffsetX ||
                     MaxY + OffsetY + margin < other.MinY + other.OffsetY ||
                     MinY + OffsetY - margin > other.MaxY + other.OffsetY);
        }
    }

    public readonly struct SistemaCorABNT
    {
        public string Sigla { get; }
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public int LineWeight { get; }

        public SistemaCorABNT(string sigla, byte r, byte g, byte b, int lineWeight)
        {
            Sigla = sigla;
            R = r;
            G = g;
            B = b;
            LineWeight = lineWeight;
        }
    }

    public static class DetalhamentoFinoPlanner
    {
        // Callback registrado pelo BahrHidro.Revit para executar na API do Revit 2026
        public static Func<DetalhamentoFinoOpcoes, string>? ExecutarDetalhamentoFinoCallback { get; set; }

        public static SistemaCorABNT ObterCorSistemaABNT(string? nomeSistemaOuClassificacao)
        {
            string s = (nomeSistemaOuClassificacao ?? string.Empty).ToLowerInvariant();

            if (s.Contains("quente") || s.Contains("hot") || s.Contains("aq"))
                return new SistemaCorABNT("AQ", 217, 56, 30, 5);

            if (s.Contains("ventil") || s.Contains("vent") || s.Contains("ve"))
                return new SistemaCorABNT("VE", 0, 153, 102, 4);

            if (s.Contains("esgoto") || s.Contains("sanit") || s.Contains("drain") || s.Contains("waste") || s.Contains("es"))
                return new SistemaCorABNT("ES", 139, 90, 43, 6);

            if (s.Contains("pluvial") || s.Contains("chuva") || s.Contains("storm") || s.Contains("ap"))
                return new SistemaCorABNT("AP", 0, 153, 204, 5);

            // Padrão Água Fria (AF)
            return new SistemaCorABNT("AF", 0, 112, 192, 5);
        }

        /// <summary>
        /// Resolve colisões 2D entre etiquetas testando deslocamentos em espiral/quadrantes (em pés do Revit).
        /// </summary>
        public static int ResolverColisoes(IList<TagBox2D> caixas)
        {
            int movidas = 0;
            var colocadas = new List<TagBox2D>();

            // Ordena da esquerda para direita, de cima para baixo para estabilizar o layout
            var ordenadas = caixas
                .OrderByDescending(c => c.MaxY)
                .ThenBy(c => c.MinX)
                .ToList();

            (double dx, double dy)[] direcoes =
            {
                ( 1.0,  1.0),
                ( 1.0, -1.0),
                (-1.0,  1.0),
                (-1.0, -1.0),
                ( 0.0,  1.2),
                ( 0.0, -1.2),
                ( 1.4,  0.0),
                (-1.4,  0.0)
            };

            foreach (var atual in ordenadas)
            {
                bool colideInicial = colocadas.Any(c => atual.Intersects(c));
                if (!colideInicial)
                {
                    colocadas.Add(atual);
                    continue;
                }

                bool encontrou = false;
                double stepX = Math.Max(0.45, atual.Width * 0.85);
                double stepY = Math.Max(0.30, atual.Height * 1.15);

                for (int anel = 1; anel <= 8 && !encontrou; anel++)
                {
                    foreach (var (dx, dy) in direcoes)
                    {
                        atual.OffsetX = dx * stepX * anel;
                        atual.OffsetY = dy * stepY * anel;

                        if (!colocadas.Any(c => atual.Intersects(c)))
                        {
                            encontrou = true;
                            movidas++;
                            break;
                        }
                    }
                }

                if (!encontrou)
                {
                    atual.OffsetY = stepY * (colocadas.Count % 6 + 1);
                    movidas++;
                }

                colocadas.Add(atual);
            }

            return movidas;
        }

        public static string GerarTextoNotasExecutivasABNT(
            IDictionary<string, double> metragemPorBitola,
            IDictionary<string, int> contagemConexoes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("NOTAS TÉCNICAS EXECUTIVAS — INSTALAÇÕES HIDROSSANITÁRIAS (SMART HIDRO BIM)");
            sb.AppendLine("1. Normas aplicáveis: ABNT NBR 5626 (Água Fria/Quente), NBR 8160 (Esgoto) e NBR 10844 (Pluvial).");
            sb.AppendLine("2. Declividades mínimas de esgoto: DN <= 75 mm usar i = 2,0%; DN >= 100 mm usar i = 1,0%.");
            sb.AppendLine("3. Pontos hidráulicos padrão (eixo acabado): Lavatório H=0,60m | Bacia c/ caixa H=0,20m | Chuveiro H=2,10m | Reg. Gaveta H=1,80m.");
            sb.AppendLine("4. Realizar ensaio de estanqueidade antes do fechamento das paredes/shafts (mín. 1,5x pressão de serviço).");
            sb.AppendLine();
            sb.AppendLine("RESUMO QUANTITATIVO DO MODELO 3D:");

            if (metragemPorBitola.Count > 0)
            {
                var tubosTxt = metragemPorBitola
                    .OrderBy(k => k.Key)
                    .Select(k => $"{k.Key}: {k.Value:0.0} m");
                sb.AppendLine("• Tubulações: " + string.Join(" | ", tubosTxt));
            }
            else
            {
                sb.AppendLine("• Tubulações: Nenhuma tubulação modelada na vista.");
            }

            if (contagemConexoes.Count > 0)
            {
                var conTxt = contagemConexoes
                    .OrderByDescending(k => k.Value)
                    .Take(8)
                    .Select(k => $"{k.Key}: {k.Value} un");
                sb.AppendLine("• Conexões: " + string.Join(" | ", conTxt));
            }

            return sb.ToString();
        }
    }
}
