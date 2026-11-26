using System;
using System.Collections.Generic;

namespace S3Ware.Guillotine
{
  /// <summary>
  /// A rectangular glass piece to cut. Length runs along the sheet length, width across it.
  /// </summary>
  public sealed class GuillotinePiece
  {
    public string Id { get; set; } = "";
    public double LengthMm { get; set; }
    public double WidthMm { get; set; }

    /// <summary>False when the piece must keep its direction, for example patterned glass such as Narrow Reed.</summary>
    public bool CanRotate { get; set; } = true;

    /// <summary>Wanted off the machine first: priority pieces fill the first sheets before any other piece is laid.</summary>
    public bool Priority { get; set; }

    /// <summary>
    /// The shape cut inside the rectangle, empty for a plain rectangle. X runs along the length from the left, Y along
    /// the width from the top. The rectangle is laid out as it is, trim allowance around the shape included,.
    /// </summary>
    public List<OutlineEdge> Outline { get; set; } = new List<OutlineEdge>();

    /// <summary>Straight scores across the breakout round the shape, in the same place as <see cref="Outline"/>; empty for none.</summary>
    public List<OutlineEdge> Scores { get; set; } = new List<OutlineEdge>();

    /// <summary>
    /// The piece's cutouts and polished sides for its picture, in the same place as <see cref="Outline"/> (in the
    /// rectangle for a plain one). Worked out when first asked for, so laying out does not make them. Null for none.
    /// </summary>
    public Lazy<PieceMarks> Marks { get; set; }

    /// <summary>
    /// Pieces with the same group are cut next to each other on one sheet, by <see cref="GroupLayout"/>, such as
    /// drawer fronts whose grain runs on from one to the next. Empty for a piece on its own.
    /// </summary>
    public string Group { get; set; } = "";

    /// <summary>The piece's slot number in <see cref="GroupLayout"/>, from 1.</summary>
    public int GroupSlot { get; set; }

    /// <summary>
    /// How the group's slots lie, in brackets: the outer brackets end to end along the length, the brackets inside
    /// them side by side across the width, and so on in turn. (1,2,3) is three end to end, ((1,2)) two side by side,
    /// ((1,2),(3,4)) two by two. Every piece of the group gives the same layout, or none for every slot end to end in
    /// slot order. The group turns only when every piece in it may.
    /// </summary>
    public string GroupLayout { get; set; } = "";

    /// <summary>The group this piece stands for while it is laid out.</summary>
    internal GuillotineBlock Block { get; set; }
  }
}
