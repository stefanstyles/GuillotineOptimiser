using System;
using System.Collections.Generic;
using System.Linq;

namespace S3Ware.Guillotine
{
  /// <summary>
  /// One edge of a shaped piece's outline: a straight line, or an arc through <see cref="MidX"/>, <see cref="MidY"/>.
  /// Top-left origin, Y down, like the sheet.
  /// </summary>
  public sealed class OutlineEdge
  {
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }

    /// <summary>A point on the arc between its ends. Null for a straight edge.</summary>
    public double? MidX { get; set; }
    public double? MidY { get; set; }

    public double Radius { get; set; }

    public bool IsArc => MidX.HasValue && MidY.HasValue && Radius > 0;

    /// <summary>Clockwise as drawn, Y down: the arc turns right going from the start through the mid point to the end.</summary>
    public bool IsClockwise => IsArc && (MidX.Value - X1) * (Y2 - MidY.Value) - (MidY.Value - Y1) * (X2 - MidX.Value) > 0;

    /// <summary>More than half a circle: the mid point is further from the chord than the radius.</summary>
    public bool IsLargeArc
    {
      get
      {
        if (!IsArc)
          return false;
        double chordX = (X1 + X2) / 2, chordY = (Y1 + Y2) / 2;
        double rise = Math.Sqrt(Math.Pow(MidX.Value - chordX, 2) + Math.Pow(MidY.Value - chordY, 2));
        return rise > Radius + 0.001;
      }
    }

    public static OutlineEdge Line(double x1, double y1, double x2, double y2) =>
      new OutlineEdge { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    public static OutlineEdge Arc(double x1, double y1, double midX, double midY, double x2, double y2, double radius) =>
      new OutlineEdge { X1 = x1, Y1 = y1, MidX = midX, MidY = midY, X2 = x2, Y2 = y2, Radius = radius };

    /// <summary>The same edge run from its end to its start.</summary>
    public OutlineEdge Reversed() => IsArc ? Arc(X2, Y2, MidX.Value, MidY.Value, X1, Y1, Radius) : Line(X2, Y2, X1, Y1);

    internal OutlineEdge Mapped(Func<double, double, (double X, double Y)> map)
    {
      var start = map(X1, Y1);
      var end = map(X2, Y2);
      if (!IsArc)
        return Line(start.X, start.Y, end.X, end.Y);
      var mid = map(MidX.Value, MidY.Value);
      return Arc(start.X, start.Y, mid.X, mid.Y, end.X, end.Y, Radius);
    }
  }

  /// <summary>A mitred side on the piece picture: its edge and the mitre angle in degrees (always positive).</summary>
  public sealed class MitreMark
  {
    public OutlineEdge Edge { get; set; }
    public int Degrees { get; set; }

    internal MitreMark Mapped(Func<double, double, (double X, double Y)> map) =>
      new MitreMark { Edge = Edge.Mapped(map), Degrees = Degrees };
  }

  /// <summary>
  /// What a piece's picture shows besides its outline, in the same place as the outline: each cutout as its DXF cuts it,
  /// the sides polished seen and unseen, and mitred sides (front solid / back dashed).
  /// </summary>
  public sealed class PieceMarks
  {
    /// <summary>Each cutout's lines and arcs, one list per cutout.</summary>
    public List<List<OutlineEdge>> Cutouts { get; set; } = new List<List<OutlineEdge>>();
    public List<OutlineEdge> PolishedSeen { get; set; } = new List<OutlineEdge>();
    public List<OutlineEdge> PolishedUnseen { get; set; } = new List<OutlineEdge>();

    /// <summary>Front mitres (negative Mitre on the glass edge): solid line outside the piece on the label.</summary>
    public List<MitreMark> FrontMitres { get; set; } = new List<MitreMark>();

    /// <summary>Back mitres (positive Mitre on the glass edge): dashed line outside the piece on the label.</summary>
    public List<MitreMark> BackMitres { get; set; } = new List<MitreMark>();

    public bool IsEmpty => Cutouts.Count == 0 && PolishedSeen.Count == 0 && PolishedUnseen.Count == 0
      && FrontMitres.Count == 0 && BackMitres.Count == 0;

    internal PieceMarks Mapped(Func<double, double, (double X, double Y)> map) => new PieceMarks
    {
      Cutouts = Cutouts.Select(cutout => cutout.Select(edge => edge.Mapped(map)).ToList()).ToList(),
      PolishedSeen = PolishedSeen.Select(edge => edge.Mapped(map)).ToList(),
      PolishedUnseen = PolishedUnseen.Select(edge => edge.Mapped(map)).ToList(),
      FrontMitres = FrontMitres.Select(mark => mark.Mapped(map)).ToList(),
      BackMitres = BackMitres.Select(mark => mark.Mapped(map)).ToList(),
    };
  }

  /// <summary>The outline of one shaped piece where it lies on the sheet.</summary>
  public sealed class PlacedShape
  {
    public string Id { get; set; } = "";

    /// <summary>Index of the piece in <see cref="GuillotineSheet.Pieces"/>.</summary>
    public int PieceIndex { get; set; }

    public List<OutlineEdge> Edges { get; set; } = new List<OutlineEdge>();

    /// <summary>Straight scores across the breakout round the shape, where it lies on the sheet.</summary>
    public List<OutlineEdge> Scores { get; set; } = new List<OutlineEdge>();
  }
}
