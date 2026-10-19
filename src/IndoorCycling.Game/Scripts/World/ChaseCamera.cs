using System;
using Godot;

public partial class ChaseCamera : Camera3D
{
    private const uint ObstructionCollisionMask = 1u << 1;
    private const float CameraShapeRadiusMeters = 0.28f;
    private const float CameraClearanceMeters = 0.12f;
    private Vector3 _lookTarget;
    private float _boomDistance;
    private bool _snap = true;
    private RiderCameraView _view = RiderCameraView.GameplayRefined;
    private readonly SphereShape3D _clearanceShape = new() { Radius = CameraShapeRadiusMeters };
    private readonly PhysicsShapeQueryParameters3D _clearanceQuery = new()
    {
        CollisionMask = ObstructionCollisionMask,
        CollideWithAreas = false,
        CollideWithBodies = true,
        Margin = 0.02f
    };
    private string? _obstructionSource;
    private string _clearanceResult = "unresolved";
    private Vector3 _desiredPosition;

    public Vector3 DesiredPosition => _desiredPosition;
    public string? ObstructionSource => _obstructionSource;
    public string ClearanceResult => _clearanceResult;

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
        _desiredPosition = targetPosition;
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
            var blocked = allowedDistance + 0.001f < desiredDistance;
            var smoothed = blocked || _snap ? safeTarget : GlobalPosition.Lerp(safeTarget, weight);
            // Sweep the camera's near-plane volume along its actual eased path as well
            // as the rider-to-camera boom. A point ray misses corner and near-plane clips.
            GlobalPosition = blocked || _snap
                ? safeTarget
                : ResolveMovementPosition(GlobalPosition, smoothed, safeTarget);
        }
        _lookTarget = _lookTarget.Lerp(lookTarget, weight);
        LookAt(_lookTarget, Vector3.Up);
        _snap = false;
    }

    private float FindAllowedBoomDistance(Vector3 anchor, Vector3 target, float desiredDistance)
    {
        _obstructionSource = null;
        var state = GetSpaceState();
        if (state is null || desiredDistance <= 1e-5f)
        {
            _clearanceResult = "query-unavailable";
            return desiredDistance;
        }

        var direction = (target - anchor) / desiredDistance;
        SetQueryTransform(anchor);
        var startOverlaps = state.IntersectShape(_clearanceQuery, 1);
        if (startOverlaps.Count > 0)
        {
            _obstructionSource = ColliderName(startOverlaps[0]);
            _clearanceResult = "anchor-overlap";
            // An anchor overlap means the source geometry intrudes on the rider
            // volume. Avoid sending the camera farther through it.
            return 0;
        }

        _clearanceQuery.Motion = direction * desiredDistance;
        var motion = state.CastMotion(_clearanceQuery);
        if (motion.Length < 2 || motion[0] >= 0.99999f)
        {
            _clearanceResult = "clear";
            return desiredDistance;
        }

        var safeDistance = CameraObstructionMath.ResolveSafeDistance(desiredDistance,
            motion[0], CameraClearanceMeters);
        SetQueryTransform(anchor + direction * Math.Min(desiredDistance, motion[1] * desiredDistance));
        var hit = state.IntersectShape(_clearanceQuery, 1);
        _obstructionSource = hit.Count > 0 ? ColliderName(hit[0]) : "camera-collision-mesh";
        _clearanceResult = "boom-clamped";
        return safeDistance;
    }

    private Vector3 ResolveMovementPosition(Vector3 from, Vector3 to, Vector3 safeTarget)
    {
        var state = GetSpaceState();
        if (state is null || from.DistanceSquaredTo(to) <= 1e-6f) return to;
        SetQueryTransform(from);
        var startOverlaps = state.IntersectShape(_clearanceQuery, 1);
        if (startOverlaps.Count > 0)
        {
            _obstructionSource ??= ColliderName(startOverlaps[0]);
            _clearanceResult = "camera-overlap-retracted";
            return safeTarget;
        }
        var motionVector = to - from;
        _clearanceQuery.Motion = motionVector;
        var motion = state.CastMotion(_clearanceQuery);
        if (motion.Length < 2 || motion[0] >= 0.99999f) return to;
        var safeFraction = motion[0];
        var safeFractionWithClearance = Math.Max(0, safeFraction - CameraClearanceMeters / motionVector.Length());
        _clearanceResult = "movement-swept";
        SetQueryTransform(from + motionVector * Math.Min(1, motion[1]));
        var hit = state.IntersectShape(_clearanceQuery, 1);
        _obstructionSource ??= hit.Count > 0 ? ColliderName(hit[0]) : "camera-collision-mesh";
        return from.Lerp(to, safeFractionWithClearance);
    }

    private PhysicsDirectSpaceState3D? GetSpaceState() => IsInsideTree() ? GetWorld3D().DirectSpaceState : null;

    private void SetQueryTransform(Vector3 position)
    {
        _clearanceQuery.Shape = _clearanceShape;
        _clearanceQuery.Transform = new Transform3D(Basis.Identity, position);
    }

    private static string? ColliderName(Godot.Collections.Dictionary result)
    {
        if (!result.TryGetValue("collider", out var value)) return null;
        return value.AsGodotObject() is Node node ? node.Name.ToString() : "camera-collision-mesh";
    }

}
