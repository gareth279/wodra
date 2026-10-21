using System;
using Godot;

public partial class ChaseCamera : Camera3D
{
    private const uint ObstructionCollisionMask = 1u << 1;
    private const float CameraShapeRadiusMeters = 0.28f;
    private const float CameraClearanceMeters = 0.12f;
    private Vector3 _lookTarget;
    private float _boomDistance;
    private readonly Vector3[] _frameBoundsCorners = new Vector3[8];
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
    private int _repeatedBlockedMovement;
    private string _recoveryReason = "none";

    public Vector3 DesiredPosition => _desiredPosition;
    public Vector3 LookTargetPosition => _lookTarget;
    public string? ObstructionSource => _obstructionSource;
    public string ClearanceResult => _clearanceResult;
    public int RepeatedBlockedMovement => _repeatedBlockedMovement;
    public float RiderToCameraDistance { get; private set; }
    public string RecoveryReason => _recoveryReason;

    public bool HasClearanceAtCurrentPosition()
    {
        var state = GetSpaceState();
        if (state is null) return false;
        SetQueryTransform(GlobalPosition);
        return state.IntersectShape(_clearanceQuery, 1).Count == 0;
    }

    public bool CanSeeRider(Vector3 riderPosition)
    {
        return HasLineOfSight(GlobalPosition, riderPosition + Vector3.Up * 1.05f);
    }

    public ChaseCamera()
    {
        Fov = 65;
        Near = 0.1f;
        Far = 4000;
    }

    public void ResetFollow() => _snap = true;
    public RiderCameraView View => _view;
    public bool FitsVisualBounds => _view is RiderCameraView.CloseRear or RiderCameraView.RearThreeQuarter
        or RiderCameraView.Side or RiderCameraView.FrontThreeQuarter;

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
        => Follow(riderPosition, forward, delta, default, new Rect2(0, 0, 1, 1));

    public void Follow(Vector3 riderPosition, Vector3 forward, double delta, Aabb riderBikeBounds, Rect2 safeViewportRect)
        => FollowCore(riderPosition, forward, delta, riderBikeBounds, Transform3D.Identity, false, safeViewportRect);

    public void Follow(Vector3 riderPosition, Vector3 forward, double delta, Aabb riderBikeLocalBounds,
        Transform3D riderRootTransform, Rect2 safeViewportRect)
        => FollowCore(riderPosition, forward, delta, riderBikeLocalBounds, riderRootTransform, true, safeViewportRect);

    private void FollowCore(Vector3 riderPosition, Vector3 forward, double delta, Aabb riderBikeBounds,
        Transform3D boundsTransform, bool transformBounds, Rect2 safeViewportRect)
    {
        _recoveryReason = "none";
        var hasVisualBounds = riderBikeBounds.Size.LengthSquared() > 1e-4f;
        if (hasVisualBounds)
        {
            for (var i = 0; i < _frameBoundsCorners.Length; i++)
            {
                var point = riderBikeBounds.Position + new Vector3((i & 1) == 0 ? 0 : riderBikeBounds.Size.X,
                    (i & 2) == 0 ? 0 : riderBikeBounds.Size.Y,
                    (i & 4) == 0 ? 0 : riderBikeBounds.Size.Z);
                _frameBoundsCorners[i] = transformBounds ? boundsTransform * point : point;
            }
        }
        var riderAnchor = new System.Numerics.Vector3(riderPosition.X,riderPosition.Y,riderPosition.Z);
        var routeForward = new System.Numerics.Vector3(forward.X,forward.Y,forward.Z);
        var follow = ChaseCameraMath.FollowPosition(riderAnchor, routeForward, _view);
        var look = ChaseCameraMath.LookTarget(riderAnchor, routeForward, _view);
        if (FitsVisualBounds && hasVisualBounds)
            (follow, look) = FitInspectionView(riderAnchor, routeForward, _frameBoundsCorners, safeViewportRect);
        var targetPosition = new Vector3(follow.X,follow.Y,follow.Z);
        _desiredPosition = targetPosition;
        var lookTarget = new Vector3(look.X,look.Y,look.Z);
        var weight = _snap ? 1 : (float)(1 - Math.Exp(-6 * delta));
        var inspectionWeight = _snap ? 1 : (float)(1 - Math.Exp(-24 * delta));
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
            // Inspection cameras frame the rider rather than a fixed world point.
            // Smooth their relative offset in the route basis so the camera does
            // not trail a fast-moving rider by speed / smoothing-rate metres.
            // The previous world-space lerp left a persistent horizontal error
            // at riding speed (about 2.8 m at 61 km/h with the 6/s response),
            // which put Side and Front-three-quarter subjects under the HUD.
            var smoothed = safeTarget;
            if (!blocked && !_snap && FitsVisualBounds)
            {
                var flatForward = new System.Numerics.Vector3(routeForward.X, 0, routeForward.Z);
                if (flatForward.LengthSquared() < 1e-6f) flatForward = new System.Numerics.Vector3(0, 0, -1);
                flatForward = System.Numerics.Vector3.Normalize(flatForward);
                var forwardAxis = new Vector3(flatForward.X, flatForward.Y, flatForward.Z);
                var rightAxis = forwardAxis.Cross(Vector3.Up).Normalized();
                if (rightAxis.LengthSquared() < 1e-6f) rightAxis = Vector3.Right;
                var currentOffset = GlobalPosition - riderPosition;
                var targetOffset = safeTarget - riderPosition;
                var currentLocal = new Vector3(currentOffset.Dot(rightAxis), currentOffset.Y,
                    currentOffset.Dot(forwardAxis));
                var targetLocal = new Vector3(targetOffset.Dot(rightAxis), targetOffset.Y,
                    targetOffset.Dot(forwardAxis));
                var easedLocal = currentLocal.Lerp(targetLocal, inspectionWeight);
                smoothed = riderPosition + rightAxis * easedLocal.X + Vector3.Up * easedLocal.Y +
                           forwardAxis * easedLocal.Z;
            }
            else if (!blocked && !_snap)
            {
                smoothed = GlobalPosition.Lerp(safeTarget, weight);
            }
            // Sweep the camera's near-plane volume along its actual eased path as well
            // as the rider-to-camera boom. A point ray misses corner and near-plane clips.
            GlobalPosition = blocked || _snap
                ? safeTarget
                : ResolveMovementPosition(GlobalPosition, smoothed, safeTarget);
            if (blocked || _snap) _repeatedBlockedMovement = 0;
            if (!IsValidEndpoint(anchor, GlobalPosition))
            {
                // Re-query the actual resolved endpoint after path resolution.
                // If a seam/corner invalidated it, search back toward the clear
                // rider anchor in bounded steps rather than leaving an overlap.
                var recoveredDistance = safeDistance;
                var recoveredPosition = anchor + direction * recoveredDistance;
                while (recoveredDistance > 0 && !IsValidEndpoint(anchor, recoveredPosition))
                {
                    recoveredDistance = Math.Max(0, recoveredDistance - 0.25f);
                    recoveredPosition = anchor + direction * recoveredDistance;
                }
                GlobalPosition = recoveredPosition;
                _clearanceResult = "final-volume-recovered";
                _recoveryReason = "resolved-endpoint-failed-camera-volume-or-rider-visibility-check";
                _repeatedBlockedMovement++;
            }
        }
        RiderToCameraDistance = GlobalPosition.DistanceTo(riderPosition);
        _lookTarget = FitsVisualBounds && !_snap
            ? riderPosition + (_lookTarget - riderPosition).Lerp(lookTarget - riderPosition, inspectionWeight)
            : _lookTarget.Lerp(lookTarget, weight);
        LookAt(_lookTarget, Vector3.Up);
        if (FitsVisualBounds && hasVisualBounds)
            CenterProjectedBounds(_frameBoundsCorners, safeViewportRect);
        _snap = false;
    }

    private void CenterProjectedBounds(Vector3[] corners, Rect2 safeViewportRect)
    {
        var viewport = GetViewport().GetVisibleRect().Size;
        if (viewport.X <= 0 || viewport.Y <= 0) return;
        var safe = safeViewportRect.Size.X > .05f && safeViewportRect.Size.Y > .05f
            ? safeViewportRect
            : new Rect2(0, 0, 1, 1);
        var minimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var maximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        for (var i = 0; i < corners.Length; i++)
        {
            var projected = UnprojectPosition(corners[i]) / viewport;
            minimum = minimum.Min(projected);
            maximum = maximum.Max(projected);
        }

        // Center the projected visual envelope rather than only its 3D AABB
        // midpoint. A long bicycle sits at multiple depths, which shifts the
        // screen-space bounds center in side views. This correction is derived
        // from the current rider/bike projection and live HUD-safe rectangle.
        var projectedCenter = (minimum + maximum) * .5f;
        var error = projectedCenter - safe.GetCenter();
        if (error.LengthSquared() < 1e-8f) return;
        var center = Vector3.Zero;
        foreach (var corner in corners) center += corner;
        center /= corners.Length;
        var depth = Math.Max(.1f, GlobalPosition.DistanceTo(center));
        var tanVertical = Mathf.Tan(Mathf.DegToRad(Fov) * .5f);
        var tanHorizontal = tanVertical * viewport.X / viewport.Y;
        var cameraRight = GlobalTransform.Basis.X.Normalized();
        var cameraUp = GlobalTransform.Basis.Y.Normalized();
        _lookTarget += cameraRight * (error.X * 2 * depth * tanHorizontal)
                     + cameraUp * ((safe.GetCenter().Y - projectedCenter.Y) * 2 * depth * tanVertical);
        LookAt(_lookTarget, Vector3.Up);
    }

    private (System.Numerics.Vector3 Follow, System.Numerics.Vector3 Look) FitInspectionView(
        System.Numerics.Vector3 riderPosition, System.Numerics.Vector3 forward,
        Vector3[] corners, Rect2 safeRect)
    {
        var baseFollow = ChaseCameraMath.FollowPosition(riderPosition, forward, _view);
        var center = Vector3.Zero;
        foreach (var corner in corners) center += corner;
        center /= corners.Length;
        var cameraOffset = new Vector3(baseFollow.X, baseFollow.Y, baseFollow.Z) - center;
        if (cameraOffset.LengthSquared() < 1e-5f) cameraOffset = Vector3.Back * 4;
        var away = cameraOffset.Normalized();
        var towardView = -away;
        var right = towardView.Cross(Vector3.Up).Normalized();
        if (right.LengthSquared() < 1e-5f) right = Vector3.Right;
        var up = right.Cross(towardView).Normalized();
        var halfWidth = 0f;
        var halfHeight = 0f;
        for (var i = 0; i < corners.Length; i++)
        {
            var relative = corners[i] - center;
            halfWidth = Math.Max(halfWidth, Math.Abs(relative.Dot(right)));
            halfHeight = Math.Max(halfHeight, Math.Abs(relative.Dot(up)));
        }

        var viewport = GetViewport().GetVisibleRect().Size;
        var aspect = viewport.Y > 0 ? viewport.X / viewport.Y : 16f / 9f;
        var tanVertical = Mathf.Tan(Mathf.DegToRad(Fov) * .5f);
        var tanHorizontal = tanVertical * aspect;
        safeRect = safeRect.Size.X > .05f && safeRect.Size.Y > .05f ? safeRect : new Rect2(0, 0, 1, 1);
        var fitDistance = Math.Max(2 * halfHeight / (tanVertical * safeRect.Size.Y),
            2 * halfWidth / (tanHorizontal * safeRect.Size.X)) * 1.15f;
        var distance = Math.Max(cameraOffset.Length(), fitDistance);
        var safeCenter = safeRect.GetCenter();
        // Aim at the world point whose projection lands at the center of the largest
        // HUD-free rectangle. The offset is derived from viewport geometry and FOV.
        var target = center + right * ((.5f - safeCenter.X) * 2 * distance * tanHorizontal)
                    + up * ((safeCenter.Y - .5f) * 2 * distance * tanVertical);
        var position = center + away * distance;
        return (new System.Numerics.Vector3(position.X, position.Y, position.Z),
            new System.Numerics.Vector3(target.X, target.Y, target.Z));
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
            _recoveryReason = "rider-anchor-overlaps-collision-mesh; camera-held-at-anchor";
            // An anchor overlap means the source geometry intrudes on the rider
            // volume. Avoid sending the camera farther through it.
            return 0;
        }

        _clearanceQuery.Motion = direction * desiredDistance;
        var motion = state.CastMotion(_clearanceQuery);
        if (motion.Length < 2 || motion[0] >= 0.99999f)
        {
            if (IsValidEndpoint(anchor, target))
            {
                _clearanceResult = "clear";
                return desiredDistance;
            }
            SetQueryTransform(target);
            var endpointHit = state.IntersectShape(_clearanceQuery, 1);
            _obstructionSource = endpointHit.Count > 0 ? ColliderName(endpointHit[0]) : "camera-collision-mesh";
            var endpointValidatedDistance = desiredDistance;
            while (!IsValidEndpoint(anchor, anchor + direction * endpointValidatedDistance) && endpointValidatedDistance > 0)
                endpointValidatedDistance = Math.Max(0, endpointValidatedDistance - 0.25f);
            _clearanceResult = "boom-final-volume-retracted";
            _recoveryReason = "boom-endpoint-retracted-until-camera-volume-and-rider-sightline-are-clear";
            return endpointValidatedDistance;
        }

        var safeDistance = CameraObstructionMath.ResolveSafeDistance(desiredDistance,
            motion[0], CameraClearanceMeters);
        // Godot's cast fraction can leave a camera sphere touching an adjacent
        // triangle at chunk seams/corners. Validate the final volume and retract
        // in bounded increments until the actual destination is clear.
        var validatedDistance = safeDistance;
        while (!IsValidEndpoint(anchor, anchor + direction * validatedDistance) && validatedDistance > 0)
            validatedDistance = Math.Max(0, validatedDistance - 0.25f);
        var finalVolumeRetracted = validatedDistance + 0.001f < safeDistance;
        safeDistance = validatedDistance;
        SetQueryTransform(anchor + direction * Math.Min(desiredDistance, motion[1] * desiredDistance));
        var hit = state.IntersectShape(_clearanceQuery, 1);
        _obstructionSource = hit.Count > 0 ? ColliderName(hit[0]) : "camera-collision-mesh";
        _clearanceResult = finalVolumeRetracted ? "boom-final-volume-retracted" : "boom-clamped";
        if (finalVolumeRetracted)
            _recoveryReason = "boom-endpoint-retracted-until-camera-volume-and-rider-sightline-are-clear";
        else
            _recoveryReason = "rider-to-camera-boom-obstructed; camera-retracted-to-safe-distance";
        return safeDistance;
    }

    private bool IsClearAt(Vector3 position)
    {
        var state = GetSpaceState();
        if (state is null) return false;
        SetQueryTransform(position);
        return state.IntersectShape(_clearanceQuery, 1).Count == 0;
    }

    private bool IsValidEndpoint(Vector3 anchor, Vector3 position) =>
        IsClearAt(position) && HasLineOfSight(position, anchor);

    private bool HasLineOfSight(Vector3 from, Vector3 to)
    {
        var state = GetSpaceState();
        if (state is null) return false;
        var query = new PhysicsRayQueryParameters3D
        {
            From = from,
            To = to,
            CollisionMask = ObstructionCollisionMask,
            CollideWithAreas = false,
            CollideWithBodies = true
        };
        var hit = state.IntersectRay(query);
        if (hit.Count == 0) return true;
        _obstructionSource ??= ColliderName(hit);
        return false;
    }

    private Vector3 ResolveMovementPosition(Vector3 from, Vector3 to, Vector3 safeTarget)
    {
        var state = GetSpaceState();
        if (state is null || from.DistanceSquaredTo(to) <= 1e-6f)
        {
            _repeatedBlockedMovement = 0;
            return to;
        }
        SetQueryTransform(from);
        var startOverlaps = state.IntersectShape(_clearanceQuery, 1);
        if (startOverlaps.Count > 0)
        {
            _obstructionSource ??= ColliderName(startOverlaps[0]);
            _clearanceResult = "camera-overlap-recovered";
            _recoveryReason = "previous-camera-position-overlapped; used-current-rider-validated-boom-endpoint";
            _repeatedBlockedMovement++;
            // safeTarget was independently swept from the current rider anchor
            // by FindAllowedBoomDistance. It is current-frame validated, whereas
            // the old camera volume is allowed to overlap after a moving obstacle.
            return safeTarget;
        }
        var motionVector = to - from;
        _clearanceQuery.Motion = motionVector;
        var motion = state.CastMotion(_clearanceQuery);
        if (motion.Length < 2 || motion[0] >= 0.99999f)
        {
            _repeatedBlockedMovement = 0;
            return to;
        }
        _clearanceResult = "movement-recovered";
        _recoveryReason = "previous-to-current-camera-sweep-blocked; used-current-rider-validated-boom-endpoint";
        _repeatedBlockedMovement++;
        SetQueryTransform(from + motionVector * Math.Min(1, motion[1]));
        var hit = state.IntersectShape(_clearanceQuery, 1);
        _obstructionSource ??= hit.Count > 0 ? ColliderName(hit[0]) : "camera-collision-mesh";
        // Movement sweeps prevent the camera volume crossing scenery, but a
        // blocked historical path must not pin the camera to that old position.
        // safeTarget is already validated against the current rider's boom and
        // is the closest valid current-frame fallback.
        return safeTarget;
    }

    private PhysicsDirectSpaceState3D? GetSpaceState() => IsInsideTree() ? GetWorld3D().DirectSpaceState : null;

    private void SetQueryTransform(Vector3 position)
    {
        _clearanceQuery.Shape = _clearanceShape;
        _clearanceQuery.Transform = new Transform3D(Basis.Identity, position);
        _clearanceQuery.Motion = Vector3.Zero;
    }

    private static string? ColliderName(Godot.Collections.Dictionary result)
    {
        if (!result.TryGetValue("collider", out var value)) return null;
        return value.AsGodotObject() is Node node ? node.Name.ToString() : "camera-collision-mesh";
    }

}
