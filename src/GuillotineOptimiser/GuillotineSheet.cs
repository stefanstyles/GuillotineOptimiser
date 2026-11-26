using System;
using System.Collections.Generic;
using System.Linq;

namespace S3Ware.Guillotine
{
  /// <summary>
  /// One stock sheet laid out for a glass cutting table. Top-left origin; length runs left to right.
  /// </summary>
  public sealed class GuillotineSheet
  {
    private const double Tolerance = 0.5;

    public string BoardName { get; set; } = "";
    public double LengthMm { get; set; }
    public double WidthMm { get; set; }

    /// <summary>
    /// The trim the layout was cut inside. The operator breaks the front (top) trim off first, then the strips from the
    /// left trim on, so a leftover against the front or left trim is a strip of its own. The back (bottom) trim comes off
    /// with each strip's last row (cut or not, <see cref="BackTrimCut"/>), and the right trim is not written to the
    /// table, so a leftover against either breaks off with it (<see cref="CutsAsBroken"/>).
    /// </summary>
    public SheetTrim Trim { get; set; } = SheetTrim.None();

    /// <summary>
    /// The table cuts the back (bottom) trim (the machine's Cut the back trim). Off, the back trim only keeps the pieces
    /// off the back edge: it is not written to the table and comes off as waste with each strip's last row.
    /// </summary>
    public bool BackTrimCut { get; set; }

    public List<PlacedPart> Pieces { get; set; } = new List<PlacedPart>();

    /// <summary>The leftovers, each with the cut that made it (<see cref="SheetGap.CutLevel"/>), so fill can be cut into it.</summary>
    public List<SheetGap> Offcuts { get; set; } = new List<SheetGap>();

    /// <summary>Guillotine cuts in cutting order, trim cuts excluded.</summary>
    public List<GuillotineCut> Cuts { get; set; } = new List<GuillotineCut>();

    /// <summary>The leftovers fill pieces were laid into, as they were before: deleting the fill gives them back.</summary>
    public List<SheetGap> FilledLeftovers { get; set; } = new List<SheetGap>();

    internal int BoardIndex { get; set; }

    /// <summary>The piece each entry in <see cref="Pieces"/> was placed from, same index.</summary>
    internal List<GuillotinePiece> SourcePieces { get; set; } = new List<GuillotinePiece>();

    /// <summary>
    /// The outline of each shaped piece in sheet coordinates. A piece turned 90° has its length down the sheet, the
    /// outline's left edge at the top.
    /// </summary>
    public List<PlacedShape> Shapes()
    {
      var shapes = new List<PlacedShape>();
      for (int i = 0; i < Pieces.Count && i < SourcePieces.Count; i++)
      {
        var source = SourcePieces[i];
        if (source?.Outline == null || source.Outline.Count == 0)
          continue;
        var part = Pieces[i];
        var map = OnSheet(part);
        shapes.Add(new PlacedShape
        {
          Id = part.Id,
          PieceIndex = i,
          Edges = source.Outline.Select(edge => edge.Mapped(map)).ToList(),
          Scores = (source.Scores ?? new List<OutlineEdge>()).Select(edge => edge.Mapped(map)).ToList()
        });
      }
      return shapes;
    }

    /// <summary>Each placed piece's cutouts and polished sides in sheet coordinates, by index in <see cref="Pieces"/>; a piece with none is left out.</summary>
    public Dictionary<int, PieceMarks> Marks()
    {
      var marks = new Dictionary<int, PieceMarks>();
      for (int i = 0; i < Pieces.Count && i < SourcePieces.Count; i++)
      {
        var own = SourcePieces[i]?.Marks?.Value;
        if (own != null && !own.IsEmpty)
          marks[i] = own.Mapped(OnSheet(Pieces[i]));
      }
      return marks;
    }

    /// <summary>From the piece's own coordinates to the sheet's. A piece turned 90° has its length down the sheet.</summary>
    private static Func<double, double, (double X, double Y)> OnSheet(PlacedPart part)
    {
      bool turned = Math.Abs(part.RotationDegrees) > 0.001;
      return (x, y) => turned ? (part.X + part.Width - y, part.Y + x) : (part.X + x, part.Y + y);
    }

    /// <summary>
    /// <see cref="Cuts"/> as the operator breaks them out, for drawing: the front trim is off first, then each strip
    /// comes off with its piece of the back trim, so a strip cut runs on through the back trim to the back edge. On a
    /// turned sheet (strips along the front) a strip cut runs through the left trim and on to the right edge.
    /// </summary>
    public List<GuillotineCut> CutsAsBroken()
    {
      var trim = Trim ?? SheetTrim.None();
      return Cuts.Select(cut =>
      {
        var broken = cut.Copy();
        if (cut.Level != 0)
          return broken;
        if (cut.IsVertical)
        {
          if (broken.Y2 >= broken.Y1 && broken.Y2 >= WidthMm - trim.BottomMm - Tolerance)
            broken.Y2 = WidthMm;
          else if (broken.Y1 > broken.Y2 && broken.Y1 >= WidthMm - trim.BottomMm - Tolerance)
            broken.Y1 = WidthMm;
          return broken;
        }
        (double from, double to) = broken.X1 <= broken.X2 ? (broken.X1, broken.X2) : (broken.X2, broken.X1);
        if (from <= trim.LeftMm + Tolerance)
          from = 0;
        if (to >= LengthMm - trim.RightMm - Tolerance)
          to = LengthMm;
        (broken.X1, broken.X2) = (from, to);
        return broken;
      }).ToList();
    }

    /// <summary>
    /// The trim cuts as the operator breaks them, for drawing: the front (top) trim edge to edge, first; the left trim
    /// from it to the back edge; when <see cref="BackTrimCut"/>, the back (bottom) trim from the left trim to the right
    /// edge, broken off each strip with its last row. The right trim is not written to the table, so it has no cut.
    /// </summary>
    public List<GuillotineCut> TrimCuts()
    {
      var trim = Trim ?? SheetTrim.None();
      var cuts = new List<GuillotineCut>();
      if (trim.TopMm > 0)
        cuts.Add(new GuillotineCut { X1 = 0, Y1 = trim.TopMm, X2 = LengthMm, Y2 = trim.TopMm });
      if (trim.LeftMm > 0)
        cuts.Add(new GuillotineCut { X1 = trim.LeftMm, Y1 = trim.TopMm, X2 = trim.LeftMm, Y2 = WidthMm });
      if (BackTrimCut && trim.BottomMm > 0)
        cuts.Add(new GuillotineCut { X1 = trim.LeftMm, Y1 = WidthMm - trim.BottomMm, X2 = LengthMm, Y2 = WidthMm - trim.BottomMm });
      return cuts;
    }

    /// <summary>
    /// Each break named in the order the operator breaks it out, for a printout in black and white: T1, T2, T3 the
    /// <see cref="TrimCuts"/> in order; X1, X2... the strips from the operator's end (left; on a turned sheet the front);
    /// in each strip Y1, Y2... from its start, and in each row Z1, Z2... then W1... the same way. A trim's label is in
    /// the middle of its line; a cut's sits a little in from the end the operator starts at.
    /// </summary>
    public List<BreakLabel> BreakLabels()
    {
      var labels = new List<BreakLabel>();
      int number = 0;
      foreach (var cut in TrimCuts())
        labels.Add(new BreakLabel("T" + ++number, (cut.X1 + cut.X2) / 2, (cut.Y1 + cut.Y2) / 2));
      if (Cuts.Count == 0)
        return labels;
      var stripCut = Cuts.FirstOrDefault(cut => cut.Level == 0);
      bool turned = stripCut != null ? !stripCut.IsVertical : Cuts.FirstOrDefault(cut => cut.Level == 1)?.IsVertical ?? false;
      var trim = Trim ?? SheetTrim.None();
      Label(new BreakoutArea(trim.LeftMm, trim.TopMm, LengthMm - trim.RightMm, WidthMm - trim.BottomMm), 0, Cuts.Max(cut => cut.Level), turned, labels);
      return labels;
    }

    private const string LevelLetters = "XYZWVU";

    /// <summary>The cuts of <paramref name="level"/> across <paramref name="area"/> numbered from its start, then the sections between them.</summary>
    private void Label(BreakoutArea area, int level, int deepest, bool turned, List<BreakLabel> labels)
    {
      if (level > deepest)
        return;
      bool vertical = (level % 2 == 0) != turned;
      var (sections, between) = Sections(area, level, vertical);
      string letter = LevelLetters[Math.Min(level, LevelLetters.Length - 1)].ToString();
      for (int i = 0; i < between.Count; i++)
      {
        if (between[i].Count > 0)
          labels.Add(LabelOn(between[i][0], letter + (i + 1)));
      }
      foreach (var section in sections)
        Label(section.Area, level + 1, deepest, turned, labels);
    }

    /// <summary>A label on the cut, in from its start (its top or left end) by up to 90 mm, at most half its length.</summary>
    private static BreakLabel LabelOn(GuillotineCut cut, string text)
    {
      if (cut.IsVertical)
      {
        double from = Math.Min(cut.Y1, cut.Y2), length = Math.Abs(cut.Y2 - cut.Y1);
        return new BreakLabel(text, cut.X1, from + Math.Min(90, length / 2));
      }
      double start = Math.Min(cut.X1, cut.X2), along = Math.Abs(cut.X2 - cut.X1);
      return new BreakLabel(text, start + Math.Min(90, along / 2), cut.Y1);
    }

    /// <summary>Cut stages the table needs: the deepest cut level plus one.</summary>
    public int Stages => Cuts.Count == 0 ? 0 : Cuts.Max(cut => cut.Level) + 1;

    public double FillRatio
    {
      get
      {
        double area = LengthMm * WidthMm;
        return area > 0 ? Pieces.Sum(piece => piece.Width * piece.Height) / area : 0;
      }
    }

    public double AreaM2 => LengthMm * WidthMm / 1e6;

    /// <summary>Area of the pieces cut, or of the fill pieces laid into leftovers, in m².</summary>
    public double PieceAreaM2(bool filler) =>
      Pieces.Where(piece => piece.IsFiller == filler).Sum(piece => piece.Width * piece.Height) / 1e6;

    /// <summary>Room inside the trims that no piece covers, in m².</summary>
    public double SpareM2 => Math.Max(0, AreaM2 - TrimAreaM2 - Pieces.Sum(piece => piece.Width * piece.Height) / 1e6);

    /// <summary>The strips the trim cuts take off the edges, in m².</summary>
    public double TrimAreaM2 =>
      (LengthMm * WidthMm
        - Math.Max(0, LengthMm - Trim.LeftMm - Trim.RightMm) * Math.Max(0, WidthMm - Trim.TopMm - Trim.BottomMm)) / 1e6;

    /// <summary>
    /// The clear end of the sheet past its last piece, trim included: the whole width past the last piece along the
    /// length, or the whole length below the last piece, whichever is larger. The whole sheet when it has no pieces.
    /// </summary>
    public (double LengthMm, double WidthMm) Remnant()
    {
      if (Pieces.Count == 0)
        return (LengthMm, WidthMm);
      double alongLength = Math.Max(0, LengthMm - Pieces.Max(piece => piece.X + piece.Width));
      double alongWidth = Math.Max(0, WidthMm - Pieces.Max(piece => piece.Y + piece.Height));
      return alongLength * WidthMm >= LengthMm * alongWidth ? (alongLength, WidthMm) : (LengthMm, alongWidth);
    }

    /// <summary>
    /// Lays a new layout out to break out at the table, cutting the same pieces the same way. Strips and pieces along a
    /// strip are ordered by size: by default narrowest/shortest toward the origin (glass). When
    /// <paramref name="shortestAtFarEnd"/> is set (beam saw), widest/longest stay at the origin so the shorter length
    /// is at the far end — the saw takes the right-hand side first, so the smaller piece comes off first for easier handling.
    /// Remnant stays past the last strip. Inside a strip, rows also prefer fewest deeper breaks first.
    /// <paramref name="topDown"/> lays the sheet out from the back of the table (the top of
    /// the printout): at every cut level that runs across the table's depth, the leftover past the last piece moves to
    /// the front, so it breaks off on the operator's side. A leftover beside pieces that all stand back from the cut has
    /// the cut moved so it and their thinnest gap break off the same width. A change that would leave an offcut the
    /// table cannot break off is not made. Only with no gap between pieces (glass) and before any fill.
    /// </summary>
    public void ArrangeForBreakout(bool topDown, double minBreakDistanceMm, bool shortestAtFarEnd = false)
    {
      var stripCut = Cuts.FirstOrDefault(cut => cut.Level == 0);
      var rowCut = Cuts.FirstOrDefault(cut => cut.Level == 1);
      if (Pieces.Count == 0 || (stripCut == null && rowCut == null))
        return;
      bool turned = stripCut != null ? !stripCut.IsVertical : rowCut!.IsVertical;
      var trim = Trim ?? SheetTrim.None();
      var inside = new BreakoutArea(trim.LeftMm, trim.TopMm, LengthMm - trim.RightMm, WidthMm - trim.BottomMm);
      Arrange(inside, 0, Cuts.Max(cut => cut.Level), turned, topDown, minBreakDistanceMm, shortestAtFarEnd);
    }

    /// <summary>
    /// Arranges the sections the cuts of <paramref name="level"/> make in <paramref name="area"/>, then the sections
    /// inside each. Cuts at an even level run down the sheet (constant X), on a turned sheet across it.
    /// </summary>
    private void Arrange(
      BreakoutArea area, int level, int deepest, bool turned, bool topDown, double minBreakDistanceMm, bool shortestAtFarEnd)
    {
      if (level > deepest)
        return;
      bool vertical = (level % 2 == 0) != turned;
      var (sections, between) = Sections(area, level, vertical);
      var order = sections.ToList();
      if (level == 0 && sections.SkipLast(1).All(section => section.Parts.Count > 0))
      {
        var filled = sections.Where(section => section.Parts.Count > 0);
        order = (shortestAtFarEnd
            ? filled.OrderByDescending(section => Math.Round(section.Size, 1))
            : filled.OrderBy(section => Math.Round(section.Size, 1)))
          .Concat(sections.Where(section => section.Parts.Count == 0)).ToList();
      }
      else if (level > 0)
        order = FewestBreaksFirst(sections, shortestAtFarEnd);
      if (topDown && !vertical && order.Count > 1 && order[^1].Parts.Count == 0)
      {
        order.Insert(0, order[^1]);
        order.RemoveAt(order.Count - 1);
      }
      if (!order.SequenceEqual(sections))
        KeepIfBreakable(() => LayOut(sections[0].Start, order, between, vertical), minBreakDistanceMm);
      var laid = Sections(area, level, vertical).Sections;
      for (int i = 0; i + 1 < laid.Count; i++)
        Balance(area, laid[i], laid[i + 1], vertical, minBreakDistanceMm);
      foreach (var section in Sections(area, level, vertical).Sections.Where(section => section.Parts.Count > 0))
        Arrange(section.Area, level + 1, deepest, turned, topDown, minBreakDistanceMm, shortestAtFarEnd);
    }

    /// <summary>
    /// The sections with pieces in the order of the breaks left inside them, fewest first, so what comes off whole is
    /// broken out before what still needs breaking up; among those needing as many breaks, ordered by size (shortest
    /// toward the origin, or toward the far end when <paramref name="shortestAtFarEnd"/>). Leftovers keep their places.
    /// </summary>
    private static List<BreakoutSection> FewestBreaksFirst(List<BreakoutSection> sections, bool shortestAtFarEnd)
    {
      var filled = sections.Where(section => section.Parts.Count > 0)
        .OrderBy(section => section.Cuts.Count);
      filled = shortestAtFarEnd
        ? filled.ThenByDescending(section => Math.Round(section.Size, 1))
        : filled.ThenBy(section => Math.Round(section.Size, 1));
      var queue = new Queue<BreakoutSection>(filled);
      return sections.Select(section => section.Parts.Count > 0 ? queue.Dequeue() : section).ToList();
    }

    /// <summary>
    /// A leftover beside a section whose pieces all stand back from the cut between them: the cut moves into the
    /// leftover until it and the thinnest gap left in the section break off the same width, up to twice the break
    /// distance, so neither is a sliver. A piece flush with the cut keeps it where it is.
    /// </summary>
    private void Balance(BreakoutArea area, BreakoutSection first, BreakoutSection second, bool vertical, double minBreakDistanceMm)
    {
      bool leftoverFirst = first.Parts.Count == 0 && second.Parts.Count > 0;
      if (!leftoverFirst && !(second.Parts.Count == 0 && first.Parts.Count > 0))
        return;
      var (leftover, section) = leftoverFirst ? (first, second) : (second, first);
      double line = second.Start;
      double Low(PlacedPart part) => vertical ? part.X : part.Y;
      double High(PlacedPart part) => vertical ? part.X + part.Width : part.Y + part.Height;
      double gap = section.Parts.Min(part => leftoverFirst ? Low(part) - line : line - High(part));
      if (gap <= Tolerance)
        return;
      double by = Math.Min((leftover.Size + gap) / 2, 2 * minBreakDistanceMm) - gap;
      if (by <= Tolerance)
        return;
      double to = leftoverFirst ? line - by : line + by;
      KeepIfBreakable(() => MoveLine(area, vertical, line, to), minBreakDistanceMm);
    }

    /// <summary>
    /// Moves the cut at <paramref name="from"/> across <paramref name="area"/> to <paramref name="to"/>, with the ends of
    /// the cuts and the edges of the leftovers on it. No piece may touch it.
    /// </summary>
    private void MoveLine(BreakoutArea area, bool vertical, double from, double to)
    {
      double Moved(double at) => Math.Abs(at - from) < Tolerance ? to : at;
      foreach (var cut in Cuts.Where(area.Holds))
      {
        if (vertical)
          (cut.X1, cut.X2) = (Moved(cut.X1), Moved(cut.X2));
        else
          (cut.Y1, cut.Y2) = (Moved(cut.Y1), Moved(cut.Y2));
      }
      foreach (var gap in Offcuts.Where(area.Holds).ToList())
      {
        double low = vertical ? gap.X : gap.Y, high = low + (vertical ? gap.Width : gap.Height);
        (low, high) = (Moved(low), Moved(high));
        if (vertical)
          (gap.X, gap.Width) = (low, high - low);
        else
          (gap.Y, gap.Height) = (low, high - low);
      }
    }

    /// <summary>Makes <paramref name="change"/>, and puts the sheet back when that leaves more offcuts the table cannot break off.</summary>
    private void KeepIfBreakable(Action change, double minBreakDistanceMm)
    {
      int unbreakable = FindUnbreakableOffcuts(minBreakDistanceMm).Count;
      var parts = Pieces.Select(part => (part, part.X, part.Y)).ToList();
      var cuts = Cuts.Select(cut => (cut, cut.X1, cut.Y1, cut.X2, cut.Y2)).ToList();
      var gaps = Offcuts.Select(gap => (gap, gap.X, gap.Y, gap.Width, gap.Height)).ToList();
      change();
      if (FindUnbreakableOffcuts(minBreakDistanceMm).Count <= unbreakable)
        return;
      foreach (var (part, x, y) in parts)
        (part.X, part.Y) = (x, y);
      foreach (var (cut, x1, y1, x2, y2) in cuts)
        (cut.X1, cut.Y1, cut.X2, cut.Y2) = (x1, y1, x2, y2);
      foreach (var (gap, x, y, width, height) in gaps)
        (gap.X, gap.Y, gap.Width, gap.Height) = (x, y, width, height);
    }

    /// <summary>The sections between the cuts of <paramref name="level"/> across <paramref name="area"/>, and the cuts between each two.</summary>
    private (List<BreakoutSection> Sections, List<List<GuillotineCut>> Between) Sections(BreakoutArea area, int level, bool vertical)
    {
      double start = vertical ? area.Left : area.Top, end = vertical ? area.Right : area.Bottom;
      double At(GuillotineCut cut) => vertical ? cut.X1 : cut.Y1;
      var cuts = Cuts.Where(cut => cut.Level == level && cut.IsVertical == vertical && area.Holds(cut)
        && At(cut) > start + Tolerance && At(cut) < end - Tolerance).ToList();
      var bounds = new List<double> { start };
      foreach (double at in cuts.Select(At).OrderBy(at => at))
      {
        if (at - bounds[^1] > Tolerance)
          bounds.Add(at);
      }
      bounds.Add(end);
      var sections = new List<BreakoutSection>();
      var between = new List<List<GuillotineCut>>();
      for (int i = 0; i + 1 < bounds.Count; i++)
      {
        var box = vertical
          ? new BreakoutArea(bounds[i], area.Top, bounds[i + 1], area.Bottom)
          : new BreakoutArea(area.Left, bounds[i], area.Right, bounds[i + 1]);
        sections.Add(new BreakoutSection(this, box, level, vertical));
        if (i + 2 < bounds.Count)
          between.Add(cuts.Where(cut => Math.Abs(At(cut) - bounds[i + 1]) < Tolerance).ToList());
      }
      return (sections, between);
    }

    /// <summary>Lays the sections out from <paramref name="start"/> in <paramref name="order"/>, with the cuts between them.</summary>
    private void LayOut(double start, List<BreakoutSection> order, List<List<GuillotineCut>> between, bool vertical)
    {
      double at = start;
      for (int i = 0; i < order.Count; i++)
      {
        order[i].Shift(at - order[i].Start);
        at += order[i].Size;
        if (i < between.Count)
        {
          foreach (var cut in between[i])
          {
            if (vertical)
              cut.X1 = cut.X2 = at;
            else
              cut.Y1 = cut.Y2 = at;
          }
        }
      }
    }

    /// <summary>Part of the sheet, edges in sheet coordinates.</summary>
    private readonly record struct BreakoutArea(double Left, double Top, double Right, double Bottom)
    {
      private bool Holds(double x1, double y1, double x2, double y2) =>
        Math.Min(x1, x2) >= Left - Tolerance && Math.Max(x1, x2) <= Right + Tolerance
        && Math.Min(y1, y2) >= Top - Tolerance && Math.Max(y1, y2) <= Bottom + Tolerance;

      public bool Holds(PlacedPart part) => Holds(part.X, part.Y, part.X + part.Width, part.Y + part.Height);
      public bool Holds(SheetGap gap) => Holds(gap.X, gap.Y, gap.X + gap.Width, gap.Y + gap.Height);
      public bool Holds(GuillotineCut cut) => Holds(cut.X1, cut.Y1, cut.X2, cut.Y2);
    }

    /// <summary>A section between two cuts of one level: its pieces, and the deeper cuts and leftovers in it, moved together.</summary>
    private sealed class BreakoutSection
    {
      private readonly bool _vertical;

      public BreakoutSection(GuillotineSheet sheet, BreakoutArea area, int level, bool vertical)
      {
        _vertical = vertical;
        Area = area;
        Start = vertical ? area.Left : area.Top;
        Size = vertical ? area.Right - area.Left : area.Bottom - area.Top;
        Parts = sheet.Pieces.Where(area.Holds).ToList();
        Cuts = sheet.Cuts.Where(cut => cut.Level > level && area.Holds(cut)).ToList();
        Offcuts = sheet.Offcuts.Where(area.Holds).ToList();
      }

      public BreakoutArea Area { get; }
      public double Start { get; }
      public double Size { get; }
      public List<PlacedPart> Parts { get; }
      public List<GuillotineCut> Cuts { get; }
      public List<SheetGap> Offcuts { get; }

      /// <summary>Moves everything in the section <paramref name="by"/> across its level's cuts.</summary>
      public void Shift(double by)
      {
        double dx = _vertical ? by : 0, dy = _vertical ? 0 : by;
        foreach (var part in Parts)
        {
          part.X += dx;
          part.Y += dy;
        }
        foreach (var cut in Cuts)
        {
          cut.X1 += dx;
          cut.X2 += dx;
          cut.Y1 += dy;
          cut.Y2 += dy;
        }
        foreach (var gap in Offcuts)
        {
          gap.X += dx;
          gap.Y += dy;
        }
      }
    }

    /// <summary>
    /// Offcuts the table cannot break off: thinner than <paramref name="minBreakDistanceMm"/> and held by a cut
    /// on both sides, or by a cut and an untrimmed sheet edge. The front and left trims break off first, so they hold an
    /// offcut as a cut does. A thin offcut against the back or right trim breaks off with it when the two together reach
    /// the break distance. A side with no cut joins the next offcut.
    /// </summary>
    public List<SheetGap> FindUnbreakableOffcuts(double minBreakDistanceMm)
    {
      return Offcuts
        .Where(offcut => offcut != null && IsUnbreakable(offcut, minBreakDistanceMm))
        .ToList();
    }

    public bool IsBreakable(double minBreakDistanceMm) => FindUnbreakableOffcuts(minBreakDistanceMm).Count == 0;

    private bool IsUnbreakable(SheetGap offcut, double minBreakDistanceMm)
    {
      if (offcut.Width < minBreakDistanceMm - 0.001
        && !BreaksOff(true, offcut.X, offcut.X + offcut.Width, offcut.Y, offcut.Y + offcut.Height, offcut.Width, minBreakDistanceMm))
        return true;
      return offcut.Height < minBreakDistanceMm - 0.001
        && !BreaksOff(false, offcut.Y, offcut.Y + offcut.Height, offcut.X, offcut.X + offcut.Width, offcut.Height, minBreakDistanceMm);
    }

    /// <summary>
    /// The offcut lies between lines at <paramref name="low"/> and <paramref name="high"/>, which run vertically
    /// (constant X) or horizontally (constant Y) from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    private bool BreaksOff(bool vertical, double low, double high, double from, double to, double thickness, double minBreakDistanceMm)
    {
      var lowSide = SideAt(vertical, low, from, to);
      var highSide = SideAt(vertical, high, from, to);
      if (lowSide.Kind == SideKind.Open || highSide.Kind == SideKind.Open)
        return true;
      return highSide.Kind == SideKind.Trim && thickness + highSide.TrimMm >= minBreakDistanceMm - 0.001;
    }

    private (SideKind Kind, double TrimMm) SideAt(bool vertical, double position, double from, double to)
    {
      double size = vertical ? LengthMm : WidthMm;
      double lowTrim = vertical ? Trim.LeftMm : Trim.TopMm;
      double highTrim = vertical ? Trim.RightMm : Trim.BottomMm;
      if (position <= Tolerance || position >= size - Tolerance)
        return (SideKind.Edge, 0);
      if (lowTrim > 0 && Math.Abs(position - lowTrim) < Tolerance)
        return (SideKind.Cut, 0);
      if (highTrim > 0 && Math.Abs(position - (size - highTrim)) < Tolerance)
        return (SideKind.Trim, highTrim);
      return Cuts.Any(cut => cut.Runs(vertical, position, from, to, Tolerance))
        ? (SideKind.Cut, 0)
        : (SideKind.Open, 0);
    }

    private enum SideKind
    {
      Edge,
      Trim,
      Cut,
      Open
    }
  }

  /// <summary>A break's name (T1, X2, Y1...) where it is printed on the sheet, in sheet mm.</summary>
  public sealed record BreakLabel(string Text, double X, double Y);

  /// <summary>
  /// One straight guillotine cut. Level 0 cuts run the whole sheet (strips), level 1 cuts run a strip, and so on.
  /// </summary>
  public sealed class GuillotineCut
  {
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public int Level { get; set; }

    /// <summary>The id of the filled leftover a fill cut was made in; empty for the optimise's own cuts.</summary>
    public string FilledInto { get; set; } = "";

    public bool IsVertical => Math.Abs(X1 - X2) < 0.001;

    public GuillotineCut Copy() => (GuillotineCut)MemberwiseClone();

    /// <summary>True when this cut lies on the line and covers part of <paramref name="from"/> to <paramref name="to"/>.</summary>
    internal bool Runs(bool vertical, double position, double from, double to, double tolerance)
    {
      if (vertical != IsVertical)
        return false;
      double at = vertical ? X1 : Y1;
      if (Math.Abs(at - position) >= tolerance)
        return false;
      double start = vertical ? Math.Min(Y1, Y2) : Math.Min(X1, X2);
      double end = vertical ? Math.Max(Y1, Y2) : Math.Max(X1, X2);
      return Math.Min(end, to) - Math.Max(start, from) > tolerance;
    }
  }
}
