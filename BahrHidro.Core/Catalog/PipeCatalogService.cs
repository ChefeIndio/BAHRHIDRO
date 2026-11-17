
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BahrHidro.Core.Catalog
{
    public sealed class PipeCatalogService
    {
        private readonly PipeCatalogRepository _repository;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public PipeCatalogService(PipeCatalogRepository repository)
        {
            _repository = repository ??
                throw new ArgumentNullException(nameof(repository));
        }

        public async Task<List<PipeProduct>> GetAllAsync()
        {
            await _lock.WaitAsync();

            try
            {
                return await _repository.LoadAsync();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task AddAsync(PipeProduct product)
        {
            ArgumentNullException.ThrowIfNull(product);

            ValidateProduct(product);

            await _lock.WaitAsync();

            try
            {
                var products = await _repository.LoadAsync();

                if (products.Any(p => p.Id == product.Id))
                    throw new InvalidOperationException(
                        "ID ja cadastrado.");

                if (products.Any(p => IsDuplicate(p, product)))
                    throw new InvalidOperationException(
                        "Produto ja cadastrado.");

                products.Add(product);

                await _repository.SaveAsync(products);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<bool> UpdateAsync(
            Guid id, PipeProduct updatedProduct)
        {
            ArgumentNullException.ThrowIfNull(updatedProduct);

            if (id == Guid.Empty || updatedProduct.Id != id)
                throw new ArgumentException("ID invalido.");

            ValidateProduct(updatedProduct);

            await _lock.WaitAsync();

            try
            {
                var products = await _repository.LoadAsync();

                int index = products.FindIndex(p => p.Id == id);

                if (index < 0)
                    return false;

                if (products.Any(p =>
                    p.Id != id && IsDuplicate(p, updatedProduct)))
                    throw new InvalidOperationException(
                        "Produto duplicado.");

                products[index] = updatedProduct;

                await _repository.SaveAsync(products);

                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<bool> DeleteAsync(Guid id)
        {
            if (id == Guid.Empty)
                throw new ArgumentException("ID invalido.");

            await _lock.WaitAsync();

            try
            {
                var products = await _repository.LoadAsync();

                var product = products.FirstOrDefault(p => p.Id == id);

                if (product == null)
                    return false;

                products.Remove(product);

                await _repository.SaveAsync(products);

                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public Task<List<PipeProduct>> GetByMaterialAsync(
            PipeMaterial material)
        {
            return _repository.FindByMaterialAsync(material);
        }

        public Task<List<PipeProduct>> GetByManufacturerAsync(
            string manufacturer)
        {
            return _repository.FindByManufacturerAsync(manufacturer);
        }

        public Task<List<PipeProduct>> GetByMinimumDiameterAsync(
            decimal minimumDiameterMm)
        {
            return _repository.FindByMinimumDiameterAsync(
                minimumDiameterMm);
        }

        private static void ValidateProduct(PipeProduct product)
        {
            var errors = product.Validate();

            if (errors.Count > 0)
                throw new InvalidDataException(
                    string.Join(Environment.NewLine, errors));
        }

        private static bool IsDuplicate(
            PipeProduct first, PipeProduct second)
        {
            return first.Material == second.Material
                && string.Equals(first.Manufacturer.Trim(),
                    second.Manufacturer.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(first.ProductLine.Trim(),
                    second.ProductLine.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(first.NominalDesignation.Trim(),
                    second.NominalDesignation.Trim(),
                    StringComparison.OrdinalIgnoreCase);
        }
    }
}
