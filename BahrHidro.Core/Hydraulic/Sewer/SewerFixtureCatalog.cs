using System.Collections.Generic;

namespace BahrHidro.Core.Hydraulic.Sewer
{
    public sealed record SewerFixture(string Name, int UhcValue, int MinimumDiameterMm);

    public static class SewerFixtureCatalog
    {
        public static IReadOnlyList<SewerFixture> GetStandardFixtures()
        {
            return new List<SewerFixture>
            {
                new("Bacia Sanitária (Sanita)", 6, 100),
                new("Lavatório (Pia de WC)", 1, 40),
                new("Pia de Cozinha", 3, 50),
                new("Chuveiro", 2, 40),
                new("Banheira", 2, 40),
                new("Máquina de Lavar Roupa", 3, 50),
                new("Tanque de Lavar Roupa", 3, 40),
                new("Ralo Sifonado", 2, 40),
                new("Mictório (Válvula)", 3, 50)
            }.AsReadOnly();
        }
    }
}