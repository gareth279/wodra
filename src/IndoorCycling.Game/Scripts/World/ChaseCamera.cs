using System;
using Godot;

public partial class ChaseCamera : Camera3D
{
    private Vector3 _lookTarget;
    private bool _snap = true;

    public ChaseCamera()
    {
        Fov = 65;
        Near = 0.1f;
        Far = 4000;
    }

    public void ResetFollow() => _snap = true;

    public void Follow(Vector3 riderPosition, Vector3 forward, double delta)
    {
        var targetPosition = riderPosition - forward * 8 + Vector3.Up * 4;
        var lookTarget = riderPosition + forward * 6 + Vector3.Up;
        var weight = _snap ? 1 : (float)(1 - Math.Exp(-6 * delta));
        GlobalPosition = GlobalPosition.Lerp(targetPosition, weight);
        _lookTarget = _lookTarget.Lerp(lookTarget, weight);
        LookAt(_lookTarget, Vector3.Up);
        _snap = false;
    }
}
