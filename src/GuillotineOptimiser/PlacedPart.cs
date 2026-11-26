using System;
using System.Collections.Generic;
using System.Linq;

namespace S3Ware.Guillotine
{
  /// <summary>
  /// A part placed on a sheet (top-left origin, axis-aligned bounds).
  /// Width/Height are the placed AABB. RotationDegrees is from the part's original orientation (0 = none); the
  /// guillotine layout uses 0 or 90.
  /// </summary>
  public sealed class PlacedPart
  {
    public string Id { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double RotationDegrees { get; set; }

    /// <summary>
    /// A shaped part's outline in its own length (across) × width (down) box, top-left origin, Y down, before it is
    /// turned; empty for a rectangle.
    /// </summary>
    public List<OutlineEdge> Outline { get; set; } = new List<OutlineEdge>();

    /// <summary>The outline where the part lies on the sheet (turned when <see cref="RotationDegrees"/> is 90); empty for a rectangle.</summary>
    public List<OutlineEdge> OutlineOnSheet()
    {
      if (Outline == null || Outline.Count == 0)
        return new List<OutlineEdge>();
      bool turned = Math.Abs(RotationDegrees) > 0.001;
      return Outline.Select(edge => edge.Mapped((x, y) => turned ? (X + Width - y, Y + x) : (X + x, Y + y))).ToList();
    }

    /// <summary>A spare piece placed into a leftover after the main layout, not one of the pieces given.</summary>
    public bool IsFiller { get; set; }

    /// <summary>The id of the filled leftover (in <see cref="GuillotineSheet.FilledLeftovers"/>) a fill piece was laid into by guillotine cuts; empty otherwise.</summary>
    public string FilledInto { get; set; } = "";

    /// <summary>A priority part: laid on the first sheets.</summary>
    public bool Priority { get; set; }

    public PlacedPart Copy() => (PlacedPart)MemberwiseClone();
  }

  /// <summary>
  /// One unused rectangle on a sheet. Same top-left origin as <see cref="PlacedPart"/>.
  /// </summary>
  public sealed class SheetGap
  {
    public string Id { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>
    /// The level of the guillotine cut that made this leftover, from 0 (the cuts across the whole sheet). A fill by
    /// guillotine cuts is cut on from that level. Null when the gap is not a guillotine leftover.
    /// </summary>
    public int? CutLevel { get; set; }

    /// <summary>The cut that made it runs down the sheet (constant X).</summary>
    public bool CutVertical { get; set; }

    /// <summary>The id of the filled leftover (in FilledLeftovers) this is what is left of; empty otherwise.</summary>
    public string FilledInto { get; set; } = "";

    public SheetGap Copy() => (SheetGap)MemberwiseClone();
  }
}
