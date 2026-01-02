using System.Collections.Generic;
using Revive.Slime;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Revive.Environment
{
    public static class WindFieldRegistry
    {
        private static readonly List<WindFieldZone> Zones = new List<WindFieldZone>(16);

        public static IReadOnlyList<WindFieldZone> ActiveZones => Zones;

        public static void Register(WindFieldZone zone)
        {
            if (zone == null)
                return;

            if (!Zones.Contains(zone))
            {
                Zones.Add(zone);
            }
        }

        public static void Unregister(WindFieldZone zone)
        {
            if (zone == null)
                return;

            Zones.Remove(zone);
        }

        public static void GetCombinedAtWorldPosition(
            Vector3 worldPos,
            int layer,
            out float groundDrag,
            out float airDrag,
            out Vector3 pushVector
        )
        {
            groundDrag = 0f;
            airDrag = 0f;
            pushVector = Vector3.zero;

            for (int i = Zones.Count - 1; i >= 0; i--)
            {
                var z = Zones[i];
                if (z == null)
                {
                    Zones.RemoveAt(i);
                    continue;
                }

                if (!z.isActiveAndEnabled)
                    continue;

                if (!z.AffectsLayer(layer))
                    continue;

                if (!z.ContainsWorldPoint(worldPos))
                    continue;

                groundDrag += Mathf.Max(0f, z.GroundDrag);
                airDrag += Mathf.Max(0f, z.AirDrag);

                float push = Mathf.Max(0f, z.PushStrength);
                if (push > 0f)
                {
                    pushVector += z.GetDirectionWorld() * push;
                }
            }
        }

        public static void GetCombinedAtWorldPosition(
            Vector3 worldPos,
            GameObject target,
            out float groundDrag,
            out float airDrag,
            out Vector3 pushVector
        )
        {
            int layer = target != null ? target.layer : 0;
            GetCombinedAtWorldPosition(worldPos, layer, out groundDrag, out airDrag, out pushVector);
        }

        public static void GetCombinedForCarryable(
            Vector3 worldPos,
            SlimeCarryableObject carryable,
            out float groundDrag,
            out float airDrag,
            out Vector3 pushVector
        )
        {
            groundDrag = 0f;
            airDrag = 0f;
            pushVector = Vector3.zero;

            if (carryable == null)
                return;

            if (carryable.Type == SlimeCarryableObject.CarryableType.Stone)
                return;

            GetCombinedAtWorldPosition(worldPos, carryable.gameObject, out groundDrag, out airDrag, out pushVector);
        }

        public static void FillActiveZonesSimData(
            NativeList<WindFieldZoneData> outZones,
            float worldToSimScale
        )
        {
            for (int i = Zones.Count - 1; i >= 0; i--)
            {
                var z = Zones[i];
                if (z == null)
                {
                    Zones.RemoveAt(i);
                    continue;
                }

                if (!z.isActiveAndEnabled)
                    continue;

                var bounds = z.WorldBounds;

                WindFieldZoneData data;
                data.CenterSim = (float3)bounds.center * worldToSimScale;
                data.ExtentsSim = (float3)bounds.extents * worldToSimScale;
                data.GroundDrag = Mathf.Max(0f, z.GroundDrag);
                data.AirDrag = Mathf.Max(0f, z.AirDrag);
                data.PushSim = (float3)(z.GetDirectionWorld() * Mathf.Max(0f, z.PushStrength)) * worldToSimScale;
                data.AffectsLayerMask = z.AffectsLayers.value;

                outZones.Add(data);
            }
        }
    }
}
