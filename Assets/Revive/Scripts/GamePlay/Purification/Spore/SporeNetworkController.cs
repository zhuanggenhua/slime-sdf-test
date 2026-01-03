using System;
using System.Collections.Generic;
using Revive.Slime;
using UnityEngine;

namespace Revive.GamePlay.Purification
{
    [RequireComponent(typeof(PurificationSystem))]
    public class SporeNetworkController : MonoBehaviour
    {
        [ChineseHeader("孢子网络")]
        [ChineseLabel("孢子类型")]
        [DefaultValue("Spore")]
        [SerializeField] private string sporeIndicatorType = "Spore";

        [ChineseLabel("路径类型")]
        [DefaultValue("SporeTrail")]
        [SerializeField] private string trailIndicatorType = "SporeTrail";

        [ChineseLabel("连线范围(米)")]
        [DefaultValue(20f)]
        [SerializeField, Min(0.01f)] private float linkRadius = 20f;

        [ChineseLabel("路径点间距(米)")]
        [DefaultValue(3f)]
        [SerializeField, Min(0.01f)] private float trailSpacing = 3f;

        [ChineseLabel("路径点贡献值")]
        [DefaultValue(1f)]
        [SerializeField] private float trailContributionValue = 1f;

        [ChineseLabel("路径点辐射范围(米)")]
        [DefaultValue(8f)]
        [SerializeField, Min(0.01f)] private float trailRadiationRadius = 8f;

        private PurificationSystem _system;

        private readonly Dictionary<string, PurificationIndicator> _sporeNodesByName = new(StringComparer.Ordinal);
        private readonly HashSet<EdgeKey> _edges = new();
        private readonly Dictionary<EdgeKey, List<PurificationIndicator>> _edgeTrailIndicators = new();

        private void Awake()
        {
            _system = GetComponent<PurificationSystem>();
        }

        private void OnEnable()
        {
            if (_system == null)
                _system = GetComponent<PurificationSystem>();

            if (_system == null)
                return;

            _system.IndicatorAdded += OnIndicatorAdded;
            _system.IndicatorRemoved += OnIndicatorRemoved;

            Rebuild();
        }

        private void OnDisable()
        {
            if (_system != null)
            {
                _system.IndicatorAdded -= OnIndicatorAdded;
                _system.IndicatorRemoved -= OnIndicatorRemoved;
            }

            ClearTrailsAndEdges();
            _sporeNodesByName.Clear();
        }

        private void OnIndicatorAdded(PurificationIndicator indicator)
        {
            if (indicator == null)
                return;

            if (!string.Equals(indicator.IndicatorType, sporeIndicatorType, StringComparison.Ordinal))
                return;

            if (string.IsNullOrEmpty(indicator.Name))
                return;

            _sporeNodesByName[indicator.Name] = indicator;

            foreach (var kv in _sporeNodesByName)
            {
                PurificationIndicator other = kv.Value;
                if (other == null || ReferenceEquals(other, indicator))
                    continue;

                float d = Vector3.Distance(indicator.Position, other.Position);
                if (d > linkRadius)
                    continue;

                EdgeKey key = new EdgeKey(indicator.Name, other.Name);
                if (_edges.Contains(key))
                    continue;

                _edges.Add(key);
                _edgeTrailIndicators[key] = CreateTrailIndicators(key, indicator.Position, other.Position);
            }
        }

        private void OnIndicatorRemoved(PurificationIndicator indicator)
        {
            if (indicator == null)
                return;

            if (!string.Equals(indicator.IndicatorType, sporeIndicatorType, StringComparison.Ordinal))
                return;

            if (string.IsNullOrEmpty(indicator.Name))
                return;

            _sporeNodesByName.Remove(indicator.Name);

            List<EdgeKey> edgesToRemove = null;
            foreach (var edge in _edges)
            {
                if (edge.Contains(indicator.Name))
                {
                    edgesToRemove ??= new List<EdgeKey>();
                    edgesToRemove.Add(edge);
                }
            }

            if (edgesToRemove == null)
                return;

            foreach (var edge in edgesToRemove)
            {
                RemoveTrailIndicators(edge);
                _edgeTrailIndicators.Remove(edge);
                _edges.Remove(edge);
            }
        }

        private void Rebuild()
        {
            if (_system == null)
                return;

            ClearTrailsAndEdges();
            _sporeNodesByName.Clear();

            IReadOnlyList<PurificationIndicator> all = _system.GetAllIndicators();
            for (int i = 0; i < all.Count; i++)
            {
                PurificationIndicator indicator = all[i];
                if (indicator == null)
                    continue;
                if (!string.Equals(indicator.IndicatorType, sporeIndicatorType, StringComparison.Ordinal))
                    continue;
                if (string.IsNullOrEmpty(indicator.Name))
                    continue;

                _sporeNodesByName[indicator.Name] = indicator;
            }

            var nodes = new List<PurificationIndicator>(_sporeNodesByName.Values);
            for (int i = 0; i < nodes.Count; i++)
            {
                for (int j = i + 1; j < nodes.Count; j++)
                {
                    PurificationIndicator a = nodes[i];
                    PurificationIndicator b = nodes[j];
                    if (a == null || b == null)
                        continue;

                    float d = Vector3.Distance(a.Position, b.Position);
                    if (d > linkRadius)
                        continue;

                    EdgeKey key = new EdgeKey(a.Name, b.Name);
                    if (_edges.Contains(key))
                        continue;

                    _edges.Add(key);
                    _edgeTrailIndicators[key] = CreateTrailIndicators(key, a.Position, b.Position);
                }
            }
        }

        private List<PurificationIndicator> CreateTrailIndicators(EdgeKey key, Vector3 a, Vector3 b)
        {
            List<PurificationIndicator> list = new List<PurificationIndicator>();

            float d = Vector3.Distance(a, b);
            if (trailSpacing <= 0f)
                return list;

            int count = Mathf.FloorToInt(d / trailSpacing) - 1;
            if (count <= 0)
                return list;

            for (int i = 1; i <= count; i++)
            {
                float t = (float)i / (count + 1);
                Vector3 pos = Vector3.Lerp(a, b, t);
                string name = $"{trailIndicatorType}_{key.A}_{key.B}_{i}";
                PurificationIndicator indicator = _system.AddIndicator(name, pos, trailContributionValue, trailIndicatorType, trailRadiationRadius);
                list.Add(indicator);
            }

            return list;
        }

        private void RemoveTrailIndicators(EdgeKey key)
        {
            if (_system == null)
                return;

            if (!_edgeTrailIndicators.TryGetValue(key, out List<PurificationIndicator> list) || list == null)
                return;

            for (int i = 0; i < list.Count; i++)
            {
                PurificationIndicator indicator = list[i];
                if (indicator == null)
                    continue;
                _system.RemoveIndicator(indicator);
            }
        }

        private void ClearTrailsAndEdges()
        {
            foreach (var kv in _edgeTrailIndicators)
            {
                RemoveTrailIndicators(kv.Key);
            }

            _edgeTrailIndicators.Clear();
            _edges.Clear();
        }

        private readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            public readonly string A;
            public readonly string B;

            public EdgeKey(string a, string b)
            {
                if (string.CompareOrdinal(a, b) <= 0)
                {
                    A = a;
                    B = b;
                }
                else
                {
                    A = b;
                    B = a;
                }
            }

            public bool Contains(string name)
            {
                return string.Equals(A, name, StringComparison.Ordinal) || string.Equals(B, name, StringComparison.Ordinal);
            }

            public bool Equals(EdgeKey other)
            {
                return string.Equals(A, other.A, StringComparison.Ordinal) && string.Equals(B, other.B, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is EdgeKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(A, B);
            }
        }
    }
}
