using Revive.Mathematics;
using Unity.Mathematics;

namespace Revive.Slime
{
    public static class ColliderShapes
    {
        public const int Aabb = 0;
        public const int Obb = 1;
        public const int Capsule = 2;
    }

    public static class ColliderShapeUtils
    {
        public static void ComputeCapsule(in MyBoxCollider box, out float3 a, out float3 b, out float r)
        {
            ComputeCapsule(box.Center, box.Extent, box.Rotation, out a, out b, out r);
        }

        public static void ComputeCapsule(float3 center, float3 extent, quaternion rotation, out float3 a, out float3 b, out float r)
        {
            float3 e = extent;

            int axis = 0;
            if (e.y > e[axis]) axis = 1;
            if (e.z > e[axis]) axis = 2;

            if (axis == 0) r = math.min(e.y, e.z);
            else if (axis == 1) r = math.min(e.x, e.z);
            else r = math.min(e.x, e.y);

            float segHalf = math.max(0f, e[axis] - r);
            float3 aLocal = float3.zero;
            float3 bLocal = float3.zero;
            aLocal[axis] = -segHalf;
            bLocal[axis] = segHalf;

            a = center + math.mul(rotation, aLocal);
            b = center + math.mul(rotation, bLocal);
        }

        public static float3 ClosestPointOnSegment(float3 p, float3 a, float3 b)
        {
            float3 ab = b - a;
            float ab2 = math.dot(ab, ab);
            if (ab2 <= 1e-10f)
                return a;
            float t = mathex.clamp01(math.dot(p - a, ab) / ab2);
            return a + ab * t;
        }
    }
}
