using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Unity.Mathematics;
// ReSharper disable InconsistentNaming

namespace Revive.Mathematics
{
    public static partial class mathex
    {
        #region chain methods
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 normalize(this float2 v) => math.normalize(v);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float length(this float2 v) => math.length(v);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float lengthsq(this float2 v) => math.lengthsq(v);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 normalize(this float3 v) => math.normalize(v);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float length(this float3 v) => math.length(v);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float lengthsq(this float3 v) => math.lengthsq(v);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float dot(this float3 a, float3 b) => math.dot(a, b);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 cross(this float3 a, float3 b) => math.cross(a, b);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double2 normalize(this double2 v) => math.normalize(v);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double length(this double2 v) => math.length(v);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double lengthsq(this double2 v) => math.lengthsq(v);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double length(this double3 v) => math.length(v);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double lengthsq(this double3 v) => math.lengthsq(v);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double dot(this double3 a, double3 b) => math.dot(a, b);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double3 cross(this double3 a, double3 b) => math.cross(a, b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4x4 mul(this float4x4 m, float4x4 v) => math.mul(m, v);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4x4 inverse(this float4x4 m) => math.inverse(m);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static quaternion mul(this quaternion a, quaternion b) => math.mul(a, b);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 mul(this quaternion q, float3 v) => math.mul(q, v);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static quaternion normalize(this quaternion q) => math.normalize(q);

        #endregion

        #region sdf
        
        // ref: https://iquilezles.org/articles/distfunctions/
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float udSqrSegment(this float3 p, float3 a, float3 b)
        {
            float3 pa = p - a, ba = b - a;
            var h = clamp01(dot(pa, ba) / dot(ba, ba));
            return math.lengthsq(pa - ba * h);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double udSqrSegment(this double3 p, double3 a, double3 b)
        {
            double3 pa = p - a, ba = b - a;
            var h = clamp01(dot(pa, ba) / dot(ba, ba));
            return math.lengthsq(pa - ba * h);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float udSegment(this float3 p, float3 a, float3 b) =>
            math.sqrt(p.udSqrSegment(a, b));
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double udSegment(this double3 p, double3 a, double3 b) =>
            math.sqrt(p.udSqrSegment(a, b));
        
        public static float udSegment(this float2 point, float2 point0, float2 point1)
        {
            var segmentDir = (point1 - point0);
            var segmentLength = segmentDir.length();
            segmentDir = segmentDir.normalize();

            var fromStartToPt = (point - point0);

            var projection = math.dot(segmentDir, fromStartToPt);

            if (projection >= 0.0f && projection <= segmentLength)
                return ((point0 + segmentDir * projection) - point).length();

            if (projection < 0.0f) return fromStartToPt.length();
            return (point1 - point).length();
        }
        
        public static double udSegment(this double2 point, double2 point0, double2 point1)
        {
            var segmentDir = (point1 - point0);
            var segmentLength = segmentDir.length();
            segmentDir = segmentDir.normalize();

            var fromStartToPt = (point - point0);

            var projection = math.dot(segmentDir, fromStartToPt);

            if (projection >= 0.0f && projection <= segmentLength)
                return ((point0 + segmentDir * projection) - point).length();

            if (projection < 0.0f) return fromStartToPt.length();
            return (point1 - point).length();
        }

        public static float sdCapsule(this float3 p, float3 a, float3 b, float r) =>
            p.udSegment(a, b) - r;


        public static float udTriangle(this float3 p, float3 a, float3 b, float3 c)
        {
            var ba = b - a;
            var pa = p - a;
            var cb = c - b;
            var pb = p - b;
            var ac = a - c;
            var pc = p - c;
            var nor = math.cross(ba, ac);

            return math.sqrt(
                math.sign(dot(math.cross(ba, nor), pa)) +
                math.sign(dot(math.cross(cb, nor), pb)) +
                math.sign(dot(math.cross(ac, nor), pc)) < 2
                    ? math.min(math.min(
                            math.lengthsq(ba * clamp01(dot(ba, pa) / math.lengthsq(ba)) - pa),
                            math.lengthsq(cb * clamp01(dot(cb, pb) / math.lengthsq(cb)) - pb)),
                        math.lengthsq(ac * clamp01(dot(ac, pc) / math.lengthsq(ac)) - pc))
                    : dot(nor, pa) * dot(nor, pa) / math.lengthsq(nor));
        }

        public static float udQuad(this float3 p, float3 a, float3 b, float3 c, float3 d)
        {
            var ba = b - a;
            var pa = p - a;
            var cb = c - b;
            var pb = p - b;
            var dc = d - c;
            var pc = p - c;
            var ad = a - d;
            var pd = p - d;
            var nor = math.cross(ba, ad);

            return math.sqrt(
                math.sign(dot(math.cross(ba, nor), pa)) +
                math.sign(dot(math.cross(cb, nor), pb)) +
                math.sign(dot(math.cross(dc, nor), pc)) +
                math.sign(dot(math.cross(ad, nor), pd)) < 3
                    ? math.min(math.min(math.min(
                                math.lengthsq(ba * clamp01(dot(ba, pa) / math.lengthsq(ba)) - pa),
                                math.lengthsq(cb * clamp01(dot(cb, pb) / math.lengthsq(cb)) - pb)),
                            math.lengthsq(dc * clamp01(dot(dc, pc) / math.lengthsq(dc)) - pc)),
                        math.lengthsq(ad * clamp01(dot(ad, pd) / math.lengthsq(ad)) - pd))
                    : dot(nor, pa) * dot(nor, pa) / math.lengthsq(nor));
        }

        #endregion

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SafeAcos(float cosine) { return math.acos(math.max(-1.0f, math.min(1.0f, cosine))); }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4x4 Scale(this RigidTransform frame, float3 scale)
        {
            var frameMat = new float4x4(frame);
            var mat = mul(frameMat, mul(float4x4.Scale(scale), inverse(frameMat)));
            return mat;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 GetNormal(this float2 vec)
        {
            return (new float2(-vec.y, vec.x)).normalize();
        }
        
        public static float SignedAngle(float3 from, float3 to, float3 axis)
        {
            float dot = math.dot(from.normalize(), to.normalize());
            if ((1.0f - dot) < 1e-5f) return 0.0f;
            if ((1.0f + dot) < 1e-5f) return math.PI;

            var cross = math.cross(from, to).normalize();
            float angle = SafeAcos(dot);
            if (math.dot(cross, axis) < 0.0f) angle = -angle;

            return angle;
        }

        public static float Angle(this float3 from, float3 to)
        {
            var num = math.sqrt((double) from.lengthsq() * (double) to.lengthsq());
            return num < 1.0000000036274937E-15 ? 0.0f : (float) math.acos(math.clamp(math.dot(from, to) / num, -1f, 1f));
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Angle(this double3 from, double3 to)
        {
            var num = math.sqrt(from.lengthsq() * to.lengthsq());
            return num < 1.0000000036274937E-15 ? 0.0f : math.acos(math.clamp(math.dot(from, to) / num, -1f, 1f));
        }
        
        public static bool IsAngleWithinRange(float3 a, float3 b, float3 c, float angle = 2f)
        {
            double abX = b.x - a.x;
            double abY = b.y - a.y;
            double abZ = b.z - a.z;

            double bcX = c.x - b.x;
            double bcY = c.y - b.y;
            double bcZ = c.z - b.z;

            double dot = abX * bcX + abY * bcY + abZ * bcZ;
            double lengthProduct = math.sqrt(abX * abX + abY * abY + abZ * abZ) * math.sqrt(bcX * bcX + bcY * bcY + bcZ * bcZ);
            double cosAngle = dot / lengthProduct;

            double angleBetween = math.acos(cosAngle) * math.TODEGREES;

            return angleBetween <= angle;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 ProjectOnSegment(this float3 point, float3 point0, float3 point1)
        {
            float3 segmentDir = (point1 - point0).normalize();
            return point0 + segmentDir * math.dot(point - point0, segmentDir);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 ProjectOnPlane(this float3 vector, float3 planeNormal)
        {
            float num1 = math.dot(planeNormal, planeNormal);
            if ((double) num1 < math.EPSILON_DBL)
                return vector;
            float num2 = math.dot(vector, planeNormal);
            return new float3(vector.x - planeNormal.x * num2 / num1, vector.y - planeNormal.y * num2 / num1, vector.z - planeNormal.z * num2 / num1);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float AbsDot(this float3 v1, float3 v2) { return math.abs(math.dot(v1, v2)); }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float clamp01(in float x) => math.clamp(x, 0, 1);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double clamp01(in double x) => math.clamp(x, 0, 1);
        
        public static int GetMaxAbsAxis(this float3 v)
        {
            float absX = math.abs(v.x);
            float absY = math.abs(v.y);
            float absZ = math.abs(v.z);

            if (absX > absY)
            {
                if (absX > absZ) return 0;
                return 2;
            }

            if (absY > absZ) return 1;
            return 2;
        }
        
        public static float GetMaxAbsComp(this float3 v)
        {
            float maxAbsComp = math.abs(v.x);

            float absY = math.abs(v.y);
            if (absY > maxAbsComp) maxAbsComp = absY;
            float absZ = math.abs(v.z);
            if (absZ > maxAbsComp) maxAbsComp = absZ;

            return maxAbsComp;
        }
        
        public static int GetMinAbsAxis(this float3 v)
        {
            float absX = math.abs(v.x);
            float absY = math.abs(v.y);
            float absZ = math.abs(v.z);

            if (absX < absY)
            {
                if (absX < absZ) return 0;
                return 2;
            }

            if (absY < absZ) return 1;
            return 2;
        }
        
        public static float GetMinAbsComp(this float3 v)
        {
            float minAbsComp = math.abs(v.x);

            float absY = math.abs(v.y);
            if (absY < minAbsComp) minAbsComp = absY;
            float absZ = math.abs(v.z);
            if (absZ < minAbsComp) minAbsComp = absZ;

            return minAbsComp;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool approx(this float a, float b, float tolerance = math.EPSILON)
        {
            return MathF.Abs(a - b) < tolerance;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool approx(this float3 a, float3 b, float tolerance = math.EPSILON)
        {
            return MathF.Abs(a.x - b.x) < tolerance 
                   && MathF.Abs(a.y - b.y) < tolerance 
                   && MathF.Abs(a.z - b.z) < tolerance;
        }
        
        /// <summary>
        /// 判断两个 float3 点是否近似相等（基于欧氏距离）。
        /// 适用于工业 CAD 场景的几何点比较，使用平方距离避免开方运算，几何意义更准确。
        /// </summary>
        /// <param name="a">第一个点</param>
        /// <param name="b">第二个点</param>
        /// <param name="absTolerance">绝对容差（默认 1e-6f，约 1 微米，适用于米单位）</param>
        /// <param name="relTolerance">相对容差（默认 0，不启用相对容差）</param>
        /// <returns>如果两点距离小于等于有效容差则返回 true</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool approxEqual(this float3 a, float3 b, float absTolerance = 1e-6f, float relTolerance = 0f)
        {
            float3 diff = a - b;
            float distSq = math.lengthsq(diff);
            
            // 计算有效容差：考虑相对容差时，基于两点中较大的模长
            float effectiveTol = absTolerance;
            if (relTolerance > 0f)
            {
                float baseMag = math.max(math.length(a), math.length(b));
                effectiveTol = math.max(absTolerance, relTolerance * baseMag);
            }
            
            return distSq <= effectiveTol * effectiveTol;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool approx(this quaternion a, quaternion b, float tolerance = math.EPSILON)
        {
            var av = a.value;
            var bv = b.value;
            return MathF.Abs(av.x - bv.x) < tolerance 
                   && MathF.Abs(av.y - bv.y) < tolerance 
                   && MathF.Abs(av.z - bv.z) < tolerance
                   && MathF.Abs(av.w - bv.w) < tolerance;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool approx(this RigidTransform a, RigidTransform b, float tolerance = math.EPSILON)
        {
            return a.pos.approx(b.pos, tolerance) && a.rot.approx(b.rot, tolerance);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RigidTransform lerp(this RigidTransform a, RigidTransform b, float t)
        {
            return new RigidTransform(
                math.slerp(a.rot, b.rot, t),
                math.lerp(a.pos, b.pos, t));
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RigidTransform flip(this RigidTransform self)
        {
            return new RigidTransform(self.rot.mul(quaternion.AxisAngle(math.right(), math.PI)), self.pos);
        }
        
        /// <summary>
        ///   <para>Rotates the transform about axis passing through point in world coordinates by angle degrees.</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RigidTransform RotateAround(this in RigidTransform t, float3 point, float3 axis, float angle)
        {
            var worldPos = t.pos;
            var worldRot = t.rot;
            
            var fromPivotToPos = worldPos - point;

            var angleAxis = quaternion.AxisAngle(axis, angle);
            
            fromPivotToPos = angleAxis.mul(fromPivotToPos);
            var pos = point + fromPivotToPos;
            var rot = angleAxis.mul(worldRot);
            return new RigidTransform(rot, pos);
        }
        
        /// <summary>
        ///   <para>Rotates the transform about axis passing through pivot in world coordinates by rotation and pivot.</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RigidTransform RotateAroundPivot(this in RigidTransform t, in quaternion rotation, in float3 pivot)
        {
            float3 fromPivotToPos = t.pos - pivot;
            fromPivotToPos = rotation.mul(fromPivotToPos);
            return new RigidTransform(rotation.mul(t.rot).normalize(), pivot + fromPivotToPos);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Repeat(float t, float length) {
	        return math.clamp(t - math.floor(t / length) * length, 0.0f, length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float PingPong(float t, float length) {
	        t = Repeat(t, length * 2);
	        return length - math.abs(t - length);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float DeltaAngle(float current, float target) {
	        float num = Repeat(target - current, math.PI2);
	        if (num > math.PI)
		        num -= math.PI2;
	        return num;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float LerpAngle(float a, float b, float t) {
	        float num = Repeat(b - a, math.PI2);
	        if (num > math.PI)
		        num -= math.PI2;
	        return a + num * clamp01(t);
        }
        
        /// <summary>
        /// Returns delta angle from 'dir1' to 'dir2' in degrees.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float DeltaAngle(double2 dir1, double2 dir2)
        {
	        var angle1 = math.atan2(dir1.x, dir1.y);
	        var angle2 = math.atan2(dir2.x, dir2.y);
	        return DeltaAngle(angle1, angle2);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAligned(this float3 vector, float3 other, bool checkSameDirection)
        {
	        if (!checkSameDirection)
	        {
		        float absDot = vector.AbsDot(other);
		        return math.abs(absDot - 1.0f) < 1e-5f;
	        }
	        else
	        {
		        float dot = math.dot(vector, other);
		        return dot > 0.0f && math.abs(dot - 1.0f) < 1e-5f;
	        }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 float3(this float2 v) => math.float3(v, 0);
        
        public static quaternion FromToRotation3D(float3 from, float3 to, float3 perp180)
        {
            from = from.normalize();
            to = to.normalize();

            float dot = math.dot(from, to);
            if (1.0f - dot < 1e-5f) return quaternion.identity;
            if (1.0f + dot < 1e-5f) return quaternion.AxisAngle(perp180, math.PI);

            float angle = SafeAcos(dot);
            float3 rotationAxis = math.cross(from, to).normalize();
            return quaternion.AxisAngle(rotationAxis, angle);
        }
        
        public static quaternion FromToRotation2D(float2 from, float2 to)
        {
            from = from.normalize();
            to = to.normalize();

            float dot = math.dot(from, to);
            if (1.0f - dot < 1e-5f) return quaternion.identity;
            if (1.0f + dot < 1e-5f) return quaternion.AxisAngle(math.forward(), math.PI);

            float angle = SafeAcos(dot);
            var rotationAxis = math.cross(from.float3(), to.float3()).normalize();
            return quaternion.AxisAngle(rotationAxis, angle);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ConvertTo2DRotation(this quaternion quat)
        {
            quat.ToAngleAxis(out var angle, out var axis);

            if (math.dot(math.forward(), axis) < 0.0f) angle = -angle;
            return angle;
        }

        /// <summary>
        /// Converts a rotation to angle-axis representation.
        /// </summary>
        /// <param name="q"></param>
        /// <param name="angle">Angle in degrees</param>
        /// <param name="axis">Normalized axis vector</param>
        public static void ToAngleAxis(this quaternion q, out float angle, out float3 axis)
        {
            // Internal implementation works in radians
            ToAxisAngleRad(q, out axis, out angle);
            
            // Convert from radians to degrees
            angle *= math.TODEGREES;
        }

        /// <summary>
        /// Internal implementation that works in radians
        /// </summary>
        private static void ToAxisAngleRad(this quaternion q, out float3 axis, out float angle)
        {
            // Normalize the quaternion first 
            var normalized = math.normalizesafe(q);
            
            // Call the core algorithm
            QuaternionToAxisAngle(normalized, out axis, out angle);
        }

        /// <summary>
        /// Core algorithm from QuaternionToAxisAngle function
        /// </summary>
        private static void QuaternionToAxisAngle(this quaternion q, out float3 axis, out float angle)
        {
            // AssertIf (! CompareApproximately(SqrMagnitude (q), 1.0F));

            // Calculate angle: angle = 2 * acos(w)
            var w = q.value.w;
            angle = 2.0f * (float)MathF.Acos(MathF.Abs(w));

            // Handle near-zero rotation case
            if (approx(angle, 0.0f))
            {
                axis = math.right();
                return;
            }

            // Calculate axis from quaternion components
            // div = 1 / sqrt(1 - w²)
            float div = 1.0f / (float)MathF.Sqrt(1.0f - w * w);
            axis = q.value.xyz * div;
        }

        /// <summary>
        /// 球面线性插值
        /// </summary>
        public static float3 Slerp(float3 from, float3 to, float t)
        {
            // 计算两个向量的模长
            float lhsMag = math.length(from);
            float rhsMag = math.length(to);
            
            // 如果任一向量的模长小于epsilon，回退到线性插值
            if (lhsMag < math.EPSILON || rhsMag < math.EPSILON)
                return math.lerp(from, to, t);
            
            // 计算插值后的模长
            float lerpedMagnitude = lhsMag + (rhsMag - lhsMag) * t;
            
            // 计算标准化后的点积
            float dot = math.dot(from, to) / (lhsMag * rhsMag);
            
            // 方向几乎相同
            if (dot > 1.0f - math.EPSILON)
            {
                return math.lerp(from, to, t);
            }
            // 方向几乎相反
            else if (dot < -1.0f + math.EPSILON)
            {
                var lhsNorm = from / lhsMag;
                var axis = OrthoNormalVectorFast(lhsNorm);
                var slerped = RotateAroundAxis(lhsNorm, axis, (float)math.PI * t);
                return slerped * lerpedMagnitude;
            }
            // 一般情况
            else
            {
                var axis = math.normalize(math.cross(from, to));
                var lhsNorm = from / lhsMag;
                float angle = (float)MathF.Acos(MathF.Max(-1f, MathF.Min(1f, dot))) * t;
                
                var slerped = RotateAroundAxis(lhsNorm, axis, angle);
                return slerped * lerpedMagnitude;
            }
        }
        
        /// <summary>
        /// 生成与给定向量正交的标准化向量
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 OrthoNormalVectorFast(this in float3 n)
        {
            float3 res;
            if (MathF.Abs(n.z) > 0.7f)
            {
                float a = n.y * n.y + n.z * n.z;
                float k = 1.0f / (float)MathF.Sqrt(a);
                res = new (0, -n.z * k, n.y * k);
            }
            else
            {
                float a = n.x * n.x + n.y * n.y;
                float k = 1.0f / (float)MathF.Sqrt(a);
                res = new (-n.y * k, n.x * k, 0);
            }
            return res;
        }
        
        /// <summary>
        /// 绕轴旋转向量
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 RotateAroundAxis(float3 vector, float3 axis, float angle)
        {
            float cos = (float)MathF.Cos(angle);
            float sin = (float)MathF.Sin(angle);
            float dot = math.dot(vector, axis);
            
            return vector * cos + 
                   math.cross(axis, vector) * sin + 
                   axis * dot * (1 - cos);
        }
        
        public static void Flip(ref this RigidTransform t) => t.rot = math.mul(t.rot, (quaternion.AxisAngle(math.right(), math.PI)));
        
        /// <summary>
        /// 计算关于平面的镜像变换矩阵（4x4）。
        /// </summary>
        /// <param name="pointOnPlane">平面上的一个点</param>
        /// <param name="planeNormal">平面的法向量（会自动归一化）</param>
        /// <returns>4x4 镜像变换矩阵</returns>
        public static float4x4 GetMirrorMatrix(float3 pointOnPlane, float3 planeNormal)
        {
            // 归一化法向量
            float3 n = math.normalize(planeNormal);
        
            // 计算外积矩阵 n * n^T
            float3x3 nnT = new float3x3(
                n.x * n.x, n.x * n.y, n.x * n.z,
                n.y * n.x, n.y * n.y, n.y * n.z,
                n.z * n.x, n.z * n.y, n.z * n.z
            );
        
            // 计算反射矩阵 I - 2 * n * n^T
            float3x3 identity = float3x3.identity;
            float3x3 reflection = identity - 2.0f * nnT;
        
            // 计算平移向量 t = 2 * (n · p) * n
            float d = math.dot(n, pointOnPlane);
            float3 translation = 2.0f * d * n;
        
            // 构造 4x4 矩阵
            float4x4 mirrorMatrix = math.transpose(new float4x4(
                new float4(reflection.c0, translation.x),
                new float4(reflection.c1, translation.y),
                new float4(reflection.c2, translation.z),
                new float4(0, 0, 0, 1)
            ));
        
            return mirrorMatrix;
        }
        
        /// <summary>
        /// 基于自定义坐标系的缩放变换矩阵。
        /// </summary>
        /// <param name="coordSystem">自定义坐标系（RigidTransform，包含旋转和原点平移）</param>
        /// <param name="scale">沿自定义坐标系三个轴的缩放因子 (sx, sy, sz)</param>
        /// <returns>4x4 变换矩阵</returns>
        public static float4x4 GetScalingMatrix(RigidTransform coordSystem, float3 scale)
        {
            // 提取旋转和位置
            quaternion rot = coordSystem.rot;
            float3 pos = coordSystem.pos;

            // 构建旋转矩阵 R（从自定义坐标系到世界坐标系）
            float3x3 R = new float3x3(rot);

            // 构建 R 的逆矩阵（对于旋转矩阵，R^-1 = R^T）
            float3x3 R_inv = math.transpose(R);

            // 构建缩放矩阵 S
            float3x3 S = new float3x3(
                scale.x, 0, 0,
                0, scale.y, 0,
                0, 0, scale.z
            );

            // 构建平移矩阵 Translate(p) 和 Translate(-p)
            float4x4 translatePos = float4x4.Translate(pos);
            float4x4 translateNegPos = float4x4.Translate(-pos);

            // 计算中间矩阵：R * S * R^-1
            float3x3 RSR_inv = math.mul(R, math.mul(S, R_inv));

            // 构建最终 4x4 变换矩阵
            float4x4 result = math.mul(translatePos, new float4x4(RSR_inv, 0));
            result = math.mul(result, translateNegPos);

            return result;
        }
        
        /// <summary>
        /// 生成绕指定轴和枢轴点旋转的 4x4 变换矩阵。
        /// </summary>
        /// <param name="pivot">旋转中心点</param>
        /// <param name="axis">旋转轴（会自动归一化）</param>
        /// <param name="angle">旋转角度（弧度）</param>
        /// <returns>4x4 旋转变换矩阵</returns>
        public static float4x4 GetRotationMatrix(float3 pivot, float3 axis, float angle)
        {
            axis = math.normalize(axis);
            
            var t = float4x4.Translate(pivot);
            var tInv = float4x4.Translate(-pivot);
            var r = float4x4.AxisAngle(axis, angle);
            var mat = math.mul(t, math.mul(r, tInv));
            return mat;
        }
    }
}