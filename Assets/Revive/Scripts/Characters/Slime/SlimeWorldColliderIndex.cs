using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Revive.Slime
{
    [DefaultExecutionOrder(-1000)]
    public sealed class SlimeWorldColliderIndex : MonoBehaviour
    {
        public static SlimeWorldColliderIndex Instance { get; private set; }

        [SerializeField]
        private LayerMask staticColliderLayers;

        [SerializeField]
        private LayerMask dynamicColliderLayers;

        [SerializeField]
        private bool includeTriggers;

        private const float BucketSizeWorld = 5f;
        private float _invBucketSizeWorld;

        private const int OversizedStaticMaxBuckets = 512;

        private readonly Dictionary<long, List<int>> _buckets = new Dictionary<long, List<int>>(2048);
        private readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>(4096);
        private readonly Dictionary<int, DynamicState> _dynamicStates = new Dictionary<int, DynamicState>(256);

        private readonly List<int> _oversizedStaticIds = new List<int>(32);

        private readonly List<int> _tmpToRemove = new List<int>(64);
        private readonly List<int> _tmpEntryKeys = new List<int>(4096);

        private struct Entry
        {
            public Collider Collider;
            public Bounds Bounds;
            public int Type;
            public float Friction;
            public bool IsDynamic;
            public Vector3 PrevCenterWorld;
            public Vector3 VelocityWorld;
            public bool InBuckets;
            public bool IsOversized;
            public int MinX;
            public int MaxX;
            public int MinZ;
            public int MaxZ;
        }

        private struct DynamicState
        {
            public Collider Collider;
        }

        public static bool TryGetInstance(out SlimeWorldColliderIndex index)
        {
            index = Instance;
            return index != null;
        }

        public static SlimeWorldColliderIndex GetOrCreate()
        {
            if (Instance != null)
                return Instance;

            var existing = FindFirstObjectByType<SlimeWorldColliderIndex>(FindObjectsInactive.Exclude);
            if (existing != null)
            {
                Instance = existing;
                return existing;
            }

            var go = new GameObject(nameof(SlimeWorldColliderIndex));
            var created = go.AddComponent<SlimeWorldColliderIndex>();
            Instance = created;
            return created;
        }

        public void Configure(LayerMask staticLayers, LayerMask dynamicLayers, bool includeTriggerColliders)
        {
            staticColliderLayers = staticLayers;
            dynamicColliderLayers = dynamicLayers;
            includeTriggers = includeTriggerColliders;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            _invBucketSizeWorld = 1f / math.max(0.0001f, BucketSizeWorld);
        }

        private void OnEnable()
        {
            _invBucketSizeWorld = 1f / math.max(0.0001f, BucketSizeWorld);
        }

        public void Rebuild()
        {
            _buckets.Clear();
            _entries.Clear();
            _dynamicStates.Clear();
            _oversizedStaticIds.Clear();

            var colliders = FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < colliders.Length; i++)
            {
                var col = colliders[i];
                if (col == null)
                    continue;
                if (!includeTriggers && col.isTrigger)
                    continue;

                int layerBit = 1 << col.gameObject.layer;
                bool isDynamic = (dynamicColliderLayers.value & layerBit) != 0;
                bool isStatic = (staticColliderLayers.value & layerBit) != 0;
                if (!isDynamic && !isStatic)
                    continue;

                RegisterInternal(col, isDynamic);
            }

            UpdateDynamicBuckets();
        }

        private void FixedUpdate()
        {
            UpdateDynamicBuckets();
        }

        public void RegisterDynamicColliders(Collider[] colliders)
        {
            if (colliders == null)
                return;

            for (int i = 0; i < colliders.Length; i++)
            {
                var col = colliders[i];
                if (col == null)
                    continue;
                if (!includeTriggers && col.isTrigger)
                    continue;

                int layerBit = 1 << col.gameObject.layer;
                if (dynamicColliderLayers.value != 0 && (dynamicColliderLayers.value & layerBit) == 0)
                    continue;

                RegisterInternal(col, isDynamic: true);
            }
        }

        public void UnregisterDynamicColliders(Collider[] colliders)
        {
            if (colliders == null)
                return;

            for (int i = 0; i < colliders.Length; i++)
            {
                var col = colliders[i];
                if (col == null)
                    continue;
                UnregisterInternal(col);
            }
        }

        private void RegisterInternal(Collider col, bool isDynamic)
        {
            int id = col.GetInstanceID();

            var info = col.GetComponent<SlimeColliderInfo>();
            int type = info != null ? (int)info.colliderType : ColliderTypes.Ground;
            float friction = info != null ? info.surfaceFriction : 0.3f;

            var entry = new Entry
            {
                Collider = col,
                Bounds = col.bounds,
                Type = type,
                Friction = friction,
                IsDynamic = isDynamic,
                PrevCenterWorld = col.bounds.center,
                VelocityWorld = Vector3.zero,
                InBuckets = false,
                IsOversized = false,
                MinX = 0,
                MaxX = 0,
                MinZ = 0,
                MaxZ = 0,
            };

            _entries[id] = entry;
            if (isDynamic)
            {
                _dynamicStates[id] = new DynamicState { Collider = col };
            }
        }

        private void UnregisterInternal(Collider col)
        {
            int id = col.GetInstanceID();
            if (!_entries.TryGetValue(id, out var entry))
                return;

            if (entry.IsOversized)
            {
                _oversizedStaticIds.Remove(id);
            }
            else if (entry.InBuckets)
            {
                RemoveFromBuckets(id, entry.MinX, entry.MaxX, entry.MinZ, entry.MaxZ);
            }

            _entries.Remove(id);
            _dynamicStates.Remove(id);
        }

        private void UpdateDynamicBuckets()
        {
            _tmpToRemove.Clear();

            if (_dynamicStates.Count > 0)
            {
                foreach (var kv in _dynamicStates)
                {
                    int id = kv.Key;
                    var col = kv.Value.Collider;
                    if (col == null)
                    {
                        _tmpToRemove.Add(id);
                        continue;
                    }

                    if (!_entries.TryGetValue(id, out var entry))
                    {
                        _tmpToRemove.Add(id);
                        continue;
                    }

                    if (!includeTriggers && col.isTrigger)
                    {
                        if (entry.InBuckets)
                        {
                            RemoveFromBuckets(id, entry.MinX, entry.MaxX, entry.MinZ, entry.MaxZ);
                            entry.InBuckets = false;
                            _entries[id] = entry;
                        }
                        continue;
                    }

                    if (!col.enabled || !col.gameObject.activeInHierarchy)
                    {
                        if (entry.InBuckets)
                        {
                            RemoveFromBuckets(id, entry.MinX, entry.MaxX, entry.MinZ, entry.MaxZ);
                            entry.InBuckets = false;
                            _entries[id] = entry;
                        }
                        continue;
                    }

                    Bounds b = col.bounds;

                    Vector3 velocityWorld = Vector3.zero;
                    var rb = col.attachedRigidbody;
                    if (rb != null && !rb.isKinematic)
                    {
                        velocityWorld = rb.linearVelocity;
                    }
                    else
                    {
                        float dt = Time.fixedDeltaTime;
                        float invDt = 1f / Mathf.Max(0.0001f, dt);
                        velocityWorld = (b.center - entry.PrevCenterWorld) * invDt;
                    }

                    int minX = WorldToBucket(b.min.x);
                    int maxX = WorldToBucket(b.max.x);
                    int minZ = WorldToBucket(b.min.z);
                    int maxZ = WorldToBucket(b.max.z);

                    bool changed = !entry.InBuckets || entry.MinX != minX || entry.MaxX != maxX || entry.MinZ != minZ || entry.MaxZ != maxZ;
                    entry.Bounds = b;
                    entry.VelocityWorld = velocityWorld;
                    entry.PrevCenterWorld = b.center;

                    if (changed)
                    {
                        if (entry.InBuckets)
                            RemoveFromBuckets(id, entry.MinX, entry.MaxX, entry.MinZ, entry.MaxZ);

                        AddToBuckets(id, minX, maxX, minZ, maxZ);
                        entry.InBuckets = true;
                        entry.MinX = minX;
                        entry.MaxX = maxX;
                        entry.MinZ = minZ;
                        entry.MaxZ = maxZ;
                    }

                    _entries[id] = entry;
                }
            }

            for (int i = 0; i < _tmpToRemove.Count; i++)
            {
                int id = _tmpToRemove[i];
                if (_entries.TryGetValue(id, out var entry) && entry.InBuckets)
                {
                    RemoveFromBuckets(id, entry.MinX, entry.MaxX, entry.MinZ, entry.MaxZ);
                }
                _entries.Remove(id);
                _dynamicStates.Remove(id);
            }

            _tmpEntryKeys.Clear();
            foreach (var key in _entries.Keys)
            {
                _tmpEntryKeys.Add(key);
            }

            for (int i = 0; i < _tmpEntryKeys.Count; i++)
            {
                int id = _tmpEntryKeys[i];
                if (!_entries.TryGetValue(id, out var entry))
                    continue;
                if (entry.IsDynamic)
                    continue;

                var col = entry.Collider;
                if (col == null)
                {
                    if (entry.IsOversized)
                    {
                        _oversizedStaticIds.Remove(id);
                    }
                    else if (entry.InBuckets)
                    {
                        RemoveFromBuckets(id, entry.MinX, entry.MaxX, entry.MinZ, entry.MaxZ);
                    }
                    _entries.Remove(id);
                    continue;
                }

                if (!includeTriggers && col.isTrigger)
                    continue;

                if (!col.enabled || !col.gameObject.activeInHierarchy)
                {
                    if (entry.IsOversized)
                    {
                        _oversizedStaticIds.Remove(id);
                        entry.IsOversized = false;
                    }
                    else if (entry.InBuckets)
                    {
                        RemoveFromBuckets(id, entry.MinX, entry.MaxX, entry.MinZ, entry.MaxZ);
                    }
                    entry.InBuckets = false;
                    _entries[id] = entry;
                    continue;
                }

                if (entry.InBuckets)
                    continue;

                Bounds b = col.bounds;
                int minX = WorldToBucket(b.min.x);
                int maxX = WorldToBucket(b.max.x);
                int minZ = WorldToBucket(b.min.z);
                int maxZ = WorldToBucket(b.max.z);

                AddToBuckets(id, minX, maxX, minZ, maxZ);
                if (_entries.TryGetValue(id, out var updatedEntry))
                {
                    updatedEntry.Bounds = b;
                    updatedEntry.InBuckets = true;
                    updatedEntry.MinX = minX;
                    updatedEntry.MaxX = maxX;
                    updatedEntry.MinZ = minZ;
                    updatedEntry.MaxZ = maxZ;
                    _entries[id] = updatedEntry;
                }
            }
        }

        private static MyBoxCollider BuildMyBoxCollider(Collider col, in Entry entry, in Bounds b)
        {
            Vector3 lossy = col.transform != null ? col.transform.lossyScale : Vector3.one;
            float sx = Mathf.Abs(lossy.x);
            float sy = Mathf.Abs(lossy.y);
            float sz = Mathf.Abs(lossy.z);

            float3 margin = new float3(1f, 1f, 1f);

            int shape = ColliderShapes.Obb;
            quaternion rot = quaternion.identity;
            float3 centerSim = (float3)(b.center * PBF_Utils.InvScale);
            float3 extentSim = (float3)(b.extents * PBF_Utils.InvScale) + margin;

            if (col is BoxCollider boxCol)
            {
                rot = (quaternion)boxCol.transform.rotation;
                Vector3 worldCenter = boxCol.transform.TransformPoint(boxCol.center);
                Vector3 halfSizeWorld = Vector3.Scale(boxCol.size * 0.5f, boxCol.transform.lossyScale);
                centerSim = (float3)(worldCenter * PBF_Utils.InvScale);

                float3 extentNoMargin = (float3)(new Vector3(Mathf.Abs(halfSizeWorld.x), Mathf.Abs(halfSizeWorld.y), Mathf.Abs(halfSizeWorld.z)) * PBF_Utils.InvScale);
                extentSim = extentNoMargin + margin;

                shape = ColliderShapes.Obb;
            }
            else if (col is MeshCollider meshCol && meshCol.sharedMesh != null)
            {
                rot = (quaternion)meshCol.transform.rotation;
                Bounds localBounds = meshCol.sharedMesh.bounds;

                Vector3 worldCenter = meshCol.transform.TransformPoint(localBounds.center);
                Vector3 extentWorld = Vector3.Scale(localBounds.extents, new Vector3(sx, sy, sz));

                centerSim = (float3)(worldCenter * PBF_Utils.InvScale);
                float3 extentNoMargin = (float3)(new Vector3(Mathf.Abs(extentWorld.x), Mathf.Abs(extentWorld.y), Mathf.Abs(extentWorld.z)) * PBF_Utils.InvScale);
                extentSim = extentNoMargin + margin;
                shape = ColliderShapes.Obb;
            }
            else if (col is CapsuleCollider capsuleCol)
            {
                rot = (quaternion)capsuleCol.transform.rotation;
                Vector3 worldCenter = capsuleCol.transform.TransformPoint(capsuleCol.center);
                centerSim = (float3)(worldCenter * PBF_Utils.InvScale);

                float radiusWorld;
                float halfHeightWorld = capsuleCol.height * 0.5f;
                Vector3 extentWorld;
                switch (capsuleCol.direction)
                {
                    case 0:
                        radiusWorld = capsuleCol.radius * Mathf.Max(sy, sz);
                        halfHeightWorld *= sx;
                        extentWorld = new Vector3(halfHeightWorld, radiusWorld, radiusWorld);
                        break;
                    case 2:
                        radiusWorld = capsuleCol.radius * Mathf.Max(sx, sy);
                        halfHeightWorld *= sz;
                        extentWorld = new Vector3(radiusWorld, radiusWorld, halfHeightWorld);
                        break;
                    default:
                        radiusWorld = capsuleCol.radius * Mathf.Max(sx, sz);
                        halfHeightWorld *= sy;
                        extentWorld = new Vector3(radiusWorld, halfHeightWorld, radiusWorld);
                        break;
                }

                float3 extentNoMargin = (float3)(new Vector3(Mathf.Abs(extentWorld.x), Mathf.Abs(extentWorld.y), Mathf.Abs(extentWorld.z)) * PBF_Utils.InvScale);
                extentSim = extentNoMargin + margin;
                shape = ColliderShapes.Capsule;
            }
            else if (col is SphereCollider sphereCol)
            {
                rot = (quaternion)sphereCol.transform.rotation;
                Vector3 worldCenter = sphereCol.transform.TransformPoint(sphereCol.center);
                centerSim = (float3)(worldCenter * PBF_Utils.InvScale);

                float rWorld = sphereCol.radius * Mathf.Max(sx, Mathf.Max(sy, sz));
                float3 extentNoMargin = (float3)(new Vector3(rWorld, rWorld, rWorld) * PBF_Utils.InvScale);
                extentSim = extentNoMargin + margin;
                shape = ColliderShapes.Capsule;
            }
            else if (col is CharacterController cc)
            {
                rot = (quaternion)cc.transform.rotation;
                Vector3 worldCenter = cc.transform.TransformPoint(cc.center);
                centerSim = (float3)(worldCenter * PBF_Utils.InvScale);

                float radiusWorld = cc.radius * Mathf.Max(sx, sz);
                float halfHeightWorld = (cc.height * 0.5f) * sy;
                float3 extentNoMargin = (float3)(new Vector3(radiusWorld, halfHeightWorld, radiusWorld) * PBF_Utils.InvScale);
                extentSim = extentNoMargin + margin;
                shape = ColliderShapes.Capsule;
            }

            return new MyBoxCollider
            {
                Center = centerSim,
                Extent = extentSim,
                Type = entry.Type,
                Friction = entry.Friction,
                IsDynamic = entry.IsDynamic ? 1 : 0,
                Velocity = entry.IsDynamic ? (float3)(entry.VelocityWorld * PBF_Utils.InvScale) : float3.zero,
                Shape = shape,
                Rotation = rot,
            };
        }

        public void AppendMyBoxColliders(
            Vector3 centerWorld,
            float radiusWorld,
            Transform ignoreRootA,
            Transform ignoreRootB,
            NativeArray<MyBoxCollider> outBuffer,
            ref int outCount,
            int maxCount,
            HashSet<int> visited)
        {
            if (outCount >= maxCount)
                return;

            float r = math.max(0.001f, radiusWorld);
            float r2 = r * r;

            if (_oversizedStaticIds.Count > 0 && outCount < maxCount)
            {
                for (int i = 0; i < _oversizedStaticIds.Count && outCount < maxCount; i++)
                {
                    int id = _oversizedStaticIds[i];
                    if (visited != null && !visited.Add(id))
                        continue;

                    if (!_entries.TryGetValue(id, out var entry))
                        continue;

                    var col = entry.Collider;
                    if (col == null)
                        continue;

                    if (!includeTriggers && col.isTrigger)
                        continue;

                    if (!col.enabled || !col.gameObject.activeInHierarchy)
                        continue;

                    if (col.transform != null)
                    {
                        if (ignoreRootA != null && col.transform.IsChildOf(ignoreRootA))
                        {
                            continue;
                        }
                        if (ignoreRootB != null && col.transform.IsChildOf(ignoreRootB))
                        {
                            continue;
                        }
                    }

                    Bounds b = entry.Bounds;
                    Vector3 cp = b.ClosestPoint(centerWorld);
                    float dx = centerWorld.x - cp.x;
                    float dy = centerWorld.y - cp.y;
                    float dz = centerWorld.z - cp.z;
                    float dist2 = dx * dx + dy * dy + dz * dz;
                    if (dist2 > r2)
                        continue;

                    outBuffer[outCount] = BuildMyBoxCollider(col, in entry, in b);
                    outCount++;
                }
            }

            if (outCount >= maxCount)
                return;

            int minX = WorldToBucket(centerWorld.x - r);
            int maxX = WorldToBucket(centerWorld.x + r);
            int minZ = WorldToBucket(centerWorld.z - r);
            int maxZ = WorldToBucket(centerWorld.z + r);

            for (int bz = minZ; bz <= maxZ; bz++)
            {
                for (int bx = minX; bx <= maxX; bx++)
                {
                    long key = MakeKey(bx, bz);
                    if (!_buckets.TryGetValue(key, out var list) || list == null)
                        continue;

                    for (int i = 0; i < list.Count; i++)
                    {
                        int id = list[i];
                        if (visited != null && !visited.Add(id))
                            continue;

                        if (!_entries.TryGetValue(id, out var entry))
                            continue;

                        var col = entry.Collider;
                        if (col == null)
                            continue;

                        if (!includeTriggers && col.isTrigger)
                            continue;

                        if (!col.enabled || !col.gameObject.activeInHierarchy)
                            continue;

                        if (col.transform != null)
                        {
                            if (ignoreRootA != null && col.transform.IsChildOf(ignoreRootA))
                            {
                                continue;
                            }
                            if (ignoreRootB != null && col.transform.IsChildOf(ignoreRootB))
                            {
                                continue;
                            }
                        }

                        Bounds b = entry.Bounds;
                        Vector3 cp = b.ClosestPoint(centerWorld);
                        float dx = centerWorld.x - cp.x;
                        float dy = centerWorld.y - cp.y;
                        float dz = centerWorld.z - cp.z;
                        float dist2 = dx * dx + dy * dy + dz * dz;
                        if (dist2 > r2)
                            continue;

                        outBuffer[outCount] = BuildMyBoxCollider(col, in entry, in b);
                        outCount++;

                        if (outCount >= maxCount)
                            return;
                    }
                }
            }
        }

        private int WorldToBucket(float world)
        {
            return Mathf.FloorToInt(world * _invBucketSizeWorld);
        }

        private static long MakeKey(int bx, int bz)
        {
            unchecked
            {
                return ((long)bx << 32) ^ (uint)bz;
            }
        }

        private void AddToBuckets(int id, int minX, int maxX, int minZ, int maxZ)
        {
            int spanX = (maxX - minX) + 1;
            int spanZ = (maxZ - minZ) + 1;
            int bucketCount = spanX * spanZ;
            if (bucketCount > OversizedStaticMaxBuckets)
            {
                if (_entries.TryGetValue(id, out var entry) && !entry.IsDynamic)
                {
                    if (!entry.IsOversized)
                    {
                        _oversizedStaticIds.Add(id);
                        entry.IsOversized = true;
                    }
                    entry.InBuckets = true;
                    entry.MinX = minX;
                    entry.MaxX = maxX;
                    entry.MinZ = minZ;
                    entry.MaxZ = maxZ;
                    _entries[id] = entry;
                    return;
                }
            }

            for (int bz = minZ; bz <= maxZ; bz++)
            {
                for (int bx = minX; bx <= maxX; bx++)
                {
                    long key = MakeKey(bx, bz);
                    if (!_buckets.TryGetValue(key, out var list) || list == null)
                    {
                        list = new List<int>(8);
                        _buckets[key] = list;
                    }
                    list.Add(id);
                }
            }
        }

        private void RemoveFromBuckets(int id, int minX, int maxX, int minZ, int maxZ)
        {
            if (_entries.TryGetValue(id, out var entry) && entry.IsOversized)
                return;

            for (int bz = minZ; bz <= maxZ; bz++)
            {
                for (int bx = minX; bx <= maxX; bx++)
                {
                    long key = MakeKey(bx, bz);
                    if (!_buckets.TryGetValue(key, out var list) || list == null)
                        continue;

                    int idx = list.IndexOf(id);
                    if (idx < 0)
                        continue;

                    int last = list.Count - 1;
                    list[idx] = list[last];
                    list.RemoveAt(last);
                }
            }
        }
    }
}
