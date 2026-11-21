// The water level changed in the map editor (docs/MAP_EDITOR.md "Water"): no grass below the new level.
namespace PwRemake.View.Terrain;

public partial class GrassLayer
{
    /// <summary>the water level the grass keeps above (null: none); Invalidate afterwards</summary>
    public double? WaterLevel { get => _water; set => _water = value; }
}
