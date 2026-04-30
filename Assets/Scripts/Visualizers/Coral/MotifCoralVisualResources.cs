using UnityEngine;

public interface IMotifCoralVisualResources
{
    Texture2D OutlineNoise { get; }
    Texture2D FillNoise { get; }
}

public sealed class MotifCoralVisualResources : IMotifCoralVisualResources
{
    public Texture2D OutlineNoise { get; }
    public Texture2D FillNoise { get; }
    public MotifCoralVisualResources(Texture2D outlineNoise, Texture2D fillNoise) { OutlineNoise = outlineNoise; FillNoise = fillNoise; }
}
