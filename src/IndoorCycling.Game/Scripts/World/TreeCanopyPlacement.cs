/// <summary>Shared canopy placement math; canopy bottoms overlap the top of tree trunks.</summary>
public static class TreeCanopyPlacement
{
    public const float TrunkTopHeightFraction = .30f;
    public const float JoinOverlapHeightFraction = .02f;

    public static float CenterY(double treeHeightMeters, float canopyScaleY, float meshHeight)
    {
        if (!double.IsFinite(treeHeightMeters) || treeHeightMeters <= 0
            || !float.IsFinite(canopyScaleY) || canopyScaleY <= 0
            || !float.IsFinite(meshHeight) || meshHeight <= 0)
            throw new System.ArgumentOutOfRangeException(nameof(treeHeightMeters));

        var canopyHalfHeight = meshHeight * .5f * canopyScaleY;
        return (float)(treeHeightMeters * (TrunkTopHeightFraction - JoinOverlapHeightFraction))
            + canopyHalfHeight;
    }

    public static (float Bottom, float Top) CanopyBounds(double treeHeightMeters,
        float canopyScaleY, float meshHeight)
    {
        var center = CenterY(treeHeightMeters, canopyScaleY, meshHeight);
        var halfHeight = meshHeight * .5f * canopyScaleY;
        return (center - halfHeight, center + halfHeight);
    }
}
