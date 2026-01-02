using Unity.Mathematics;

namespace Revive.Environment
{
    public struct WindFieldZoneData
    {
        public float3 CenterSim;
        public float3 ExtentsSim;
        public float GroundDrag;
        public float AirDrag;
        public float3 PushSim;
        public int AffectsLayerMask;
    }
}
