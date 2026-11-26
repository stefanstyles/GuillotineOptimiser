using System;
using System.Collections.Generic;
using System.Linq;

namespace S3Ware.Guillotine
{
  public sealed class GuillotineResult
  {
    /// <summary>Sheets in cutting order.</summary>
    public List<GuillotineSheet> Sheets { get; set; } = new List<GuillotineSheet>();

    /// <summary>Pieces larger than every sheet, or left when the stock ran out.</summary>
    public List<GuillotinePiece> UnplacedPieces { get; set; } = new List<GuillotinePiece>();

    /// <summary>
    /// A sheet that still has an offcut thinner than the break distance. Only a piece the sheet cannot hold cleanly
    /// on its own leaves one. Empty for a layout the table can break out.
    /// </summary>
    public List<string> Warnings { get; set; } = new List<string>();

    /// <summary>The first optimise left no unbreakable offcut, so no sheet was rebuilt.</summary>
    public bool FirstPassClean { get; set; }

    /// <summary>Whole-batch settings run to build the sheets (1 for a large batch).</summary>
    public int SettingsTried { get; set; }

    public double ElapsedSeconds { get; set; }

    /// <summary>When the search ended; the date the run sheet shows.</summary>
    public DateTime OptimisedAt { get; set; }

    /// <summary>Every build the staged search ran, in order. Empty when no staged search ran.</summary>
    public List<GuillotineBuild> Builds { get; set; } = new List<GuillotineBuild>();

    /// <summary>The fewest sheets the glass area allows; the staged search stops when it gets there.</summary>
    public int FewestSheetsPossible { get; set; }

    /// <summary>Why the staged search ended. Null when no staged search ran.</summary>
    public GuillotineStop? StoppedBy { get; set; }

    /// <summary>Area of the sheets used that is not a piece, trim and the last sheet's remnant included, in percent.</summary>
    public double WastePercent
    {
      get
      {
        double area = Sheets.Sum(sheet => sheet.LengthMm * sheet.WidthMm);
        return area > 0 ? 100 * (1 - Sheets.Sum(sheet => sheet.FillRatio * sheet.LengthMm * sheet.WidthMm) / area) : 0;
      }
    }

    public GuillotineSummary Summarise()
    {
      var last = Sheets.Count > 0 ? Sheets[^1] : null;
      var remnant = last?.Remnant();
      return new GuillotineSummary
      {
        Sheets = Sheets.Count,
        Pieces = Sheets.Sum(sheet => sheet.Pieces.Count(piece => !piece.IsFiller)),
        FillPieces = Sheets.Sum(sheet => sheet.Pieces.Count(piece => piece.IsFiller)),
        UnplacedPieces = UnplacedPieces.Count,
        StockM2 = Sheets.Sum(sheet => sheet.AreaM2),
        PieceM2 = Sheets.Sum(sheet => sheet.PieceAreaM2(false)),
        FillM2 = Sheets.Sum(sheet => sheet.PieceAreaM2(true)),
        TrimM2 = Sheets.Sum(sheet => sheet.TrimAreaM2),
        Remnant = remnant == null ? null : new GuillotineRemnant(Sheets.Count, remnant.Value.LengthMm, remnant.Value.WidthMm),
        BySize = Sheets
          .GroupBy(sheet => (L: Math.Round(sheet.LengthMm, 1), W: Math.Round(sheet.WidthMm, 1)))
          .Select(group => new GuillotineSizeSummary(group.Key.L, group.Key.W, group.Count(),
            group.Sum(sheet => sheet.AreaM2), group.Sum(sheet => sheet.PieceAreaM2(false)), group.Sum(sheet => sheet.PieceAreaM2(true))))
          .OrderByDescending(size => size.Sheets)
          .ToList()
      };
    }

    /// <summary>
    /// Puts the sheets of each size together for the run: size by the longer edge then the shorter (not m²), smallest
    /// first. Each size's sheets keep their relative order.
    /// </summary>
    public void GroupSheetsBySize()
    {
      if (Sheets == null || Sheets.Count < 2)
        return;
      var ordered = Sheets
        .Select((sheet, index) => (sheet, index, size: RunSize(sheet)))
        .OrderBy(row => row.size.Length)
        .ThenBy(row => row.size.Width)
        .ThenBy(row => row.index)
        .Select(row => row.sheet)
        .ToList();
      Sheets.Clear();
      Sheets.AddRange(ordered);
    }

    /// <summary>Longer edge as length, shorter as width — used only for run size order.</summary>
    internal static (double Length, double Width) RunSize(GuillotineSheet sheet)
    {
      double a = Math.Round(sheet?.LengthMm ?? 0, 1);
      double b = Math.Round(sheet?.WidthMm ?? 0, 1);
      return a >= b ? (a, b) : (b, a);
    }

    /// <summary>
    /// Joins a result read back from JSON to the pieces and boards it was made from, so shapes draw. A placed piece not
    /// among <paramref name="pieces"/> (a fill piece) gets a plain piece of its size.
    /// </summary>
    public void Restore(IEnumerable<GuillotinePiece> pieces, IList<BoardDefinition> boards)
    {
      var byId = new Dictionary<string, GuillotinePiece>(StringComparer.Ordinal);
      foreach (var piece in pieces ?? Enumerable.Empty<GuillotinePiece>())
        byId.TryAdd(piece.Id, piece);
      foreach (var sheet in Sheets)
      {
        sheet.SourcePieces = sheet.Pieces.Select(part =>
        {
          if (byId.TryGetValue(part.Id, out var piece))
            return piece;
          bool turned = Math.Abs(part.RotationDegrees) > 0.001;
          return new GuillotinePiece { Id = part.Id, LengthMm = turned ? part.Height : part.Width, WidthMm = turned ? part.Width : part.Height };
        }).ToList();
        sheet.BoardIndex = -1;
        for (int i = 0; i < (boards?.Count ?? 0); i++)
        {
          if (Math.Abs(boards[i].LengthMm - sheet.LengthMm) < 0.5 && Math.Abs(boards[i].WidthMm - sheet.WidthMm) < 0.5)
          {
            sheet.BoardIndex = i;
            break;
          }
        }
      }
    }

    /// <summary>
    /// The piece area on the last sheet over the room inside the trims the other sheets leave spare. A sheet fewer
    /// needs the last sheet's pieces moved into that room, so the higher it is the less likely. Infinite with one sheet
    /// or no spare room.
    /// </summary>
    public double LastSheetShareOfSpare()
    {
      if (Sheets.Count < 2)
        return double.PositiveInfinity;
      double spare = Sheets.Take(Sheets.Count - 1).Sum(sheet => sheet.SpareM2);
      double last = Sheets[^1].PieceAreaM2(false) + Sheets[^1].PieceAreaM2(true);
      return spare > 0 ? last / spare : double.PositiveInfinity;
    }

    /// <summary>
    /// Why <paramref name="piece"/>, one of <see cref="UnplacedPieces"/>, was not placed on the stock
    /// <paramref name="boards"/>: larger than every size inside its trim, the warning that names it or its group (a
    /// group that cannot be cut, an offcut thinner than the break distance), or the sheets ran out.
    /// </summary>
    public string UnplacedReason(GuillotinePiece piece, IReadOnlyList<BoardDefinition> boards, SheetTrim trim, bool allowTurnedSheets)
    {
      const double tolerance = 0.001;
      bool Fits(BoardDefinition board)
      {
        var edges = board.Trim ?? trim ?? SheetTrim.None();
        double length = board.LengthMm - edges.LeftMm - edges.RightMm, width = board.WidthMm - edges.TopMm - edges.BottomMm;
        bool turns = piece.CanRotate || (board.CanTurn ?? allowTurnedSheets);
        return piece.LengthMm <= length + tolerance && piece.WidthMm <= width + tolerance
          || turns && piece.WidthMm <= length + tolerance && piece.LengthMm <= width + tolerance;
      }
      bool SameSize(GuillotineSheet sheet, BoardDefinition board) =>
        Math.Abs(sheet.LengthMm - board.LengthMm) < 0.5 && Math.Abs(sheet.WidthMm - board.WidthMm) < 0.5
        || Math.Abs(sheet.LengthMm - board.WidthMm) < 0.5 && Math.Abs(sheet.WidthMm - board.LengthMm) < 0.5;

      if (boards == null || boards.Count == 0)
        return "No sheet size is ticked.";
      if (!boards.Any(Fits))
        return $"{piece.LengthMm:0} x {piece.WidthMm:0} is larger than every sheet size inside its trim.";
      string group = piece.Group?.Trim() ?? "";
      string warning = Warnings.FirstOrDefault(text => text.StartsWith($"Piece {piece.Id} ", StringComparison.Ordinal))
        ?? (group.Length > 0 ? Warnings.FirstOrDefault(text => text.StartsWith($"Group {group} ", StringComparison.Ordinal)) : null);
      if (warning != null)
        return warning;
      if (boards.All(board => !board.QuantityUnlimited && Sheets.Count(sheet => SameSize(sheet, board)) >= board.Quantity))
        return "The sheets ran out: every sheet size's quantity is used. Add sheets to the size and optimise again.";
      return "No space left on the sheets.";
    }

    /// <summary>Fewer unplaced pieces, then fewer warnings, then fewer sheets, then the emptier last sheet.</summary>
    public bool IsBetterThan(GuillotineResult other)
    {
      if (other == null)
        return true;
      if (UnplacedPieces.Count != other.UnplacedPieces.Count)
        return UnplacedPieces.Count < other.UnplacedPieces.Count;
      if (Warnings.Count != other.Warnings.Count)
        return Warnings.Count < other.Warnings.Count;
      if (Sheets.Count != other.Sheets.Count)
        return Sheets.Count < other.Sheets.Count;
      // Same sheet count: the emptier last sheet leaves the larger remnant.
      return Sheets.Count > 0 && Sheets[^1].FillRatio < other.Sheets[^1].FillRatio - 0.0001;
    }
  }

  /// <summary>
  /// One build of a staged search: seconds from the start when it finished, its sheets, how full its last sheet is,
  /// whether it beat the best so far, and whether it was a rework (the first builds are not). The piece area on the
  /// last sheet and the room inside the trims left on every other sheet, in m², tell how near it is to one sheet fewer.
  /// </summary>
  public sealed record GuillotineBuild(double Seconds, int Sheets, double LastSheetFill, bool Better, bool Rework,
    double LastSheetPieceM2, double SpareM2);

  public enum GuillotineStop
  {
    /// <summary>The fewest sheets the piece area allows; no layout can do better.</summary>
    FewestSheetsPossible,
    /// <summary><see cref="GuillotineOptimiser.StallRounds"/> builds in a row found nothing better.</summary>
    Stalled,
    TimeLimit,
    /// <summary>The user stopped it and took the best layout so far.</summary>
    Stopped,
    MaxReworks,
    /// <summary>The last sheet holds more than <see cref="GuillotineOptimiser.ReachShare"/> of the other sheets' spare room.</summary>
    SheetFewerOutOfReach
  }

  public static class GuillotineStopText
  {
    /// <summary>Why a search ended, as the optimise screens show it; empty while it is still searching.</summary>
    public static string Describe(GuillotineStop? stoppedBy) => stoppedBy switch
    {
      GuillotineStop.FewestSheetsPossible => "Done: fewest sheets possible",
      GuillotineStop.Stalled => "Done: no better layout found",
      GuillotineStop.TimeLimit => "Done: time limit",
      GuillotineStop.Stopped => "Stopped: best so far",
      GuillotineStop.MaxReworks => "Done: most builds",
      GuillotineStop.SheetFewerOutOfReach => "Done: a sheet fewer is out of reach",
      _ => ""
    };
  }

  /// <summary>
  /// A staged search under way: its best layout so far against the fewest sheets possible, and how long since it
  /// improved. Reported when a build finds a better layout, about once a second while it builds, and once at the end
  /// with <see cref="StoppedBy"/> set.
  /// </summary>
  public sealed record GuillotineProgress(
    double Seconds,
    double TimeLimitSeconds,
    int Sheets,
    int FewestSheetsPossible,
    double WastePercent,
    double LastSheetFillPercent,
    int UnplacedPieces,
    int Builds,
    int BuildsSinceBetter,
    double SecondsSinceBetter,
    bool Better,
    GuillotineStop? StoppedBy)
  {
    /// <summary>No layout can use fewer sheets; stopping now loses nothing.</summary>
    public bool AtFewestSheets => Sheets <= FewestSheetsPossible;
  }
}
