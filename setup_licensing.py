import os, re, subprocess

# 1. Cria pasta privada FORA do repositório Git para guardar a chave privada RSA
secret_dir = os.path.expanduser("~/bahrhidro_chave_mestra")
os.makedirs(secret_dir, exist_ok=True)
priv_pem_path = os.path.join(secret_dir, "bahrhidro_private.pem")
pub_pem_path = os.path.join(secret_dir, "bahrhidro_public.pem")

if not os.path.exists(priv_pem_path):
    subprocess.run(["openssl", "genpkey", "-algorithm", "RSA", "-out", priv_pem_path, "-pkeyopt", "rsa_keygen_bits:2048"], check=True)
    subprocess.run(["openssl", "rsa", "-pubout", "-in", priv_pem_path, "-out", pub_pem_path], check=True)

with open(pub_pem_path, "r", encoding="utf-8") as f:
    public_pem = f.read().strip()

# Garante que arquivos .pem nunca sejam enviados ao GitHub
gitignore_path = ".gitignore"
gi = open(gitignore_path, "r", encoding="utf-8").read() if os.path.exists(gitignore_path) else ""
if "*.pem" not in gi:
    with open(gitignore_path, "a", encoding="utf-8") as f:
        f.write("\n# Chaves privadas de licenciamento\n*.pem\nbahrhidro_chave_mestra/\n")

# 2. Cria o gerador de chaves privado para o dono do plugin
keygen_script = f'''#!/usr/bin/env python3
import base64, subprocess, sys

PRIV_PEM = "{priv_pem_path}"

def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")

def generate_key(client_name: str, machine_id: str = "MASTER", valid_until: str = "VITALICIA") -> str:
    clean_client = client_name.replace("|", " ").strip() or "Licenciado Bahr Hidro"
    clean_hwid = machine_id.strip().upper()
    clean_exp = valid_until.strip().upper()
    payload = f"{{clean_client}}|{{clean_hwid}}|{{clean_exp}}".encode("utf-8")

    proc = subprocess.run(
        ["openssl", "dgst", "-sha256", "-sign", PRIV_PEM],
        input=payload,
        stdout=subprocess.PIPE,
        check=True
    )
    return f"BH26-{{b64url(payload)}}.{{b64url(proc.stdout)}}"

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Uso:")
        print("  Chave vinculada a um PC : python3 gerar_chave.py \\"Nome Cliente\\" \\"BHID-XXXX-XXXX-XXXX\\" \\"2027-12-31\\"")
        print("  Chave Mestra (qualquer PC): python3 gerar_chave.py \\"Bahr Engenharia\\" \\"MASTER\\" \\"VITALICIA\\"")
        sys.exit(1)

    nome = sys.argv[1]
    hwid = sys.argv[2] if len(sys.argv) >= 3 else "MASTER"
    validade = sys.argv[3] if len(sys.argv) >= 4 else "VITALICIA"
    chave = generate_key(nome, hwid, validade)
    print("\\n========================================================")
    print(f"Cliente  : {{nome}}")
    print(f"Máquina  : {{hwid}}")
    print(f"Validade : {{validade}}")
    print("--------------------------------------------------------")
    print(chave)
    print("========================================================\\n")
'''

keygen_path = os.path.join(secret_dir, "gerar_chave.py")
with open(keygen_path, "w", encoding="utf-8") as f:
    f.write(keygen_script)
os.chmod(keygen_path, 0o700)

# 3. Cria BahrHidro.UI/LicenseService.cs com a Chave Pública embutida
license_service_cs = f'''using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace BahrHidro.UI
{{
    public sealed record LicenseValidationResult(
        bool IsValid,
        string ClientName,
        string BoundMachineId,
        string ExpirationText,
        string Message);

    public static class LicenseService
    {{
        private const string PublicKeyPem = @"
{public_pem}
";

        public static string LicenseFilePath
        {{
            get
            {{
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BahrHidro");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "license.key");
            }}
        }}

        public static string GetMachineId()
        {{
            string machineGuid = "";
            try
            {{
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var subKey = baseKey.OpenSubKey(@"SOFTWARE\\Microsoft\\Cryptography");
                machineGuid = subKey?.GetValue("MachineGuid")?.ToString() ?? "";
            }}
            catch {{ }}

            string raw = $"{Environment.MachineName}|{Environment.UserDomainName}|{Environment.ProcessorCount}|{machineGuid}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            string hex = Convert.ToHexString(hash)[..12];
            return $"BHID-{{hex[..4]}}-{{hex[4..8]}}-{{hex[8..12]}}";
        }}

        public static LicenseValidationResult ValidateStoredLicense()
        {{
            try
            {{
                if (!File.Exists(LicenseFilePath))
                    return new LicenseValidationResult(false, "", "", "", "Nenhuma chave de ativação instalada neste computador.");

                string key = File.ReadAllText(LicenseFilePath, Encoding.UTF8).Trim();
                return ValidateKey(key);
            }}
            catch (Exception ex)
            {{
                return new LicenseValidationResult(false, "", "", "", $"Falha ao ler arquivo de licença: {{ex.Message}}");
            }}
        }}

        public static LicenseValidationResult ValidateKey(string? rawKey)
        {{
            if (string.IsNullOrWhiteSpace(rawKey))
                return new LicenseValidationResult(false, "", "", "", "Informe uma chave de ativação válida.");

            string key = rawKey.Trim();
            if (!key.StartsWith("BH26-", StringComparison.OrdinalIgnoreCase))
                return new LicenseValidationResult(false, "", "", "", "Formato inválido. A chave deve iniciar com 'BH26-'.");

            string body = key[5..];
            int dotIdx = body.IndexOf('.');
            if (dotIdx <= 0 || dotIdx >= body.Length - 1)
                return new LicenseValidationResult(false, "", "", "", "Chave corrompida ou incompleta.");

            byte[] payloadBytes;
            byte[] sigBytes;
            try
            {{
                payloadBytes = Base64UrlDecode(body[..dotIdx]);
                sigBytes = Base64UrlDecode(body[(dotIdx + 1)..]);
            }}
            catch
            {{
                return new LicenseValidationResult(false, "", "", "", "Codificação inválida na chave informada.");
            }}

            try
            {{
                using RSA rsa = RSA.Create();
                rsa.ImportFromPem(PublicKeyPem.AsSpan());
                bool verified = rsa.VerifyData(payloadBytes, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                if (!verified)
                    return new LicenseValidationResult(false, "", "", "", "Assinatura digital inválida. Chave não autenticada.");
            }}
            catch (Exception ex)
            {{
                return new LicenseValidationResult(false, "", "", "", $"Erro criptográfico na validação: {{ex.Message}}");
            }}

            string payload = Encoding.UTF8.GetString(payloadBytes);
            string[] parts = payload.Split('|');
            if (parts.Length < 3)
                return new LicenseValidationResult(false, "", "", "", "Estrutura interna da licença inválida.");

            string clientName = parts[0].Trim();
            string boundHwid = parts[1].Trim().ToUpperInvariant();
            string expRaw = parts[2].Trim().ToUpperInvariant();

            string currentHwid = GetMachineId();
            if (boundHwid != "MASTER" && !string.Equals(boundHwid, currentHwid, StringComparison.OrdinalIgnoreCase))
            {{
                return new LicenseValidationResult(
                    false,
                    clientName,
                    boundHwid,
                    expRaw,
                    $"Chave emitida para outra máquina ({{boundHwid}}). O ID deste PC é {{currentHwid}}.");
            }}

            string expFormatted = "Vitalícia";
            if (expRaw != "VITALICIA")
            {{
                if (!DateTime.TryParseExact(expRaw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime expDate))
                    return new LicenseValidationResult(false, clientName, boundHwid, expRaw, "Data de validade inválida na chave.");

                if (DateTime.Today > expDate.Date)
                    return new LicenseValidationResult(false, clientName, boundHwid, expDate.ToString("dd/MM/yyyy"), $"Licença expirada em {{expDate:dd/MM/yyyy}}.");

                expFormatted = expDate.ToString("dd/MM/yyyy");
            }}

            return new LicenseValidationResult(
                true,
                clientName,
                boundHwid == "MASTER" ? "Chave Mestra (Multimáquina)" : boundHwid,
                expFormatted,
                $"Licença ativa para {{clientName}} (Validade: {{expFormatted}}).");
        }}

        public static LicenseValidationResult ActivateAndSave(string rawKey)
        {{
            var check = ValidateKey(rawKey);
            if (check.IsValid)
            {{
                File.WriteAllText(LicenseFilePath, rawKey.Trim(), new UTF8Encoding(false));
            }}
            return check;
        }}

        public static bool EnsureLicensed(Window? owner = null, bool forcePrompt = false)
        {{
            if (!forcePrompt && ValidateStoredLicense().IsValid)
                return true;

            var dlg = new LicenseActivationWindow();
            if (owner != null && owner.IsLoaded)
            {{
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }}
            bool? result = dlg.ShowDialog();
            return result == true && ValidateStoredLicense().IsValid;
        }}

        private static byte[] Base64UrlDecode(string input)
        {{
            string s = input.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {{
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }}
            return Convert.FromBase64String(s);
        }}
    }}
}}
'''

with open("BahrHidro.UI/LicenseService.cs", "w", encoding="utf-8") as f:
    f.write(license_service_cs)

# 4. Cria a Janela WPF de Ativação (LicenseActivationWindow.xaml / .cs)
act_xaml = '''<Window x:Class="BahrHidro.UI.LicenseActivationWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Bahr Hidro BIM — Ativação de Segurança"
        Width="520"
        Height="390"
        ResizeMode="NoResize"
        WindowStartupLocation="CenterScreen"
        Background="#F1F5F9">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <Border Grid.Row="0" Background="#0F172A" Padding="18,14">
            <StackPanel Orientation="Horizontal">
                <Border Background="#0284C7" CornerRadius="6" Padding="9,5" Margin="0,0,12,0" VerticalAlignment="Center">
                    <TextBlock Text="BH" Foreground="#FFFFFF" FontWeight="Bold" FontSize="13"/>
                </Border>
                <StackPanel VerticalAlignment="Center">
                    <TextBlock Text="ATIVAÇÃO DE LICENÇA — BAHR HIDRO BIM" Foreground="#FFFFFF" FontWeight="Bold" FontSize="13.5"/>
                    <TextBlock Text="Proteção criptográfica vinculada ao identificador deste equipamento" Foreground="#94A3B8" FontSize="11"/>
                </StackPanel>
            </StackPanel>
        </Border>

        <StackPanel Grid.Row="1" Margin="20,16">
            <TextBlock Text="1. ID desta Máquina (envie este código para gerar a chave):" FontWeight="SemiBold" Foreground="#1E293B" Margin="0,0,0,6"/>
            <Grid Margin="0,0,0,14">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <TextBox x:Name="MachineIdTextBox" Grid.Column="0" IsReadOnly="True" Height="34" Padding="10,0" FontFamily="Consolas" FontWeight="Bold" FontSize="13" Background="#E2E8F0" Foreground="#0F172A" VerticalContentAlignment="Center"/>
                <Button Grid.Column="1" Content="Copiar ID" Width="96" Height="34" Margin="8,0,0,0" Background="#FFFFFF" BorderBrush="#CBD5E1" BorderThickness="1" FontWeight="SemiBold" Cursor="Hand" Click="CopiarMachineId_Click"/>
            </Grid>

            <TextBlock Text="2. Cole sua Chave de Segurança (BH26-...):" FontWeight="SemiBold" Foreground="#1E293B" Margin="0,0,0,6"/>
            <TextBox x:Name="LicenseKeyTextBox" Height="74" Padding="10,8" FontFamily="Consolas" FontSize="11.5" TextWrapping="Wrap" AcceptsReturn="False" VerticalScrollBarVisibility="Auto" Background="#FFFFFF" BorderBrush="#CBD5E1" BorderThickness="1"/>

            <TextBlock x:Name="ActivationStatusText" Text="Aguardando chave de ativação..." Foreground="#475569" FontSize="12" TextWrapping="Wrap" Margin="0,12,0,0"/>
        </StackPanel>

        <Border Grid.Row="2" Background="#FFFFFF" BorderBrush="#E2E8F0" BorderThickness="0,1,0,0" Padding="20,12">
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                <Button Content="Fechar" Width="95" Height="34" Margin="0,0,10,0" Background="#FFFFFF" BorderBrush="#CBD5E1" BorderThickness="1" FontWeight="SemiBold" Cursor="Hand" Click="Cancelar_Click"/>
                <Button Content="Ativar Licença" Width="135" Height="34" Background="#0284C7" Foreground="#FFFFFF" BorderThickness="0" FontWeight="SemiBold" Cursor="Hand" Click="AtivarLicenca_Click"/>
            </StackPanel>
        </Border>
    </Grid>
</Window>
'''

act_cs = '''using System.Windows;

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
'''

with open("BahrHidro.UI/LicenseActivationWindow.xaml", "w", encoding="utf-8") as f:
    f.write(act_xaml)

with open("BahrHidro.UI/LicenseActivationWindow.xaml.cs", "w", encoding="utf-8") as f:
    f.write(act_cs)

# 5. Integra verificação de licença no MainWindow.xaml.cs e card na aba Configurações
with open("BahrHidro.UI/MainWindow.xaml.cs", "r", encoding="utf-8") as f:
    mw_cs = f.read()

if "EnsureLicensed" not in mw_cs:
    mw_cs = mw_cs.replace(
        "this.Loaded += (_, _) => DiscoverNavigationShortcutButtons();",
        "this.Loaded += (_, _) => { if (!LicenseService.EnsureLicensed(this)) { this.Close(); return; } DiscoverNavigationShortcutButtons(); UpdateLicenseInfoCard(); };",
        1
    )

    lic_ui_helpers = """
    private void UpdateLicenseInfoCard()
    {
        if (LicenseInfoText == null) return;
        var st = LicenseService.ValidateStoredLicense();
        LicenseInfoText.Text = st.IsValid
            ? $"{st.Message} | ID desta Máquina: {LicenseService.GetMachineId()}"
            : $"Não licenciado | ID desta Máquina: {LicenseService.GetMachineId()}";
    }

    private void GerenciarLicenca_Click(object sender, RoutedEventArgs e)
    {
        if (LicenseService.EnsureLicensed(this, forcePrompt: true))
        {
            UpdateLicenseInfoCard();
        }
    }
"""
    is_file_scoped = bool(re.search(r'^\s*namespace\s+[\w.]+\s*;', mw_cs, re.MULTILINE))
    last_brace = mw_cs.rfind("}")
    insert_pos = last_brace if is_file_scoped else mw_cs.rfind("}", 0, last_brace)
    mw_cs = mw_cs[:insert_pos] + lic_ui_helpers + "\n" + mw_cs[insert_pos:]

    with open("BahrHidro.UI/MainWindow.xaml.cs", "w", encoding="utf-8") as f:
        f.write(mw_cs)

with open("BahrHidro.UI/MainWindow.xaml", "r", encoding="utf-8") as f:
    mw_xaml = f.read()

if 'x:Name="LicenseInfoText"' not in mw_xaml and '<TextBlock x:Name="CfgStatusText"' in mw_xaml:
    idx_cfg = mw_xaml.index('<TextBlock x:Name="CfgStatusText"')
    lic_card = '''<Border BorderBrush="#CBD5E1" BorderThickness="1" CornerRadius="6" Padding="12" Margin="0,4,0,12">
                        <StackPanel>
                            <TextBlock Text="5. Licença &amp; Chave de Segurança" FontWeight="SemiBold" Margin="0,0,0,6"/>
                            <TextBlock x:Name="LicenseInfoText" Text="Verificando licença..." FontSize="12" Opacity="0.85" TextWrapping="Wrap" Margin="0,0,0,8"/>
                            <Button Content="Gerenciar / Alterar Chave de Segurança" Style="{StaticResource SecondaryButton}" Height="32" Padding="12,0" HorizontalAlignment="Left" Click="GerenciarLicenca_Click"/>
                        </StackPanel>
                    </Border>

                    '''
    mw_xaml = mw_xaml[:idx_cfg] + lic_card + mw_xaml[idx_cfg:]
    with open("BahrHidro.UI/MainWindow.xaml", "w", encoding="utf-8") as f:
        f.write(mw_xaml)

# 6. Protege também os botões rápidos da Ribbon em RevitQuickRibbonCommands.cs
quick_path = "BahrHidro.Revit/RevitQuickRibbonCommands.cs"
if os.path.exists(quick_path):
    with open(quick_path, "r", encoding="utf-8") as f:
        q_cs = f.read()
    if "LicenseService.EnsureLicensed" not in q_cs:
        q_cs = q_cs.replace(
            "try\n            {",
            "if (!BahrHidro.UI.LicenseService.EnsureLicensed()) return Result.Cancelled;\n            try\n            {"
        )
        with open(quick_path, "w", encoding="utf-8") as f:
            f.write(q_cs)

# 7. Gera a primeira Chave Mestra para você ativar o seu computador imediatamente
res_key = subprocess.run(
    ["python3", keygen_path, "Bahr Engenharia (Mestra)", "MASTER", "VITALICIA"],
    stdout=subprocess.PIPE,
    text=True,
    check=True
)
print(res_key.stdout)
print(f"Gerador salvo com segurança fora do Git em: {keygen_path}")
