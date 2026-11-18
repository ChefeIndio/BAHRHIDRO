using System.Collections.Generic;

namespace BahrHidro.Core.Hydraulic
{
    public sealed record Fitting(string Name, double KValue);

    public static class FittingCatalog
    {
        public static IReadOnlyList<Fitting> GetStandardFittings()
        {
            return new List<Fitting>
            {
                new("Joelho 90°", 1.2),
                new("Joelho 45°", 0.4),
                new("Curva 90°", 0.4),
                new("Curva 45°", 0.2),
                new("Tê (Passagem Direta)", 0.9),
                new("Tê (Saída Lateral)", 1.3),
                new("Tê (Bilateral)", 1.8),
                new("Registro de Gaveta (Aberto)", 0.2),
                new("Registro de Pressão (Aberto)", 10.0),
                new("Válvula de Retenção", 2.5),
                new("Entrada de Borda", 1.0),
                new("Saída de Tubulação", 1.0)
            }.AsReadOnly();
        }
    }
}