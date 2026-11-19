using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace BahrHidro.UI
{
    public sealed record LicenseValidationResult(
        bool IsValid,
        string ClientName,
        string BoundMachineId,
        string ExpirationText,
        string Message);

    public static class LicenseService
    {
        private const string PublicKeyPem = @"
-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAx34HytJ4OVNdngmlv4oF
bXTGQNa3V5+Aa8tYoUzgBl3/i1qgL2HZuxdwewiSjBcm/3YHTi+qcN34cxqe8yAJ
ErguEWaeb/lZaI+HpmU+qEyG2/0pwqu7FVfH3mESkMEHZnXAQ8vtwgzWPSWTI0mm
3gR5SemKvX8sgx2/CVzcrgcdMm6v4SPy9Q36rmENMG//f/T+mEO8aP2aSqO2JNbL
HcLtOoTtOyUaKhMadtW6HTkvAEABwwwyabh/jyamC8p0T5Sz8tYqzV456X0l6z+9
0zvJuXrArsiyXofcNPdLPmZzlTKpiSeh3x7Yg/T617IqiHR+DqXJFWdwsLmsOUDm
HwIDAQAB
-----END PUBLIC KEY-----
";

        public static string LicenseFilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BahrHidro");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "license.key");
            }
        }

        public static string GetMachineId()
        {
            string machineGuid = "";
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var subKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                machineGuid = subKey?.GetValue("MachineGuid")?.ToString() ?? "";
            }
            catch { }

            string raw = $"{Environment.MachineName}|{Environment.UserDomainName}|{Environment.ProcessorCount}|{machineGuid}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            string hex = Convert.ToHexString(hash)[..12];
            return $"BHID-{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
        }

        public static LicenseValidationResult ValidateStoredLicense()
        {
            try
            {
                if (!File.Exists(LicenseFilePath))
                    return new LicenseValidationResult(false, "", "", "", "Nenhuma chave de ativação instalada neste computador.");

                string key = File.ReadAllText(LicenseFilePath, Encoding.UTF8).Trim();
                return ValidateKey(key);
            }
            catch (Exception ex)
            {
                return new LicenseValidationResult(false, "", "", "", $"Falha ao ler arquivo de licença: {ex.Message}");
            }
        }

        public static LicenseValidationResult ValidateKey(string? rawKey)
        {
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
            {
                payloadBytes = Base64UrlDecode(body[..dotIdx]);
                sigBytes = Base64UrlDecode(body[(dotIdx + 1)..]);
            }
            catch
            {
                return new LicenseValidationResult(false, "", "", "", "Codificação inválida na chave informada.");
            }

            try
            {
                using RSA rsa = RSA.Create();
                rsa.ImportFromPem(PublicKeyPem.AsSpan());
                bool verified = rsa.VerifyData(payloadBytes, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                if (!verified)
                    return new LicenseValidationResult(false, "", "", "", "Assinatura digital inválida. Chave não autenticada.");
            }
            catch (Exception ex)
            {
                return new LicenseValidationResult(false, "", "", "", $"Erro criptográfico na validação: {ex.Message}");
            }

            string payload = Encoding.UTF8.GetString(payloadBytes);
            string[] parts = payload.Split('|');
            if (parts.Length < 3)
                return new LicenseValidationResult(false, "", "", "", "Estrutura interna da licença inválida.");

            string clientName = parts[0].Trim();
            string boundHwid = parts[1].Trim().ToUpperInvariant();
            string expRaw = parts[2].Trim().ToUpperInvariant();

            string currentHwid = GetMachineId();
            if (boundHwid != "MASTER" && !string.Equals(boundHwid, currentHwid, StringComparison.OrdinalIgnoreCase))
            {
                return new LicenseValidationResult(
                    false,
                    clientName,
                    boundHwid,
                    expRaw,
                    $"Chave emitida para outra máquina ({boundHwid}). O ID deste PC é {currentHwid}.");
            }

            string expFormatted = "Vitalícia";
            if (expRaw != "VITALICIA")
            {
                if (!DateTime.TryParseExact(expRaw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime expDate))
                    return new LicenseValidationResult(false, clientName, boundHwid, expRaw, "Data de validade inválida na chave.");

                if (DateTime.Today > expDate.Date)
                    return new LicenseValidationResult(false, clientName, boundHwid, expDate.ToString("dd/MM/yyyy"), $"Licença expirada em {expDate:dd/MM/yyyy}.");

                expFormatted = expDate.ToString("dd/MM/yyyy");
            }

            return new LicenseValidationResult(
                true,
                clientName,
                boundHwid == "MASTER" ? "Chave Mestra (Multimáquina)" : boundHwid,
                expFormatted,
                $"Licença ativa para {clientName} (Validade: {expFormatted}).");
        }

        public static LicenseValidationResult ActivateAndSave(string rawKey)
        {
            var check = ValidateKey(rawKey);
            if (check.IsValid)
            {
                File.WriteAllText(LicenseFilePath, rawKey.Trim(), new UTF8Encoding(false));
            }
            return check;
        }

        public static bool EnsureLicensed(Window? owner = null, bool forcePrompt = false)
        {
            if (!forcePrompt && ValidateStoredLicense().IsValid)
                return true;

            var dlg = new LicenseActivationWindow();
            if (owner != null && owner.IsLoaded)
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            bool? result = dlg.ShowDialog();
            return result == true && ValidateStoredLicense().IsValid;
        }

        private static byte[] Base64UrlDecode(string input)
        {
            string s = input.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }
    }
}
