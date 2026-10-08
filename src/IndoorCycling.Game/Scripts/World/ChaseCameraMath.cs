using System.Numerics;

/// <summary>Existing chase-camera anchors expressed relative to the visual rider anchor.</summary>
public static class ChaseCameraMath
{
    public static Vector3 FollowPosition(Vector3 riderPosition,Vector3 forward)=>riderPosition-forward*8+Vector3.UnitY*4;
    public static Vector3 LookTarget(Vector3 riderPosition,Vector3 forward)=>riderPosition+forward*6+Vector3.UnitY;
}
