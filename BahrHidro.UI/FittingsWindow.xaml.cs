using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BahrHidro.Core.Hydraulic;

namespace BahrHidro.UI
{
    public partial class FittingsWindow : Window
    {
        public double TotalK { get; private set; } = 0;
        private readonly List<FittingViewModel> _fittings;

        public FittingsWindow()
        {
            InitializeComponent();
            UiNavigationController.Initialize();
            
            // Vai buscar as conexões ao Core automaticamente
            _fittings = FittingCatalog.GetStandardFittings()
                .Select(f => new FittingViewModel { Name = f.Name, KValue = f.KValue })
                .ToList();
                
            FittingsItemsControl.ItemsSource = _fittings;
        }

        private void Confirmar_Click(object sender, RoutedEventArgs e)
        {
            // Calcula o total do K com base nas quantidades preenchidas
            TotalK = _fittings.Sum(f => f.KValue * f.Quantity);
            DialogResult = true;
            Close();
        }
    }

    public class FittingViewModel
    {
        public string Name { get; set; } = "";
        public double KValue { get; set; }
        public string KValueDisplay => $"K = {KValue:F1}";
        public int Quantity { get; set; } = 0;
    }
}