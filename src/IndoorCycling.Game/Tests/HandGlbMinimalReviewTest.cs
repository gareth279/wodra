using System;
using System.IO;
using System.Threading.Tasks;
using Godot;

/// <summary>Loads the review GLB directly, without any Wodra rider or ride scripts.</summary>
public partial class HandGlbMinimalReviewTest : Node3D
{
    public override async void _Ready()
    {
        try
        {
            var model = GD.Load<PackedScene>("res://Assets/Models/WodraCyclist.glb")
                .Instantiate<Node3D>();
            AddChild(model);
            var animation = Find<AnimationPlayer>(model)
                ?? throw new InvalidOperationException("GLB has no imported AnimationPlayer.");
            var human = Find<MeshInstance3D>(model, "Human")
                ?? throw new InvalidOperationException("GLB has no Human mesh.");
            if (human.Mesh?.GetSurfaceCount() != 2)
                throw new InvalidOperationException($"Expected outfit and eye surfaces, got {human.Mesh?.GetSurfaceCount()}.");
            var neutral = new StandardMaterial3D { AlbedoColor = new Color(.58f, .36f, .25f),
                Roughness = .9f };
            human.MaterialOverride = neutral;

            var left = Find<Node3D>(model, "Contact_LeftHood")
                ?? throw new InvalidOperationException("Left imported hood contact missing from GLB.");
            var right = Find<Node3D>(model, "Contact_RightHood")
                ?? throw new InvalidOperationException("Right imported hood contact missing from GLB.");
            var target = (left.GlobalPosition + right.GlobalPosition) * .5f;
            var camera = new Camera3D { Fov = 38f, Near = .02f, Current = true };
            AddChild(camera);
            var environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(.12f, .14f, .17f), AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(.8f, .8f, .8f), AmbientLightEnergy = .8f };
            AddChild(new WorldEnvironment { Environment = environment });
            var light = new OmniLight3D { LightEnergy = 55f, OmniRange = 5f };
            AddChild(light);

            GD.Print($"[HandGlbMinimalReview] animation={string.Join(',', animation.GetAnimationList())}; " +
                     $"humanSurfaces={human.Mesh!.GetSurfaceCount()} hoodTarget={target} camera={camera.GlobalPosition}");
            animation.Play("PedalCycle");
            for (var i = 0; i < 90; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var output = Path.Combine(ProjectSettings.GlobalizePath("res://../.."), "artifacts",
                "alpha48-hand-correction", "godot-minimal-glb");
            Directory.CreateDirectory(output);
            foreach (var (hood, side, label) in new[] { (left, -1f, "left"), (right, 1f, "right") })
            {
                var hoodTarget = hood.GlobalPosition;
                var view = new Vector3(side * .90f, .20f, -.30f).Normalized();
                camera.GlobalPosition = hoodTarget + view * .68f;
                camera.LookAt(hoodTarget, Vector3.Up);
                light.GlobalPosition = camera.GlobalPosition;
                for (var i = 0; i < 3; i++)
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var image = GetViewport().GetTexture().GetImage();
                var result = image.SavePng(Path.Combine(output, $"glb-{label}-hand-playing.png"));
                if (result != Error.Ok) throw new IOException($"Could not write minimal GLB capture: {result}");
            }
            GD.Print($"[HandGlbMinimalReview] PASS; animation playing; output={output}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"[HandGlbMinimalReview] FAIL: {error}");
            GetTree().Quit(1);
        }
    }

    private static T? Find<T>(Node root, string? name = null) where T : Node
    {
        if (root is T match && (name is null || match.Name == name)) return match;
        foreach (var child in root.GetChildren())
            if (Find<T>(child, name) is { } result) return result;
        return null;
    }
}
