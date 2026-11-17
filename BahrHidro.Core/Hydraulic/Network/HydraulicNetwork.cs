
using System;
using System.Collections.Generic;
using System.Linq;

namespace BahrHidro.Core.Hydraulic.Network
{
    public sealed class HydraulicNetwork
    {
        private readonly List<HydraulicNode> _nodes = new();
        private readonly List<HydraulicPipeSegment> _segments = new();

        public string Name { get; }

        public IReadOnlyList<HydraulicNode> Nodes => _nodes.AsReadOnly();

        public IReadOnlyList<HydraulicPipeSegment> Segments =>
            _segments.AsReadOnly();

        public HydraulicNetwork(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException(
                    "O nome da rede não foi informado.");

            Name = name.Trim();
        }

        /// <summary>
        /// Adiciona um nó à rede.
        /// </summary>
        public void AddNode(HydraulicNode node)
        {
            ArgumentNullException.ThrowIfNull(node);

            node.Validate();

            if (_nodes.Any(n => n.Id == node.Id))
                throw new InvalidOperationException(
                    "Este nó já existe na rede.");

            _nodes.Add(node);
        }

        /// <summary>
        /// Adiciona um trecho entre dois nós existentes.
        /// </summary>
        public void AddSegment(HydraulicPipeSegment segment)
        {
            ArgumentNullException.ThrowIfNull(segment);

            segment.Validate();

            if (_segments.Any(s => s.Id == segment.Id))
                throw new InvalidOperationException(
                    "Este trecho já existe na rede.");

            bool startNodeExists =
                _nodes.Any(n => n.Id == segment.StartNodeId);

            bool endNodeExists =
                _nodes.Any(n => n.Id == segment.EndNodeId);

            if (!startNodeExists || !endNodeExists)
                throw new InvalidOperationException(
                    "Os nós inicial e final precisam existir na rede.");

            _segments.Add(segment);
        }

        /// <summary>
        /// Localiza um nó pelo identificador.
        /// </summary>
        public HydraulicNode? FindNode(Guid nodeId)
        {
            return _nodes.FirstOrDefault(n => n.Id == nodeId);
        }

        /// <summary>
        /// Localiza um trecho pelo identificador.
        /// </summary>
        public HydraulicPipeSegment? FindSegment(Guid segmentId)
        {
            return _segments.FirstOrDefault(s => s.Id == segmentId);
        }

        /// <summary>
        /// Remove um trecho da rede.
        /// </summary>
        public bool RemoveSegment(Guid segmentId)
        {
            var segment = FindSegment(segmentId);

            if (segment == null)
                return false;

            return _segments.Remove(segment);
        }

        /// <summary>
        /// Remove um nó somente se não estiver conectado
        /// a nenhum trecho da rede.
        /// </summary>
        public bool RemoveNode(Guid nodeId)
        {
            bool connected = _segments.Any(s =>
                s.StartNodeId == nodeId ||
                s.EndNodeId == nodeId);

            if (connected)
                throw new InvalidOperationException(
                    "Não é possível remover um nó conectado a um trecho.");

            var node = FindNode(nodeId);

            if (node == null)
                return false;

            return _nodes.Remove(node);
        }
    }
}
