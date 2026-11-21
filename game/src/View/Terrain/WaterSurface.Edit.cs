// The water level changed in the map editor (docs/MAP_EDITOR.md "Water"): the plane moved, the depth texture again.
namespace PwRemake.View.Terrain;

public partial class WaterSurface
{
    /// <summary>the water's height (m)</summary>
    public double Level => _level;

    /// <summary>moves the water to `level` and makes its depth texture again</summary>
    public void SetLevel(double level)
    {
        _level = level;
        Position = new Godot.Vector3(Position.X, (float)level, Position.Z);
        RefreshDepth();
    }
}
