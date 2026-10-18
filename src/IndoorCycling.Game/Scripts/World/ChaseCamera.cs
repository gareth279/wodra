using System;
using Godot;

public partial class ChaseCamera : Camera3D
{
    private const uint ObstructionCollisionMask = 1u << 1;
    private const float CameraClearanceMeters = 0.35f;
    private Vector3 _lookTarget;
    private float _boomDistance;
    private bool _snap = true;
    private RiderCameraView _view = RiderCameraView.GameplayRefined;

    public ChaseCamera()
    {
        Fov = 65;
        Near = 0.1f;
        Far = 4000;
    }

    public void ResetFollow() => _snap = true;
    public RiderCameraView View => _view;

    public void SetView(RiderCameraView view)
    {
        if (_view == view) return;
        _view = view;
        _snap = true;
    }

    public RiderCameraView CycleView()
    {
        _view = RiderCameraViewCycle.Next(_view);
        _snap = true;
        return _view;
    }

    public void Follow(Vector3 riderPosition, Vector3 forward, double delta)
    {
        var riderAnchor = new System.Numerics.Vector3(riderPosition.X,riderPosition.Y,riderPosition.Z);
        var routeForward = new System.Numerics.Vector3(forward.X,forward.Y,forward.Z);
        var follow = ChaseCameraMath.FollowPosition(riderAnchor, routeForward, _view);
        var look = ChaseCameraMath.LookTarget(riderAnchor, routeForward, _view);
        var targetPosition = new Vector3(follow.X,follow.Y,follow.Z);
        var lookTarget = new Vector3(look.X,look.Y,look.Z);
        var weight = _snap ? 1 : (float)(1 - Math.Exp(-6 * delta));
        var anchor = riderPosition + Vector3.Up * 1.05f;
        var boom = targetPosition - anchor;
        var desiredDistance = boom.Length();
        if (desiredDistance > 1e-5f)
        {
            var direction = boom / desiredDistance;
            var allowedDistance = FindAllowedBoomDistance(anchor, targetPosition, desiredDistance);
            _boomDistance = _snap || _boomDistance <= 0
                ? allowedDistance
                : CameraObstructionMath.ResolveBoomDistance(_boomDistance, allowedDistance, delta);
            var safeDistance = Math.Min(_boomDistance, allowedDistance);
            var safeTarget = anchor + direction * safeDistance;
            var smoothed = GlobalPosition.Lerp(safeTarget, weight);
            // The eased path can cross a corner even when both endpoints are clear.
            var finalHit = Raycast(anchor, smoothed);
            GlobalPosition = finalHit is { } hit
                ? ClampToHit(anchor, smoothed, hit)
                : smoothed;
        }
        _lookTarget = _lookTarget.Lerp(lookTarget, weight);
        LookAt(_lookTarget, Vector3.Up);
        _snap = false;
    }

    private float FindAllowedBoomDistance(Vector3 anchor, Vector3 target, float desiredDistance)
    {
        var hit = Raycast(anchor, target);
        if (hit is null) return desiredDistance;
        var safe = ClampToHit(anchor, target, hit.Value);
        return Math.Min(desiredDistance, Math.Max(0.05f, anchor.DistanceTo(safe)));
    }

    private static Vector3 ClampToHit(Vector3 anchor, Vector3 target, Vector3 hit)
    {
        static System.Numerics.Vector3 ToNumerics(Vector3 value) => new(value.X, value.Y, value.Z);
        var safe = CameraObstructionMath.ClampToHit(ToNumerics(anchor), ToNumerics(target),
            ToNumerics(hit), CameraClearanceMeters);
        return new Vector3(safe.X, safe.Y, safe.Z);
    }

    private Vector3? Raycast(Vector3 from, Vector3 to)
    {
        if (!IsInsideTree() || from.DistanceSquaredTo(to) <= 1e-6f) return null;
        var query = PhysicsRayQueryParameters3D.Create(from, to, ObstructionCollisionMask);
        query.CollideWithAreas = false;
        query.CollideWithBodies = true;
        var result = GetWorld3D().DirectSpaceState.IntersectRay(query);
        return result.Count > 0 && result.TryGetValue("position", out var value)
            ? value.AsVector3() : null;
    }
}
