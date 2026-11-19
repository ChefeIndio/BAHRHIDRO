using System.Windows;

namespace BahrHidro.UI
{
    public partial class LicenseActivationWindow : Window
    {
        public LicenseActivationWindow()
        {
            InitializeComponent();
            MachineIdTextBox.Text = LicenseService.GetMachineId();

            var current = LicenseService.ValidateStoredLicense();
            if (current.IsValid)
            {
                ActivationStatusText.Text = current.Message;
                ActivationStatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;
            }
        }

        private void CopiarMachineId_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(MachineIdTextBox.Text);
            ActivationStatusText.Text = $"ID da Máquina ({MachineIdTextBox.Text}) copiado para a área de transferência.";
            ActivationStatusText.Foreground = System.Windows.Media.Brushes.DarkSlateGray;
        }

        private void AtivarLicenca_Click(object sender, RoutedEventArgs e)
        {
            var res = LicenseService.ActivateAndSave(LicenseKeyTextBox.Text);
            if (res.IsValid)
            {
                ActivationStatusText.Text = res.Message;
                ActivationStatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;
                MessageBox.Show(res.Message, "Bahr Hidro BIM — Licença Ativada", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            else
            {
                ActivationStatusText.Text = res.Message;
                ActivationStatusText.Foreground = System.Windows.Media.Brushes.Crimson;
            }
        }

        private void Cancelar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
