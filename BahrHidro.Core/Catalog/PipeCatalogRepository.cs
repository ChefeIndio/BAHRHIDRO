
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace BahrHidro.Core.Catalog
{
    public sealed class PipeCatalogRepository
    {
        private readonly string _filePath;

        private static readonly JsonSerializerOptions JsonOptions =
            new()
            {
                WriteIndented = true,
                Converters =
                {
                    new JsonStringEnumConverter()
                }
            };

        public PipeCatalogRepository(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException(
                    "Caminho do catalogo nao informado.");

            _filePath = Path.GetFullPath(filePath);
        }

        // Carrega o catalogo armazenado.
        public async Task<List<PipeProduct>> LoadAsync()
        {
            if (!File.Exists(_filePath))
                return new List<PipeProduct>();

            await using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                useAsync: true);

            var products =
                await JsonSerializer.DeserializeAsync<List<PipeProduct>>(
                    stream,
                    JsonOptions);

            return products ?? new List<PipeProduct>();
        }

        // Salva o catalogo completo.
        public async Task SaveAsync(
            IEnumerable<PipeProduct> products)
        {
            ArgumentNullException.ThrowIfNull(products);

            var list = products.ToList();

            foreach (var product in list)
            {
                var errors = product.Validate();

                if (errors.Count > 0)
                {
                    throw new InvalidDataException(
                        string.Join(Environment.NewLine, errors));
                }
            }

            string? directory = Path.GetDirectoryName(_filePath);

            if (directory != null)
                Directory.CreateDirectory(directory);

            string temporaryPath = _filePath + ".tmp";

            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        list,
                        JsonOptions);

                    await stream.FlushAsync();
                }

                File.Move(
                    temporaryPath,
                    _filePath,
                    overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        // Pesquisa tubos por material.
        public async Task<List<PipeProduct>> FindByMaterialAsync(
            PipeMaterial material)
        {
            var products = await LoadAsync();

            return products
                .Where(p => p.Material == material)
                .ToList();
        }

        // Pesquisa tubos por fabricante.
        public async Task<List<PipeProduct>> FindByManufacturerAsync(
            string manufacturer)
        {
            if (string.IsNullOrWhiteSpace(manufacturer))
                throw new ArgumentException(
                    "Fabricante nao informado.");

            var products = await LoadAsync();

            return products
                .Where(p =>
                    string.Equals(
                        p.Manufacturer,
                        manufacturer,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Pesquisa tubos por diametro interno minimo.
        public async Task<List<PipeProduct>> FindByMinimumDiameterAsync(
            decimal minimumDiameterMm)
        {
            if (minimumDiameterMm <= 0)
                throw new ArgumentException(
                    "Diametro minimo invalido.");

            var products = await LoadAsync();

            return products
                .Where(p =>
                    p.InternalDiameterMm >= minimumDiameterMm)
                .OrderBy(p => p.InternalDiameterMm)
                .ToList();
        }
    }
}