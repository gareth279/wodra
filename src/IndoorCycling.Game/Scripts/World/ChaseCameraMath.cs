using System.Collections.Generic;
using System.Numerics;

public enum RiderCameraView
{
    GameplayOriginal,
    GameplayRefined,
    CloseRear,
    RearThreeQuarter,
    Side,
    FrontThreeQuarter
}

/// <summary>Deterministic inspection camera order shared by runtime and regression tests.</summary>
public static class RiderCameraViewCycle
{
    private static readonly RiderCameraView[] OrderedViews =
    [
        RiderCameraView.GameplayRefined,
        RiderCameraView.CloseRear,
        RiderCameraView.RearThreeQuarter,
        RiderCameraView.Side,
        RiderCameraView.FrontThreeQuarter,
        RiderCameraView.GameplayOriginal
    ];

    public static IReadOnlyList<RiderCameraView> Views => OrderedViews;

    public static RiderCameraView Next(RiderCameraView current)
    {
        for (var i = 0; i < OrderedViews.Length; i++)
            if (OrderedViews[i] == current) return OrderedViews[(i + 1) % OrderedViews.Length];
        return OrderedViews[0];
    }
}

public static class CameraInspectionInputPolicy
{
    public static bool CanCycle(bool rideActive, bool menuVisible, bool keyPressed, bool keyEcho) =>
        rideActive && !menuVisible && keyPressed && !keyEcho;
}

/// <summary>Existing chase-camera anchors expressed relative to the visual rider anchor.</summary>
public static class ChaseCameraMath
{
    public static Vector3 FollowPosition(Vector3 riderPosition, Vector3 forward) =>
        FollowPosition(riderPosition, forward, RiderCameraView.GameplayOriginal);

    public static Vector3 LookTarget(Vector3 riderPosition, Vector3 forward) =>
        LookTarget(riderPosition, forward, RiderCameraView.GameplayOriginal);

    public static Vector3 FollowPosition(Vector3 riderPosition, Vector3 forward, RiderCameraView view)
    {
        var basis = CameraBasis(forward);
        var (back, side, height) = view switch
        {
            RiderCameraView.GameplayRefined => (5.6f, 1.35f, 2.25f),
            RiderCameraView.CloseRear => (3.4f, 0f, 1.8f),
            RiderCameraView.RearThreeQuarter => (4.6f, 2.0f, 1.9f),
            RiderCameraView.Side => (-.2f, 4.0f, 1.4f),
            RiderCameraView.FrontThreeQuarter => (-4.5f, 2.0f, 1.9f),
            _ => (8f, 0f, 4f)
        };
        return riderPosition - basis.Forward * back + basis.Right * side + Vector3.UnitY * height;
    }

    public static Vector3 LookTarget(Vector3 riderPosition, Vector3 forward, RiderCameraView view)
    {
        var basis = CameraBasis(forward);
        var (ahead, side, height) = view switch
        {
            RiderCameraView.GameplayRefined => (4.8f, .1f, .95f),
            RiderCameraView.CloseRear => (1.1f, 0f, .95f),
            RiderCameraView.RearThreeQuarter => (1.0f, .15f, 1.0f),
            RiderCameraView.Side => (0f, 0f, .92f),
            RiderCameraView.FrontThreeQuarter => (-.6f, 0f, 1.0f),
            _ => (6f, 0f, 1f)
        };
        return riderPosition + basis.Forward * ahead + basis.Right * side + Vector3.UnitY * height;
    }

    private static (Vector3 Forward, Vector3 Right) CameraBasis(Vector3 forward)
    {
        var flat = new Vector3(forward.X, 0, forward.Z);
        if (flat.LengthSquared() < 1e-8f) flat = -Vector3.UnitZ;
        flat = Vector3.Normalize(flat);
        return (flat, Vector3.Normalize(Vector3.Cross(flat, Vector3.UnitY)));
    }
}
