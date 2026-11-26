using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace S3Ware.Guillotine
{
  /// <summary>
  /// Staged guillotine cutting: a glass table with zero kerf, and board for a saw or router with
  /// <see cref="GapMm"/> above zero. A sheet is cut into strips (X cuts), a strip into rows (Y cuts), a row into pieces
  /// side by side (Z cuts) and, at four stages, a piece is trimmed or split once more (W cuts). Every leftover is laid
  /// out flush or at least <see cref="MinBreakDistanceMm"/> wide, so each sheet breaks out as laid out.
  /// A non-guillotine nest (MaxRects) was tried on 199 real batches and removed: guillotine layouts were as good as
  /// non-guillotine ones (2324 against 2328 sheets), so a better guillotine search is the better route than freer layouts.
  /// </summary>
  public sealed class GuillotineOptimiser
  {
    private const double Eps = 0.001;

    /// <summary>The thinnest leftover that is not flush. Thinner would score a cut on top of the next cut or the trim.</summary>
    private const double MinLeftoverMm = 1;

    /// <summary>Sizes in each direction up to which pairs side by side are strip widths too and rows of them value a width.</summary>
    private const int PairedWidthOptions = 40;

    /// <summary>How much more a priority piece is worth than the same piece without priority, so a sheet takes every priority piece it can hold first.</summary>
    private const double PriorityWeight = 1e6;

    /// <summary>Trim on each sheet edge. An edge the table does not trim is 0.</summary>
    public SheetTrim Trim { get; set; } = SheetTrim.None();

    /// <summary>The thinnest strip the table can break off, in mm. 0 for board.</summary>
    public double MinBreakDistanceMm { get; set; } = 20;

    /// <summary>
    /// Clear space every cut takes between pieces: the saw kerf or router bit, in mm. 0 on a glass table. A piece
    /// may still lie flush with the trim or the sheet edge. Cut lines are drawn down the middle of the gap.
    /// </summary>
    public double GapMm { get; set; }

    /// <summary>
    /// Cut stages: 3 for glass limited to Z cuts (no W cuts), 4 with W cuts, 5 with V and 6 with U.
    /// The search lays out 4; deeper, once it is done, the best layout is built again with the leftovers below each
    /// sheet's strips cut on to this level from pieces still to cut, and kept when that saves sheets or fills the last.
    /// Only with no gap between pieces (glass).
    /// </summary>
    public int MaxCutLevel { get; set; } = 4;

    /// <summary>A sheet may be turned 90° on the table, so its strips run the other way.</summary>
    public bool AllowTurnedSheets { get; set; } = true;

    /// <summary>
    /// Beam saw: sheets load for horizontal first cuts - prefer a turned frame so level-0 cuts are horizontal rips
    /// (no sheet turn before the first cut). False for a glass table or a router.
    /// </summary>
    public bool PreferHorizontalRipsFirst { get; set; }

    /// <summary>
    /// After the layout, reorder for beam-saw breakout: longest/widest at the origin, shortest at the far end (right-hand
    /// side), because the saw takes the right-hand side first so the smaller length comes off first. Uses
    /// <see cref="GuillotineSheet.ArrangeForBreakout"/>. Beam saw only.
    /// </summary>
    public bool OrderStripsNarrowestFirst { get; set; }

    /// <summary>
    /// Glass: each sheet is laid out top down, from the back of the table, so every break-off across the table's depth
    /// comes out at the front (<see cref="GuillotineSheet.ArrangeForBreakout"/>). Off, bottom up, as the strips are built.
    /// </summary>
    public bool BuildTopDown { get; set; } = true;

    /// <summary>See <see cref="GuillotineSheet.BackTrimCut"/>.</summary>
    public bool CutBackTrim { get; set; }

    /// <summary>A layout laid into a leftover keeps the way it was built: the sheet it goes into is arranged as a whole.</summary>
    private bool ArrangesForBreakout { get; init; } = true;

    /// <summary>
    /// After this many seconds the search ends with the best layout so far; a build under way is dropped after its
    /// current sheet. The first build always finishes. Most batches stop well before, at the fewest sheets possible or
    /// when rebuilding stops finding fewer sheets.
    /// </summary>
    public double TimeLimitSeconds { get; set; } = 120;

    /// <summary>Builds after the first few, each valuing the sizes left on poorly filled sheets more.</summary>
    public int MaxReworks { get; set; } = 500;

    /// <summary>Reworks in a row that find nothing better end the search.</summary>
    public int StallRounds { get; set; } = 20;

    /// <summary>
    /// Ends the search before a rework once the best layout's last sheet holds more than this share of the room the
    /// other sheets have spare (<see cref="GuillotineResult.LastSheetShareOfSpare"/>): a sheet fewer is out of reach and
    /// further builds would only leave the last sheet a little emptier. On 273 runs the reworks saved a sheet
    /// only from below 0.25, and 0.4 lost none. 0 turns it off.
    /// </summary>
    public double ReachShare { get; set; } = 0.3;

    /// <summary>Searches run at once when several boards or glasses are optimised: one per processor, leaving one for the screen.</summary>
    public static int ParallelSearches => Math.Max(1, Environment.ProcessorCount - 1);

    private Stopwatch _clock = new Stopwatch();
    private double[] _value = Array.Empty<double>();
    private bool[] _took = Array.Empty<bool>();

    /// <summary>
    /// With a gap, each piece is laid out grown by the gap and the usable sheet grows by the gap too, so every cut
    /// between pieces takes exactly one gap and a piece can still reach the far edge. Grown piece to the piece given.
    /// </summary>
    private Dictionary<GuillotinePiece, GuillotinePiece> _given = new Dictionary<GuillotinePiece, GuillotinePiece>();

    private GuillotinePiece Given(GuillotinePiece piece) => _given.TryGetValue(piece, out var given) ? given : piece;

    private int Stages => Math.Min(MaxCutLevel, 4);

    /// <param name="boards">The stock sheets available.</param>
    /// <param name="pieces">The pieces to cut.</param>
    /// <param name="stop">Ends the search with the best layout so far, as the time limit does.</param>
    /// <param name="progress">The best layout so far, as it improves, about once a second, and at the end.</param>
    public GuillotineResult Optimise(IEnumerable<BoardDefinition> boards, IEnumerable<GuillotinePiece> pieces,
      CancellationToken stop = default, IProgress<GuillotineProgress> progress = null)
    {
      var boardList = (boards ?? Enumerable.Empty<BoardDefinition>()).Where(board => board != null).ToList();
      var pieceList = (pieces ?? Enumerable.Empty<GuillotinePiece>()).Where(piece => piece != null).ToList();
      Validate(boardList, pieceList);
      var ungrouped = new List<GuillotinePiece>();
      var groupWarnings = new List<string>();
      pieceList = Grouped(pieceList, ungrouped, groupWarnings);
      _clock = Stopwatch.StartNew();
      _given = new Dictionary<GuillotinePiece, GuillotinePiece>();
      if (GapMm > 0)
      {
        pieceList = pieceList.Select(piece =>
        {
          var grown = new GuillotinePiece
          {
            Id = piece.Id,
            LengthMm = piece.LengthMm + GapMm,
            WidthMm = piece.WidthMm + GapMm,
            CanRotate = piece.CanRotate,
            Priority = piece.Priority,
            Block = piece.Block
          };
          _given[grown] = piece;
          return grown;
        }).ToList();
      }

      var tooLarge = pieceList.Where(piece => !FitsABoard(piece, boardList)).ToList();
      var toCut = pieceList.Except(tooLarge).ToList();
      int fewestSheets = FewestSheetsPossible(boardList, toCut);

      GuillotineResult best = null;
      Strategy bestStrategy = null;
      int tried = 0, sinceBetter = 0;
      double betterAt = 0, reportedAt = 0;
      var builds = new List<GuillotineBuild>();
      double Seconds() => _clock.Elapsed.TotalSeconds;
      void Record(GuillotineResult built, bool better, bool rework) =>
        builds.Add(new GuillotineBuild(Seconds(), built.Sheets.Count,
          built.Sheets.Count > 0 ? built.Sheets[^1].FillRatio : 0, better, rework,
          built.Sheets.Count > 0 ? built.Sheets[^1].PieceAreaM2(false) + built.Sheets[^1].PieceAreaM2(true) : 0,
          built.Sheets.Take(Math.Max(0, built.Sheets.Count - 1)).Sum(sheet => sheet.SpareM2)));
      void Report(bool better, GuillotineStop? stoppedBy)
      {
        if (progress == null || best == null)
          return;
        reportedAt = Seconds();
        progress.Report(new GuillotineProgress(reportedAt, TimeLimitSeconds, best.Sheets.Count, fewestSheets,
          best.WastePercent, best.Sheets.Count > 0 ? 100 * best.Sheets[^1].FillRatio : 0,
          best.UnplacedPieces.Count + tooLarge.Count, tried, sinceBetter, reportedAt - betterAt, better, stoppedBy));
      }
      bool Abandon() => best != null && (stop.IsCancellationRequested || Seconds() >= TimeLimitSeconds);
      bool KeepBuilding()
      {
        if (Abandon())
          return false;
        if (Seconds() - reportedAt >= 1)
          Report(false, null);
        return true;
      }
      bool Done() => Abandon() || (best != null && best.Sheets.Count <= fewestSheets);
      bool Take(GuillotineResult built, bool rework)
      {
        tried++;
        sinceBetter++;
        bool better = built.IsBetterThan(best);
        Record(built, better, rework);
        if (better)
        {
          best = built;
          sinceBetter = 0;
          betterAt = Seconds();
          Report(true, null);
        }
        return better;
      }

      foreach (double power in new[] { 1.0, 1.25, 1.5, 0.85 })
      {
        if (Done())
          break;
        var strategy = new Strategy(power, new Dictionary<PieceType, double>());
        var built = Build(boardList, toCut, strategy, KeepBuilding);
        if (built == null)
          break;
        if (Take(built, false))
          bestStrategy = strategy;
      }

      // Sequential value correction: a size cut from a poorly filled sheet is worth more next time, so it is placed
      // while there is still room to choose.
      var rework = bestStrategy;
      var bestBuiltBy = bestStrategy;
      var last = best;
      var usableAreas = Frame.UsableAreaOf(boardList, Trim, GapMm);
      int stalled = 0;
      bool OutOfReach() => ReachShare > 0 && best.UnplacedPieces.Count == 0 && best.Warnings.Count == 0
        && best.LastSheetShareOfSpare() > ReachShare;
      bool outOfReach = false;
      for (int round = 0; round < MaxReworks && stalled < StallRounds && !Done(); round++)
      {
        if (OutOfReach())
        {
          outOfReach = true;
          break;
        }
        rework = rework.Corrected(last, usableAreas);
        last = Build(boardList, toCut, rework, KeepBuilding);
        if (last == null)
          break;
        if (Take(last, true))
        {
          stalled = 0;
          bestBuiltBy = rework;
        }
        else
          stalled++;
      }

      if (MaxCutLevel > Stages && GapMm <= 0 && best.Sheets.Count > fewestSheets && !stop.IsCancellationRequested)
        Take(Build(boardList, toCut, bestBuiltBy, () => true, deeper: true), false);

      best.StoppedBy = best.Sheets.Count <= fewestSheets ? GuillotineStop.FewestSheetsPossible
        : stop.IsCancellationRequested ? GuillotineStop.Stopped
        : Seconds() >= TimeLimitSeconds ? GuillotineStop.TimeLimit
        : outOfReach ? GuillotineStop.SheetFewerOutOfReach
        : stalled >= StallRounds ? GuillotineStop.Stalled
        : GuillotineStop.MaxReworks;
      Report(false, best.StoppedBy);
      best.FirstPassClean = true;
      best.SettingsTried = tried;
      best.Builds = builds;
      best.FewestSheetsPossible = fewestSheets;
      best.UnplacedPieces.AddRange(tooLarge);
      best.UnplacedPieces = best.UnplacedPieces.Select(Given)
        .SelectMany(piece => piece.Block?.Members.Select(member => member.Piece) ?? new[] { piece })
        .Concat(ungrouped)
        .ToList();
      best.Warnings.AddRange(groupWarnings);
      best.GroupSheetsBySize();
      if (ArrangesForBreakout && (OrderStripsNarrowestFirst || GapMm <= 0 && MinBreakDistanceMm > 0))
      {
        double breakMm = MinBreakDistanceMm > 0 ? MinBreakDistanceMm : 0;
        foreach (var sheet in best.Sheets)
          sheet.ArrangeForBreakout(BuildTopDown, breakMm, shortestAtFarEnd: OrderStripsNarrowestFirst);
      }
      best.ElapsedSeconds = Seconds();
      best.OptimisedAt = DateTime.Now;
      return best;
    }

    /// <summary>
    /// Lays <paramref name="fill"/> pieces into the sheet's leftovers, largest leftover first. Each leftover is cut on
    /// from the cut that made it, at that cut's level, so the sheet stays guillotine, within <see cref="MaxCutLevel"/>
    /// and breakable, with <see cref="GapMm"/> between pieces (the saw blade; 0 on glass). Placed pieces are marked
    /// <see cref="PlacedPart.IsFiller"/> and taken out of <paramref name="fill"/>; each leftover filled is kept as it
    /// was in <see cref="GuillotineSheet.FilledLeftovers"/>, so <see cref="RemoveFill"/> can give it back. A leftover
    /// with no <see cref="SheetGap.CutLevel"/> is not filled. Returns how many were placed.
    /// </summary>
    public int FillOffcuts(GuillotineSheet sheet, List<GuillotinePiece> fill) => LayIntoOffcuts(sheet, fill, true, 0, FillSearch);

    /// <summary>
    /// Takes fill pieces off the sheet: <paramref name="parts"/>, or every fill piece when null. Each leftover a removed
    /// piece was laid into goes back as it was, its fill cuts gone, and the fill pieces kept in it are laid into it
    /// again (as they lay, not turned). A fill piece laid some other way leaves its space as a gap with no cut level.
    /// Returns how many pieces were taken off; <paramref name="lost"/> is how many kept pieces no longer fit.
    /// </summary>
    public int RemoveFill(GuillotineSheet sheet, IEnumerable<PlacedPart> parts, out int lost)
    {
      lost = 0;
      if (sheet == null)
        return 0;
      var remove = new HashSet<PlacedPart>((parts ?? sheet.Pieces).Where(part => part != null && part.IsFiller && sheet.Pieces.Contains(part)));
      if (remove.Count == 0)
        return 0;
      var leftovers = new HashSet<string>(remove.Select(part => part.FilledInto ?? "").Where(id => id.Length > 0), StringComparer.Ordinal);
      var kept = new List<PlacedPart>();
      for (int i = sheet.Pieces.Count - 1; i >= 0; i--)
      {
        var part = sheet.Pieces[i];
        bool removed = remove.Contains(part);
        if (!removed && !(part.IsFiller && leftovers.Contains(part.FilledInto ?? "")))
          continue;
        if (!removed)
          kept.Add(part);
        sheet.Pieces.RemoveAt(i);
        if (i < sheet.SourcePieces.Count)
          sheet.SourcePieces.RemoveAt(i);
        if (removed && string.IsNullOrEmpty(part.FilledInto))
          sheet.Offcuts.Add(new SheetGap { X = part.X, Y = part.Y, Width = part.Width, Height = part.Height });
      }
      sheet.Cuts.RemoveAll(cut => leftovers.Contains(cut.FilledInto ?? ""));
      sheet.Offcuts.RemoveAll(gap => leftovers.Contains(gap.FilledInto ?? ""));
      foreach (string id in leftovers)
      {
        var original = sheet.FilledLeftovers.FirstOrDefault(gap => gap.Id == id);
        if (original == null)
          continue;
        sheet.FilledLeftovers.Remove(original);
        var back = original.Copy();
        sheet.Offcuts.Add(back);
        var again = kept.Where(part => part.FilledInto == id).ToDictionary(part => part.Id, StringComparer.Ordinal);
        if (again.Count == 0)
          continue;
        var pieces = again.Values.Select(part => new GuillotinePiece { Id = part.Id, LengthMm = part.Width, WidthMm = part.Height, CanRotate = false }).ToList();
        int before = sheet.Pieces.Count;
        lost += pieces.Count - LayIntoOffcuts(sheet, pieces, true, 0, FillSearch, back);
        for (int i = before; i < sheet.Pieces.Count; i++)
        {
          var laid = sheet.Pieces[i];
          var copy = again[laid.Id].Copy();
          copy.X = laid.X;
          copy.Y = laid.Y;
          copy.FilledInto = laid.FilledInto;
          sheet.Pieces[i] = copy;
        }
      }
      NumberOffcuts(sheet);
      return remove.Count;
    }

    private sealed record OffcutSearch(double Seconds, int Builds, int Stall);

    private static readonly OffcutSearch FillSearch = new OffcutSearch(2, 20, 5);

    /// <summary>A V or U leftover is laid out once per sheet, inside a search that builds many times.</summary>
    private static readonly OffcutSearch DeeperSearch = new OffcutSearch(0, 0, 1);

    /// <summary>
    /// Lays <paramref name="pieces"/> into the offcuts made by a cut of <paramref name="fromLevel"/> or deeper (only
    /// <paramref name="only"/> when it is given), each laid out to <see cref="MaxCutLevel"/> from that cut's level.
    /// Placed pieces are taken out of <paramref name="pieces"/>. Fill (<paramref name="asFiller"/>) marks what it adds
    /// with the leftover it went into.
    /// </summary>
    private int LayIntoOffcuts(GuillotineSheet sheet, List<GuillotinePiece> pieces, bool asFiller, int fromLevel, OffcutSearch search, SheetGap only = null)
    {
      if (sheet == null || pieces == null || pieces.Count == 0)
        return 0;

      var offcuts = sheet.Offcuts.OrderByDescending(gap => gap.Width * gap.Height).ToList();
      sheet.Offcuts = new List<SheetGap>();
      int placed = 0;
      foreach (var gap in offcuts)
      {
        int level = gap.CutLevel ?? 0;
        int stages = gap.CutLevel.HasValue && level >= fromLevel && (only == null || gap == only) && pieces.Count > 0 ? MaxCutLevel - level : 0;
        bool across = !gap.CutVertical;
        double length = across ? gap.Height : gap.Width, width = across ? gap.Width : gap.Height;
        var given = new Dictionary<GuillotinePiece, GuillotinePiece>();
        foreach (var piece in stages < 2 ? Enumerable.Empty<GuillotinePiece>() : pieces)
        {
          var laidAs = across
            ? new GuillotinePiece { Id = piece.Id, LengthMm = piece.WidthMm, WidthMm = piece.LengthMm, CanRotate = piece.CanRotate, Priority = piece.Priority }
            : new GuillotinePiece { Id = piece.Id, LengthMm = piece.LengthMm, WidthMm = piece.WidthMm, CanRotate = piece.CanRotate, Priority = piece.Priority };
          if (Orientations(laidAs, false).Any(o => o.Across <= length + Eps && o.Along <= width + Eps))
            given[laidAs] = piece;
        }
        if (given.Count == 0)
        {
          sheet.Offcuts.Add(gap);
          continue;
        }

        var sub = new GuillotineOptimiser
        {
          MinBreakDistanceMm = MinBreakDistanceMm,
          GapMm = GapMm,
          MaxCutLevel = stages,
          AllowTurnedSheets = false,
          ArrangesForBreakout = false,
          TimeLimitSeconds = search.Seconds,
          MaxReworks = search.Builds,
          StallRounds = search.Stall,
          ReachShare = 0
        };
        var laid = sub.Optimise(new[] { new BoardDefinition { LengthMm = length, WidthMm = width, Quantity = 1 } }, given.Keys).Sheets.FirstOrDefault();
        if (laid == null || laid.Pieces.Count == 0 || !laid.IsBreakable(MinBreakDistanceMm))
        {
          sheet.Offcuts.Add(gap);
          continue;
        }

        string into = !asFiller ? "" : !string.IsNullOrEmpty(gap.FilledInto) ? gap.FilledInto : KeepFilledLeftover(sheet, gap);
        (double X, double Y, double Width, double Height) Place(double x, double y, double w, double h) =>
          across ? (gap.X + y, gap.Y + x, h, w) : (gap.X + x, gap.Y + y, w, h);
        for (int i = 0; i < laid.Pieces.Count; i++)
        {
          var part = laid.Pieces[i];
          var piece = given[laid.SourcePieces[i]];
          var (x, y, w, h) = Place(part.X, part.Y, part.Width, part.Height);
          sheet.Pieces.Add(new PlacedPart { Id = piece.Id, X = x, Y = y, Width = w, Height = h, RotationDegrees = LaidAsGiven(piece, w, h) ? 0 : 90, IsFiller = asFiller, FilledInto = into });
          sheet.SourcePieces.Add(piece);
          pieces.Remove(piece);
          placed++;
        }
        foreach (var cut in laid.Cuts)
        {
          var (x1, y1, _, _) = Place(cut.X1, cut.Y1, 0, 0);
          var (x2, y2, _, _) = Place(cut.X2, cut.Y2, 0, 0);
          sheet.Cuts.Add(new GuillotineCut { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Level = level + cut.Level, FilledInto = into });
        }
        foreach (var left in laid.Offcuts)
        {
          var (x, y, w, h) = Place(left.X, left.Y, left.Width, left.Height);
          sheet.Offcuts.Add(new SheetGap
          {
            X = x, Y = y, Width = w, Height = h,
            CutLevel = level + (left.CutLevel ?? 0),
            CutVertical = left.CutVertical != across,
            FilledInto = into
          });
        }
      }
      NumberOffcuts(sheet);
      return placed;
    }

    /// <summary>Keeps <paramref name="gap"/> as it is before fill goes into it, under a new id; returns the id.</summary>
    private static string KeepFilledLeftover(GuillotineSheet sheet, SheetGap gap)
    {
      int next = 1 + sheet.FilledLeftovers
        .Select(kept => kept.Id != null && kept.Id.StartsWith("F", StringComparison.Ordinal) && int.TryParse(kept.Id.Substring(1), out int n) ? n : 0)
        .DefaultIfEmpty(0)
        .Max();
      var original = gap.Copy();
      original.Id = "F" + next;
      original.FilledInto = "";
      sheet.FilledLeftovers.Add(original);
      return original.Id;
    }

    private static void NumberOffcuts(GuillotineSheet sheet)
    {
      for (int i = 0; i < sheet.Offcuts.Count; i++)
        sheet.Offcuts[i].Id = "G" + (i + 1);
    }

    private bool[] Directions(BoardDefinition board) => (board.CanTurn ?? AllowTurnedSheets) ? new[] { false, true } : new[] { false };

    /// <summary>
    /// Each group's pieces as one piece in the place of its first, laid out as the group lays them. A group whose
    /// cuts go deeper than <see cref="MaxCutLevel"/> however it lies, or that leaves waste the table cannot break,
    /// goes to <paramref name="unplaced"/>.
    /// </summary>
    private List<GuillotinePiece> Grouped(List<GuillotinePiece> pieces, List<GuillotinePiece> unplaced, List<string> warnings)
    {
      var comparer = StringComparer.OrdinalIgnoreCase;
      string GroupOf(GuillotinePiece piece) => piece.Group?.Trim() ?? "";
      if (pieces.All(piece => GroupOf(piece).Length == 0))
        return pieces;

      var members = pieces.Where(piece => GroupOf(piece).Length > 0).GroupBy(GroupOf, comparer).ToDictionary(group => group.Key, group => group.ToList(), comparer);
      var result = new List<GuillotinePiece>();
      var done = new HashSet<string>(comparer);
      foreach (var piece in pieces)
      {
        string group = GroupOf(piece);
        if (group.Length == 0)
        {
          result.Add(piece);
          continue;
        }
        if (!done.Add(group))
          continue;

        var block = GuillotineBlock.Build(group, members[group], GapMm);
        if (GapMm <= 0 && block.ThinnestWasteMm > 0 && block.ThinnestWasteMm < MinGapMm - Eps)
        {
          warnings.Add($"Group {group} leaves waste {block.ThinnestWasteMm:0.#} mm wide beside a piece, thinner than the {MinGapMm:0.#} mm the table can break off.");
          unplaced.AddRange(members[group]);
          continue;
        }
        if (!block.Fits(true, true, true, MaxCutLevel - 1) && !block.Fits(false, true, true, MaxCutLevel - 1))
        {
          warnings.Add($"Group {group} needs cuts deeper than the {MaxCutLevel} cut levels however it lies.");
          unplaced.AddRange(members[group]);
          continue;
        }
        result.Add(new GuillotinePiece
        {
          Id = group,
          LengthMm = block.LengthMm,
          WidthMm = block.WidthMm,
          CanRotate = members[group].All(member => member.CanRotate),
          Priority = members[group].Any(member => member.Priority),
          Block = block
        });
      }
      return result;
    }

    /// <summary>
    /// Sheet by sheet, each the fullest layout of every board and direction from the pieces still to cut; while a board
    /// to use first (<see cref="BoardDefinition.IsFirstChoice"/>) has sheets left and takes a piece, only those boards
    /// are laid. A strip laid
    /// out from the whole pool stays the best for its width until one of its pieces is cut, so it is kept per board
    /// and direction between sheets. Null when <paramref name="keepBuilding"/> says stop after a sheet.
    /// <paramref name="deeper"/> lays pieces still to cut into each sheet's leftovers below its strips with V and U
    /// cuts, to <see cref="MaxCutLevel"/>.
    /// </summary>
    private GuillotineResult Build(List<BoardDefinition> boards, List<GuillotinePiece> pieces, Strategy strategy, Func<bool> keepBuilding, bool deeper = false)
    {
      var result = new GuillotineResult();
      var left = boards.Select(board => board.EffectiveQuantity).ToArray();
      var pool = new List<GuillotinePiece>(pieces);
      var kept = new Dictionary<(int Board, bool Turned), Dictionary<double, Strip>>();
      while (pool.Count > 0)
      {
        SheetPlan best = null;
        foreach (bool firstChoice in new[] { true, false })
        {
          for (int b = 0; b < boards.Count; b++)
          {
            if (left[b] <= 0 || boards[b].IsFirstChoice != firstChoice)
              continue;
            foreach (bool turned in Directions(boards[b]))
            {
              if (!kept.TryGetValue((b, turned), out var strips))
                kept[(b, turned)] = strips = new Dictionary<double, Strip>();
              var plan = FillSheet(b, new Frame(boards[b], Trim, turned, GapMm), pool, strategy, strips);
              if (plan.Strips.Count > 0 && plan.IsBetterThan(best))
                best = plan;
            }
          }
          if (best != null)
            break;
        }
        if (best == null)
          break;
        var sheet = ToSheet(best, boards[best.BoardIndex]);
        result.Sheets.Add(sheet);
        left[best.BoardIndex]--;
        var used = best.Pieces();
        pool.RemoveAll(used.Contains);
        if (deeper)
        {
          var loose = pool.Where(piece => piece.Block == null).ToList();
          if (LayIntoOffcuts(sheet, loose, false, 1, DeeperSearch) > 0)
          {
            pool.RemoveAll(piece => piece.Block == null && !loose.Contains(piece));
            used.UnionWith(sheet.SourcePieces);
          }
        }
        foreach (var strips in kept.Values)
          Forget(strips, used);
        if (pool.Count > 0 && !keepBuilding())
          return null;
      }

      if (pool.Count > 0 && left.Any(count => count > 0))
      {
        foreach (var piece in pool)
          result.Warnings.Add($"Piece {piece.Id} cannot be cut from any sheet: it leaves an offcut thinner than the {MinBreakDistanceMm:0.#} mm break distance.");
      }
      result.UnplacedPieces.AddRange(pool);
      return result;
    }

    /// <summary>
    /// Strips across the sheet one at a time. Each step plans the rest of the sheet as a knapsack of strip widths and
    /// lays the planned strip worth most per width, then plans again for what is left, so the last strips are chosen
    /// to fill the sheet rather than taken in order of how good each is on its own.
    /// </summary>
    private SheetPlan FillSheet(int boardIndex, Frame frame, List<GuillotinePiece> pool, Strategy strategy, Dictionary<double, Strip> kept)
    {
      var plan = new SheetPlan(boardIndex, frame, PreferHorizontalRipsFirst);
      var available = new List<GuillotinePiece>(pool);
      var onSheet = new HashSet<GuillotinePiece>();
      var built = new Dictionary<double, Strip>();
      double free = frame.UsableLength;

      bool TryKnown(double width, out Strip strip)
      {
        if (built.TryGetValue(width, out strip))
          return true;
        if (kept.TryGetValue(width, out strip) && (strip == null || !strip.Pieces.Overlaps(onSheet)))
          return true;
        strip = null;
        return false;
      }

      Strip StripFor(double width)
      {
        if (TryKnown(width, out var strip))
          return strip;
        strip = BuildStrip(width, frame, available, strategy);
        if (onSheet.Count == 0)
          kept[width] = strip;
        else
          built[width] = strip;
        return strip;
      }

      while (available.Count > 0)
      {
        // Plan the rest of the sheet on strip values, a bound standing in for a strip not laid out yet. A planned
        // strip is laid out and the plan made again, until every planned strip is a real one.
        List<double> planned;
        var widths = StripWidths(available, frame, free);
        bool grouped = Stages >= 3 && SizeOptions(available, frame).Count <= PairedWidthOptions;
        var bounds = new Dictionary<double, double>();
        while (true)
        {
          var options = new List<(double Width, double Value, int Copies)>();
          foreach (var (width, copies) in widths)
          {
            bool known = TryKnown(width, out var strip);
            if (known && strip == null)
              continue;
            if (!known && !bounds.ContainsKey(width))
              bounds[width] = ValueBound(width, frame, available, strategy, grouped);
            double value = known ? strip.Value : bounds[width];
            if (value > 0)
              options.Add((width, value, copies));
          }
          planned = PlanWidths(options, free, frame);
          bool allKnown = true;
          foreach (double width in planned.Distinct())
          {
            if (TryKnown(width, out _))
              continue;
            StripFor(width);
            allKnown = false;
          }
          if (allKnown)
            break;
        }

        Strip best = null;
        foreach (double width in planned.Distinct())
        {
          var strip = StripFor(width);
          if (strip != null && (best == null || strip.Value / strip.Width > best.Value / best.Width + 1e-9))
            best = strip;
        }
        if (best == null)
          break;

        plan.Strips.Add(best);
        free -= best.Width;
        var used = best.Pieces;
        available.RemoveAll(used.Contains);
        onSheet.UnionWith(used);
        Forget(built, used);
      }
      plan.FreeLength = free;
      return plan;
    }

    private static void Forget(Dictionary<double, Strip> strips, HashSet<GuillotinePiece> used)
    {
      foreach (double width in strips.Where(entry => entry.Value != null && entry.Value.Pieces.Overlaps(used)).Select(entry => entry.Key).ToList())
        strips.Remove(width);
    }

    /// <summary>
    /// Strip widths that fit what is left of the sheet and leave a leftover the table can break off, each with the
    /// number of pieces that wide: a strip needs one, so no more strips of that width fit the pieces. When another
    /// piece is narrower by less than the break distance, the width plus the break distance is a width too, so the
    /// two can share a strip.
    /// </summary>
    private SortedDictionary<double, int> StripWidths(List<GuillotinePiece> available, Frame frame, double free)
    {
      var acrosses = new SortedSet<double>(available
        .SelectMany(piece => Orientations(piece, frame.Turned))
        .Where(o => o.Along <= frame.UsableWidth + Eps)
        .Select(o => Math.Round(o.Across, 3)));
      bool HasNearNarrower(double across) =>
        Stages >= 3 && MinBreakDistanceMm > 2 * Eps && acrosses.GetViewBetween(across - MinBreakDistanceMm + Eps, across - Eps).Count > 0;

      var widths = new SortedDictionary<double, int>();
      foreach (var piece in available)
      {
        foreach (double across in Orientations(piece, frame.Turned)
          .Where(o => o.Along <= frame.UsableWidth + Eps)
          .Select(o => Math.Round(o.Across, 3))
          .SelectMany(across => HasNearNarrower(across) ? new[] { across, across + MinBreakDistanceMm } : new[] { across })
          .Where(width => width <= free + Eps && EndFits(free - width, frame.EndU))
          .Select(width => Math.Round(width, 3))
          .Distinct())
        {
          widths.TryGetValue(across, out int count);
          widths[across] = count + 1;
        }
      }
      foreach (double width in PairedWidths(available, frame, free))
        widths.TryAdd(width, 1);
      return widths;
    }

    /// <summary>
    /// Two pieces side by side in one row, the second no taller than the first, as a strip width of their own
    /// (for example 1044 + 1024 = 2068). Only for batches of a few sizes, where the pairs stay few.
    /// </summary>
    private List<double> PairedWidths(List<GuillotinePiece> available, Frame frame, double free)
    {
      var pairs = new List<double>();
      if (Stages < 3)
        return pairs;
      var options = SizeOptions(available, frame);
      if (options.Count > PairedWidthOptions)
        return pairs;
      foreach (var first in options)
      {
        foreach (var second in options)
        {
          if (second.Along > first.Along + Eps || !UnderFits(first.Along - second.Along) || (first.Type == second.Type && first.Count < 2))
            continue;
          double width = Math.Round(first.Across + second.Across, 3);
          if (width <= free + Eps && EndFits(free - width, frame.EndU))
            pairs.Add(width);
        }
      }
      return pairs;
    }

    /// <summary>Each size once in each direction that fits across the sheet, with how many pieces are that size.</summary>
    private List<(PieceType Type, double Across, double Along, int Count)> SizeOptions(List<GuillotinePiece> available, Frame frame) =>
      available
        .GroupBy(PieceType.Of)
        .SelectMany(group => Orientations(group.First(), frame.Turned)
          .Where(o => o.Along <= frame.UsableWidth + Eps)
          .Select(o => (Type: group.Key, o.Across, o.Along, Count: group.Count())))
        .ToList();

    /// <summary>
    /// The most valuable rows by value per length, the last one cut to fit. At three stages or more the rows of
    /// several pieces side by side count too, so a width made of a pair is valued for what it holds.
    /// </summary>
    private double ValueBound(double width, Frame frame, List<GuillotinePiece> available, Strategy strategy, bool grouped)
    {
      var rows = RowCandidates(width, frame, available).Select(row => (row.Along, Worth: strategy.Value(row), Count: 1));
      if (grouped)
        rows = rows.Concat(RowTemplates(width, frame, available, strategy).Select(t => (t.Along, Worth: t.Value, t.Count)));
      double room = frame.UsableWidth, value = 0;
      foreach (var (along, worth, count) in rows.OrderByDescending(row => row.Worth / row.Along))
      {
        double take = Math.Min(along * count, room);
        value += worth * take / along;
        room -= take;
        if (room <= Eps)
          break;
      }
      return value;
    }

    /// <summary>
    /// Knapsack on what is left of the sheet length: the strip widths worth most together, with a leftover at the end
    /// of the sheet that is flush or breaks off.
    /// </summary>
    private List<double> PlanWidths(List<(double Width, double Value, int Copies)> options, double free, Frame frame)
    {
      var items = options.Select(o => (Math.Max(1, (int)Math.Ceiling(o.Width - Eps)), o.Value, o.Copies)).ToList();
      var counts = Knapsack(items, Capacity(free),
        chosen => EndFits(free - Enumerable.Range(0, chosen.Length).Sum(i => chosen[i] * options[i].Width), frame.EndU));
      return Enumerable.Range(0, options.Count).SelectMany(i => Enumerable.Repeat(options[i].Width, counts[i])).ToList();
    }

    /// <summary>Each piece once, in the direction that leaves the least waste beside it in a strip this wide.</summary>
    private List<Placement> RowCandidates(double width, Frame frame, List<GuillotinePiece> available)
    {
      var candidates = new List<Placement>();
      foreach (var piece in available)
      {
        Placement best = null;
        foreach (var (across, along) in Orientations(piece, frame.Turned))
        {
          if (across > width + Eps || along > frame.UsableWidth + Eps || !SideFits(width - across)
            || !Lays(piece, across, frame.Turned, Math.Abs(width - across) <= Eps, true))
            continue;
          if (best == null || across > best.Across)
            best = new Placement(piece, across, along);
        }
        if (best != null)
          candidates.Add(best);
      }
      return candidates;
    }

    /// <summary>
    /// The better of two strips: rows chosen one piece each and filled beside afterwards, or rows first grouped
    /// several pieces side by side and then chosen whole, so a row of 692 + 632 + 690 is worth what it holds.
    /// </summary>
    private Strip BuildStrip(double width, Frame frame, List<GuillotinePiece> available, Strategy strategy)
    {
      var candidates = RowCandidates(width, frame, available);
      var strip = SingleRowStrip(width, ChooseRows(candidates, frame, strategy), frame, available, strategy);
      if (Stages >= 3 && CanPair(width, candidates, available, frame))
      {
        var grouped = GroupedRowStrip(width, frame, available, strategy);
        if (grouped != null && (strip == null || grouped.Value > strip.Value + 1e-9))
          strip = grouped;
      }
      return strip;
    }

    private Strip SingleRowStrip(double width, List<Placement> chosen, Frame frame, List<GuillotinePiece> available, Strategy strategy)
    {
      if (chosen.Count == 0)
        return null;

      var strip = new Strip(width);
      foreach (var placement in chosen.OrderByDescending(row => row.Along))
      {
        var row = new Row(placement.Along);
        row.Columns.Add(new Column(placement.Across, placement));
        strip.Rows.Add(row);
        strip.Pieces.Add(placement.Piece);
      }
      if (Stages >= 3)
        FillRowSides(strip, available.Where(piece => !strip.Pieces.Contains(piece)).ToList(), frame);
      return Measured(strip, strategy);
    }

    /// <summary>
    /// Rows of several pieces side by side, chosen whole. Each size in each direction gives a template row: that piece
    /// with its sides filled, tallest first, from all the pieces. A knapsack on height takes the templates worth most
    /// together, then the rows are built from the real pieces, most valuable first, so a short supply only leaves a
    /// row less full.
    /// </summary>
    private Strip GroupedRowStrip(double width, Frame frame, List<GuillotinePiece> available, Strategy strategy)
    {
      var templates = RowTemplates(width, frame, available, strategy);
      if (templates.Count == 0)
        return null;

      double length = frame.UsableWidth;
      var items = templates.Select(t => (Math.Max(1, (int)Math.Ceiling(t.Along - Eps)), t.Value, t.Count)).ToList();
      var counts = Knapsack(items, Capacity(length),
        chosen => EndFits(length - Enumerable.Range(0, chosen.Length).Sum(i => chosen[i] * templates[i].Along), frame.EndV));

      var left = available.ToList();
      var strip = new Strip(width);
      var rows = new List<Row>();
      foreach (int i in Enumerable.Range(0, templates.Count).Where(i => counts[i] > 0).OrderByDescending(i => templates[i].Value))
      {
        var template = templates[i];
        for (int n = 0; n < counts[i]; n++)
        {
          var piece = left.FirstOrDefault(p => PieceType.Of(p) == template.Type);
          if (piece == null)
            break;
          var row = new Row(template.Along);
          row.Columns.Add(new Column(template.Across, new Placement(piece, template.Across, template.Along)));
          left.Remove(piece);
          strip.Pieces.Add(piece);
          FillRow(row, width, left, frame, strip.Pieces);
          rows.Add(row);
        }
      }
      if (rows.Count == 0 || !EndFits(length - rows.Sum(row => row.Height), frame.EndV))
        return null;
      strip.Rows.AddRange(rows.OrderByDescending(row => row.Height));
      FillRowSides(strip, left, frame);
      return Measured(strip, strategy);
    }

    /// <summary>A row per size and direction that fits a strip this wide: that piece, its sides filled tallest first.</summary>
    private List<(PieceType Type, double Across, double Along, double Value, int Count)> RowTemplates(
      double width, Frame frame, List<GuillotinePiece> available, Strategy strategy)
    {
      var options = available
        .GroupBy(PieceType.Of)
        .SelectMany(group => Orientations(group.First(), frame.Turned)
          .Where(o => o.Along <= frame.UsableWidth + Eps && o.Across <= width + Eps)
          .Select(o => (Type: group.Key, First: group.First(), o.Across, o.Along, Value: strategy.Value(new Placement(group.First(), o.Across, o.Along)), Count: group.Count())))
        .OrderByDescending(o => o.Along)
        .ThenByDescending(o => o.Across)
        .ToList();
      var templates = new List<(PieceType Type, double Across, double Along, double Value, int Count)>();
      foreach (var anchor in options)
      {
        if (!SideFits(width - anchor.Across) || !Lays(anchor.First, anchor.Across, frame.Turned, Math.Abs(width - anchor.Across) <= Eps, true))
          continue;
        var used = new Dictionary<PieceType, int> { [anchor.Type] = 1 };
        double free = width - anchor.Across, value = anchor.Value;
        foreach (var option in options)
        {
          if (free < MinGapMm - Eps)
            break;
          if (option.Across > free + Eps || !UnderFits(anchor.Along - option.Along)
            || !Lays(option.First, option.Across, frame.Turned, false, Math.Abs(anchor.Along - option.Along) <= Eps))
            continue;
          int count = Math.Min(option.Count - used.GetValueOrDefault(option.Type), (int)Math.Floor((free + Eps) / option.Across));
          while (count > 0 && !SideFits(free - count * option.Across))
            count--;
          if (count <= 0)
            continue;
          used[option.Type] = used.GetValueOrDefault(option.Type) + count;
          free -= count * option.Across;
          value += count * option.Value;
        }
        templates.Add((anchor.Type, anchor.Across, anchor.Along, value, anchor.Count));
      }
      return templates;
    }

    /// <summary>Whether any row in a strip this wide has room beside its piece for the smallest piece left.</summary>
    private bool CanPair(double width, List<Placement> candidates, List<GuillotinePiece> available, Frame frame)
    {
      if (candidates.Count < 2)
        return false;
      double smallest = available.SelectMany(piece => Orientations(piece, frame.Turned)).Min(o => o.Across);
      return candidates.Any(c => width - c.Across >= smallest - Eps);
    }

    private static IEnumerable<Placement> Placed(Row row) => row.Columns.SelectMany(column => column.Stack);

    private static Strip Measured(Strip strip, Strategy strategy)
    {
      var placed = strip.Rows.SelectMany(Placed).ToList();
      strip.Area = placed.Sum(p => p.Across * p.Along);
      strip.Value = placed.Sum(strategy.Value);
      return strip;
    }

    /// <summary>
    /// Knapsack on the strip length: the rows worth most together, with a leftover at the end of the strip that is
    /// flush or breaks off. Pieces of one size are grouped so a batch of repeats stays quick.
    /// </summary>
    private List<Placement> ChooseRows(List<Placement> candidates, Frame frame, Strategy strategy)
    {
      double length = frame.UsableWidth;
      var groups = candidates
        .GroupBy(c => (Math.Round(c.Across, 3), Math.Round(c.Along, 3), PieceType.Of(c.Piece)))
        .Select(group => group.ToList())
        .ToList();
      var items = groups.Select(group => (Math.Max(1, (int)Math.Ceiling(group[0].Along - Eps)), strategy.Value(group[0]), group.Count)).ToList();
      var counts = Knapsack(items, Capacity(length),
        chosen => EndFits(length - Enumerable.Range(0, chosen.Length).Sum(i => chosen[i] * groups[i][0].Along), frame.EndV));
      return Enumerable.Range(0, groups.Count).SelectMany(i => groups[i].Take(counts[i])).ToList();
    }

    /// <summary>
    /// Room in whole millimetres, rounded up as the weights are, so an exact fit with a fractional gap is not lost.
    /// Choices that overflow the real room are left to the knapsack's fits check.
    /// </summary>
    private static int Capacity(double room) => Math.Max(0, (int)Math.Ceiling(room - Eps));

    /// <summary>
    /// Bounded knapsack on whole millimetres: how many of each item to take for the most value within the capacity,
    /// among the choices <paramref name="fits"/> accepts. All zero when none is accepted.
    /// </summary>
    private int[] Knapsack(List<(int Weight, double Value, int Count)> items, int capacity, Func<int[], bool> fits)
    {
      var counts = new int[items.Count];
      var parts = new List<(int Item, int Count)>();
      for (int i = 0; i < items.Count; i++)
      {
        int most = Math.Min(items[i].Count, capacity / items[i].Weight);
        for (int taken = 0, chunk = 1; taken < most; chunk *= 2)
        {
          int count = Math.Min(chunk, most - taken);
          parts.Add((i, count));
          taken += count;
        }
      }
      if (parts.Count == 0)
        return counts;

      int stride = capacity + 1;
      if (_value.Length < stride)
        _value = new double[stride];
      if (_took.Length < parts.Count * stride)
        _took = new bool[parts.Count * stride];
      Array.Fill(_value, -1.0, 0, stride);
      Array.Clear(_took, 0, parts.Count * stride);
      _value[0] = 0;
      for (int p = 0; p < parts.Count; p++)
      {
        int weight = items[parts[p].Item].Weight * parts[p].Count;
        double value = items[parts[p].Item].Value * parts[p].Count;
        int row = p * stride;
        for (int c = capacity; c >= weight; c--)
        {
          double from = _value[c - weight];
          if (from >= 0 && from + value > _value[c] + 1e-9)
          {
            _value[c] = from + value;
            _took[row + c] = true;
          }
        }
      }

      var tried = new HashSet<int>();
      while (true)
      {
        int best = -1;
        for (int c = 1; c <= capacity; c++)
        {
          if (_value[c] > 0 && (best < 0 || _value[c] > _value[best]) && !tried.Contains(c))
            best = c;
        }
        Array.Clear(counts);
        if (best < 0)
          return counts;
        tried.Add(best);

        int at = best;
        for (int p = parts.Count - 1; p >= 0 && at > 0; p--)
        {
          if (!_took[p * stride + at])
            continue;
          counts[parts[p].Item] += parts[p].Count;
          at -= items[parts[p].Item].Weight * parts[p].Count;
        }
        if (fits(counts))
          return counts;
      }
    }

    /// <summary>
    /// Beside a piece narrower than its strip, more pieces side by side: the same height at three stages, or shorter
    /// and stacked in one width at four, each leftover flush or breakable.
    /// </summary>
    private void FillRowSides(Strip strip, List<GuillotinePiece> rest, Frame frame)
    {
      foreach (var row in strip.Rows)
        FillRow(row, strip.Width, rest, frame, strip.Pieces);
    }

    /// <summary>
    /// Knapsack on the free width beside a row: the pieces that fit under its height and fill the width best, each
    /// standing as tall as fits, with a leftover beside them that is flush or breaks off.
    /// </summary>
    private void FillRow(Row row, double width, List<GuillotinePiece> rest, Frame frame, HashSet<GuillotinePiece> taken)
    {
      double free = width - row.Columns.Sum(column => column.Width);
      if (free < MinGapMm - Eps || rest.Count == 0)
        return;

      var fits = new List<Placement>();
      foreach (var piece in rest)
      {
        Placement best = null;
        foreach (var (across, along) in Orientations(piece, frame.Turned))
        {
          if (across > free + Eps || !UnderFits(row.Height - along) || !Lays(piece, across, frame.Turned, false, Math.Abs(row.Height - along) <= Eps))
            continue;
          if (best == null || along > best.Along)
            best = new Placement(piece, across, along);
        }
        if (best != null)
          fits.Add(best);
      }
      if (fits.Count == 0)
        return;

      var groups = fits
        .GroupBy(p => (Math.Round(p.Across, 3), Math.Round(p.Along, 3), PieceType.Of(p.Piece)))
        .Select(group => group.ToList())
        .ToList();
      var items = groups.Select(group => (Math.Max(1, (int)Math.Ceiling(group[0].Across - Eps)), Value: group[0].Across * group[0].Along, group.Count)).ToList();
      var counts = Knapsack(items, Capacity(free),
        chosen => SideFits(free - Enumerable.Range(0, chosen.Length).Sum(i => chosen[i] * groups[i][0].Across)));
      foreach (var pick in Enumerable.Range(0, groups.Count).SelectMany(i => groups[i].Take(counts[i])).OrderByDescending(p => p.Across))
      {
        if (!rest.Remove(pick.Piece))
          continue;
        var column = new Column(pick.Across, pick);
        taken.Add(pick.Piece);
        if (Stages >= 4)
          StackColumn(column, row.Height, rest, taken, frame);
        row.Columns.Add(column);
      }
    }

    /// <summary>More pieces of the column's width under the first, each split off by a W cut.</summary>
    private void StackColumn(Column column, double height, List<GuillotinePiece> rest, HashSet<GuillotinePiece> taken, Frame frame)
    {
      while (true)
      {
        double room = height - column.Stack.Sum(p => p.Along);
        Placement pick = null;
        foreach (var piece in rest)
        {
          foreach (var (across, along) in Orientations(piece, frame.Turned))
          {
            if (Math.Abs(across - column.Width) > Eps || !UnderFits(room - along) || !Lays(piece, across, frame.Turned, false, false))
              continue;
            if (pick == null || along > pick.Along)
              pick = new Placement(piece, across, along);
          }
        }
        if (pick == null)
          return;
        column.Stack.Add(pick);
        rest.Remove(pick.Piece);
        taken.Add(pick.Piece);
      }
    }

    private double MinGapMm => Math.Max(MinBreakDistanceMm, MinLeftoverMm);

    /// <summary>Waste beside a piece in a row: flush, or a Z cut at least the break distance away.</summary>
    private bool SideFits(double side) => side >= -Eps && (side <= Eps || (Stages >= 3 && side >= MinGapMm - Eps));

    /// <summary>Waste under a piece in a row: flush, or a W cut at least the break distance away.</summary>
    private bool UnderFits(double under) => under >= -Eps && (under <= Eps || (Stages >= 4 && under >= MinGapMm - Eps));

    /// <summary>The leftover at the end of the sheet or a strip: flush, or breaking off with the trim on that edge.</summary>
    private bool EndFits(double leftover, double trim) =>
      leftover >= -Eps && (leftover <= Eps || (leftover >= MinLeftoverMm - Eps && leftover + trim >= MinBreakDistanceMm - Eps));

    /// <summary>
    /// Piece sizes across and along a strip. Turning the sheet does not turn a fixed-direction piece on it. A group
    /// lies only the ways its inner cuts can stay within the cut levels, where it fills its row and strip.
    /// </summary>
    private IEnumerable<(double Across, double Along)> Orientations(GuillotinePiece piece, bool turned)
    {
      double across = turned ? piece.WidthMm : piece.LengthMm;
      double along = turned ? piece.LengthMm : piece.WidthMm;
      bool square = Math.Abs(piece.LengthMm - piece.WidthMm) <= Eps;
      bool CanLay(bool lengthAlongStrip) => piece.Block?.Fits(lengthAlongStrip, true, true, MaxCutLevel - 1) ?? true;
      if (CanLay(!turned) || (square && piece.CanRotate && CanLay(turned)))
        yield return (across, along);
      if (piece.CanRotate && !square && CanLay(turned))
        yield return (along, across);
    }

    /// <summary>
    /// Which way a group's length runs when laid <paramref name="across"/> a strip in a cell that fills its strip or
    /// row or neither, keeping its inner cuts within the cut levels: along the strip (U) or not. Null when it cannot.
    /// </summary>
    private bool? LengthAlongStrip(GuillotinePiece piece, double across, bool turned, bool fillsStrip, bool fillsRow)
    {
      foreach (bool alongStrip in new[] { !turned, turned })
      {
        if (alongStrip == turned && !piece.CanRotate)
          continue;
        double length = alongStrip ? piece.LengthMm : piece.WidthMm;
        if (Math.Abs(across - length) > Eps)
          continue;
        if (piece.Block == null || piece.Block.Fits(alongStrip, fillsStrip, fillsRow, MaxCutLevel - 1))
          return alongStrip;
      }
      return null;
    }

    /// <summary>Whether a piece may lie <paramref name="across"/> a strip in such a cell. Always for a piece on its own.</summary>
    private bool Lays(GuillotinePiece piece, double across, bool turned, bool fillsStrip, bool fillsRow) =>
      piece.Block == null || LengthAlongStrip(piece, across, turned, fillsStrip, fillsRow) != null;

    /// <summary>Strips from the start edge, rows from the top of each strip; frame coordinates mapped back to the sheet.</summary>
    private GuillotineSheet ToSheet(SheetPlan plan, BoardDefinition board)
    {
      var frame = plan.Frame;
      var sheet = new GuillotineSheet
      {
        BoardName = board.Name ?? board.Id ?? "",
        LengthMm = board.LengthMm,
        WidthMm = board.WidthMm,
        Trim = frame.SheetTrim,
        BackTrimCut = CutBackTrim,
        BoardIndex = plan.BoardIndex
      };
      double top = frame.StartV, bottom = frame.Width - frame.EndV + frame.Gap, end = frame.Length - frame.EndU + frame.Gap;
      double u = frame.StartU;
      foreach (var strip in plan.Strips)
      {
        double stripEnd = CutOrEnd(u + strip.Width, end, frame.Gap);
        double v = top;
        foreach (var row in strip.Rows)
        {
          double rowEnd = CutOrEnd(v + row.Height, bottom, frame.Gap);
          double x = u;
          foreach (var column in row.Columns)
          {
            double columnEnd = CutOrEnd(x + column.Width, stripEnd, frame.Gap);
            double y = v;
            foreach (var placed in column.Stack)
            {
              bool fillsRow = placed == column.Stack[0] && Math.Abs(placed.Along - row.Height) <= Eps;
              bool fillsStrip = fillsRow && row.Columns.Count == 1 && Math.Abs(placed.Across - strip.Width) <= Eps;
              AddPiece(sheet, frame, placed, x, y, fillsStrip, fillsRow);
              y += placed.Along;
              if (y < rowEnd - frame.Gap - Eps)
                AddCut(sheet, frame, x, y, columnEnd, y, 3);
            }
            AddOffcut(sheet, frame, x, y, columnEnd - x, rowEnd - y, 3);
            x += column.Width;
            if (x < stripEnd - frame.Gap - Eps)
              AddCut(sheet, frame, x, v, x, rowEnd, 2);
          }
          AddOffcut(sheet, frame, x, v, stripEnd - x, rowEnd - v, 2);
          v += row.Height;
          if (v < bottom - frame.Gap - Eps)
            AddCut(sheet, frame, u, v, stripEnd, v, 1);
        }
        AddOffcut(sheet, frame, u, v, stripEnd - u, bottom - v, 1);
        u += strip.Width;
        if (u < end - frame.Gap - Eps)
          AddCut(sheet, frame, u, top, u, bottom, 0);
      }
      AddOffcut(sheet, frame, u, top, end - u, bottom - top, 0);
      if (frame.Gap > 0)
        FitToGap(sheet, frame.Gap);
      return sheet;
    }

    /// <summary>Where a strip, row or column ends: its own cut, or its parent's end when what is left is not wider than the gap.</summary>
    private static double CutOrEnd(double at, double parentEnd, double gap) => at < parentEnd - gap - Eps ? at : parentEnd;

    private void AddPiece(GuillotineSheet sheet, Frame frame, Placement placed, double u, double v, bool fillsStrip, bool fillsRow)
    {
      var piece = Given(placed.Piece);
      if (piece.Block != null)
      {
        AddGroup(sheet, frame, placed, u, v, fillsStrip, fillsRow);
        return;
      }
      double across = placed.Across - frame.Gap, along = placed.Along - frame.Gap;
      var (x, y, width, height) = frame.Turned ? (v, u, along, across) : (u, v, across, along);
      sheet.Pieces.Add(new PlacedPart { Id = piece.Id, X = x, Y = y, Width = width, Height = height, RotationDegrees = LaidAsGiven(piece, width, height) ? 0 : 90 });
      sheet.SourcePieces.Add(piece);
    }

    /// <summary>
    /// Whether a box <paramref name="width"/> by <paramref name="height"/> holds the piece as given rather than turned:
    /// the way round nearer its size. A row of one size is laid at its first piece's size, and pieces that round to that
    /// size can differ from it by a few thousandths (out of square), so an exact match would turn them.
    /// </summary>
    private static bool LaidAsGiven(GuillotinePiece piece, double width, double height) =>
      Math.Abs(width - piece.LengthMm) + Math.Abs(height - piece.WidthMm) <= Math.Abs(width - piece.WidthMm) + Math.Abs(height - piece.LengthMm);

    /// <summary>
    /// A group's pieces where it lies, with the cuts between them and the waste beside any narrower than its slot. Its
    /// cut levels run on from the cell it fills, as <see cref="LengthAlongStrip"/> chose it to lie.
    /// </summary>
    private void AddGroup(GuillotineSheet sheet, Frame frame, Placement placed, double u, double v, bool fillsStrip, bool fillsRow)
    {
      var block = placed.Piece.Block;
      bool alongStrip = LengthAlongStrip(placed.Piece, placed.Across, frame.Turned, fillsStrip, fillsRow)
        ?? Math.Abs(placed.Across - placed.Piece.LengthMm) <= Eps;
      int first = GuillotineBlock.FirstLevel(alongStrip, fillsStrip, fillsRow);
      (double U, double V) At(double l, double w) => alongStrip ? (u + l, v + w) : (u + w, v + l);
      bool lengthAlongX = alongStrip != frame.Turned;
      foreach (var (piece, l, w) in block.Members)
      {
        var (pu, pv) = At(l, w);
        double across = alongStrip ? piece.LengthMm : piece.WidthMm, along = alongStrip ? piece.WidthMm : piece.LengthMm;
        var (x, y, width, height) = frame.Turned ? (pv, pu, along, across) : (pu, pv, across, along);
        sheet.Pieces.Add(new PlacedPart { Id = piece.Id, X = x, Y = y, Width = width, Height = height, RotationDegrees = lengthAlongX ? 0 : 90 });
        sheet.SourcePieces.Add(piece);
      }
      foreach (var cut in block.Cuts.OrderBy(cut => cut.Depth))
      {
        var (u1, v1) = At(cut.L1, cut.W1);
        var (u2, v2) = At(cut.L2, cut.W2);
        AddCut(sheet, frame, u1, v1, u2, v2, first + cut.Depth);
      }
      foreach (var waste in block.Waste)
      {
        var (wu, wv) = At(waste.L, waste.W);
        AddOffcut(sheet, frame, wu, wv, alongStrip ? waste.Length : waste.Width, alongStrip ? waste.Width : waste.Length, first + waste.Depth);
      }
    }

    /// <summary>
    /// Laid out grown by the gap, each cut is the far side of a gap and the last strip and row run a gap past the usable
    /// sheet. Cuts move back half a gap, to the middle of their gap and onto the cuts they end at, within the usable sheet.
    /// </summary>
    private static void FitToGap(GuillotineSheet sheet, double gap)
    {
      double left = sheet.Trim.LeftMm, right = sheet.LengthMm - sheet.Trim.RightMm;
      double top = sheet.Trim.TopMm, bottom = sheet.WidthMm - sheet.Trim.BottomMm;
      double X(double x) => Math.Clamp(x - gap / 2, left, right);
      double Y(double y) => Math.Clamp(y - gap / 2, top, bottom);
      sheet.Cuts = sheet.Cuts.Select(cut => new GuillotineCut { X1 = X(cut.X1), Y1 = Y(cut.Y1), X2 = X(cut.X2), Y2 = Y(cut.Y2), Level = cut.Level }).ToList();
    }

    private static void AddCut(GuillotineSheet sheet, Frame frame, double u1, double v1, double u2, double v2, int level)
    {
      sheet.Cuts.Add(frame.Turned
        ? new GuillotineCut { X1 = v1, Y1 = u1, X2 = v2, Y2 = u2, Level = level }
        : new GuillotineCut { X1 = u1, Y1 = v1, X2 = u2, Y2 = v2, Level = level });
    }

    /// <summary>
    /// An offcut's far sides end at the next cut's gap or the usable edge, a gap short of the grown frame. It starts at
    /// the cut of <paramref name="level"/> that made it: even levels run across the strips (constant U), odd levels along.
    /// </summary>
    private static void AddOffcut(GuillotineSheet sheet, Frame frame, double u, double v, double across, double along, int level)
    {
      across -= frame.Gap;
      along -= frame.Gap;
      if (across <= Eps || along <= Eps)
        return;
      var (x, y, width, height) = frame.Turned ? (v, u, along, across) : (u, v, across, along);
      sheet.Offcuts.Add(new SheetGap
      {
        Id = "G" + (sheet.Offcuts.Count + 1), X = x, Y = y, Width = width, Height = height,
        CutLevel = level, CutVertical = (level % 2 == 0) != frame.Turned
      });
    }

    /// <summary>The piece area over the largest usable sheet, rounded up. No layout uses fewer sheets.</summary>
    private int FewestSheetsPossible(List<BoardDefinition> boards, List<GuillotinePiece> pieces)
    {
      double usable = boards.Max(board => new Frame(board, Trim, false, GapMm).UsableArea);
      double area = pieces.Sum(piece => piece.LengthMm * piece.WidthMm);
      return usable > 0 ? (int)Math.Ceiling(area / usable - 0.000001) : 0;
    }

    private bool FitsABoard(GuillotinePiece piece, List<BoardDefinition> boards)
    {
      foreach (var board in boards)
      {
        foreach (bool turned in Directions(board))
        {
          var frame = new Frame(board, Trim, turned, GapMm);
          if (Orientations(piece, turned).Any(o => o.Across <= frame.UsableLength + Eps && o.Along <= frame.UsableWidth + Eps))
            return true;
        }
      }
      return false;
    }

    private void Validate(List<BoardDefinition> boards, List<GuillotinePiece> pieces)
    {
      if (MinBreakDistanceMm < 0)
        throw new InvalidOperationException("The minimum break distance cannot be below zero.");
      if (GapMm < 0)
        throw new InvalidOperationException("The gap between pieces cannot be below zero.");
      if (MaxCutLevel < 2)
        throw new InvalidOperationException("At least two cut stages are needed: strips and the cuts across them.");
      if (boards.Count == 0)
        throw new InvalidOperationException("At least one sheet size is needed.");
      foreach (var board in boards)
      {
        if (board.EffectiveQuantity <= 0 || new Frame(board, Trim, false, 0).UsableArea <= 0)
          throw new InvalidOperationException(
            $"Sheet '{board.Name ?? board.Id}' needs a length, width and quantity above zero, and room inside the trim.");
      }
      foreach (var piece in pieces)
      {
        if (piece.LengthMm <= 0 || piece.WidthMm <= 0)
          throw new InvalidOperationException($"Piece '{piece.Id}' needs a length and width above zero.");
      }
    }

    /// <summary>How a build values a piece in a strip: its area to a power, times the price of its size.</summary>
    private sealed class Strategy
    {
      private readonly Dictionary<PieceType, double> _prices;

      public Strategy(double valuePower, Dictionary<PieceType, double> prices)
      {
        ValuePower = valuePower;
        _prices = prices;
      }

      /// <summary>Above 1, larger pieces weigh more, so they are placed while there is room to choose.</summary>
      public double ValuePower { get; }

      public double Value(Placement placement) =>
        Math.Pow(placement.Across * placement.Along, ValuePower)
        * (_prices.TryGetValue(PieceType.Of(placement.Piece), out double price) ? price : 1)
        * (placement.Piece.Priority ? PriorityWeight : 1);

      /// <summary>Each size's price moves halfway to one over the fill of the sheets it was cut from.</summary>
      public Strategy Corrected(GuillotineResult result, double[] usableAreas)
      {
        var seen = new Dictionary<PieceType, (double Sum, int Count)>();
        foreach (var sheet in result.Sheets)
        {
          double fill = sheet.SourcePieces.Sum(piece => piece.LengthMm * piece.WidthMm) / usableAreas[sheet.BoardIndex];
          if (fill <= 0)
            continue;
          foreach (var piece in sheet.SourcePieces)
          {
            var type = PieceType.Of(piece);
            seen.TryGetValue(type, out var total);
            seen[type] = (total.Sum + 1 / fill, total.Count + 1);
          }
        }
        var prices = new Dictionary<PieceType, double>(_prices);
        foreach (var (type, total) in seen)
          prices[type] = 0.5 * (prices.TryGetValue(type, out double old) ? old : 1) + 0.5 * total.Sum / total.Count;
        return new Strategy(ValuePower, prices);
      }
    }

    /// <summary>Pieces that lay out alike. A group's inner cut depth decides where it may lie, so it is part of the type.</summary>
    private readonly record struct PieceType(double Length, double Width, bool CanRotate, bool Priority, int GroupDepth)
    {
      public static PieceType Of(GuillotinePiece piece) =>
        new PieceType(Math.Round(piece.LengthMm, 1), Math.Round(piece.WidthMm, 1), piece.CanRotate, piece.Priority, piece.Block?.Depth ?? -1);
    }

    /// <summary>A piece as laid in a strip: across the strip and along it.</summary>
    private sealed record Placement(GuillotinePiece Piece, double Across, double Along);

    private sealed class Column
    {
      public Column(double width, Placement first)
      {
        Width = width;
        Stack.Add(first);
      }

      public double Width { get; }
      public List<Placement> Stack { get; } = new List<Placement>();
    }

    private sealed class Row
    {
      public Row(double height) => Height = height;

      public double Height { get; }
      public List<Column> Columns { get; } = new List<Column>();
    }

    private sealed class Strip
    {
      public Strip(double width) => Width = width;

      public double Width { get; }
      public List<Row> Rows { get; } = new List<Row>();
      public HashSet<GuillotinePiece> Pieces { get; } = new HashSet<GuillotinePiece>();
      public double Area { get; set; }

      /// <summary>The strategy's value of the pieces, which plans the sheet.</summary>
      public double Value { get; set; }
    }

    private sealed class SheetPlan
    {
      public SheetPlan(int boardIndex, Frame frame, bool preferTurnedFrame)
      {
        BoardIndex = boardIndex;
        Frame = frame;
        PreferTurnedFrame = preferTurnedFrame;
      }

      public int BoardIndex { get; }
      public Frame Frame { get; }
      /// <summary>
      /// From <see cref="PreferHorizontalRipsFirst"/> (beam saw): prefer turned frame so level‑0 cuts are horizontal.
      /// </summary>
      public bool PreferTurnedFrame { get; }
      public List<Strip> Strips { get; } = new List<Strip>();
      public double FreeLength { get; set; }

      private double Fill => Strips.Sum(strip => strip.Area) / Frame.UsableArea;

      private double PriorityArea => Strips.SelectMany(strip => strip.Pieces).Where(piece => piece.Priority).Sum(piece => piece.LengthMm * piece.WidthMm);

      public HashSet<GuillotinePiece> Pieces() => new HashSet<GuillotinePiece>(Strips.SelectMany(strip => strip.Pieces));

      /// <summary>
      /// More priority area first; then fuller. Beam saw (<see cref="PreferTurnedFrame"/>): equal fill prefers a
      /// turned frame (horizontal rips first — sheets load that way; vertical first needs a sheet turn) before
      /// remnant. Glass/CNC: equal fill then larger remnant, then prefer unturned.
      /// </summary>
      public bool IsBetterThan(SheetPlan other)
      {
        if (other == null)
          return true;
        if (Math.Abs(PriorityArea - other.PriorityArea) > Eps)
          return PriorityArea > other.PriorityArea;
        if (Math.Abs(Fill - other.Fill) > 1e-9)
          return Fill > other.Fill;
        // Beam saw only: do not let remnant pick a vertical-first (sheet-turn) layout when fill is equal.
        if (PreferTurnedFrame && Frame.Turned != other.Frame.Turned)
          return Frame.Turned;
        double remnant = FreeLength * Frame.UsableWidth;
        double otherRemnant = other.FreeLength * other.Frame.UsableWidth;
        if (Math.Abs(remnant - otherRemnant) > Eps)
          return remnant > otherRemnant;
        // Glass / CNC: prefer unturned when still tied.
        if (!PreferTurnedFrame && Frame.Turned != other.Frame.Turned)
          return !Frame.Turned;
        return false;
      }
    }

    /// <summary>
    /// The sheet as the optimiser lays it out: strips run across U from the start edge, rows along V from the top.
    /// Not turned, U is the sheet length (X); turned, U is the sheet width (Y).
    /// </summary>
    private sealed class Frame
    {
      public Frame(BoardDefinition board, SheetTrim trim, bool turned, double gap)
      {
        trim = board.Trim ?? trim;
        Gap = Math.Max(0, gap);
        double left = Math.Max(0, trim?.LeftMm ?? 0), right = Math.Max(0, trim?.RightMm ?? 0);
        double topTrim = Math.Max(0, trim?.TopMm ?? 0), bottomTrim = Math.Max(0, trim?.BottomMm ?? 0);
        SheetTrim = new SheetTrim { LeftMm = left, RightMm = right, TopMm = topTrim, BottomMm = bottomTrim };
        Turned = turned;
        Length = turned ? board.WidthMm : board.LengthMm;
        Width = turned ? board.LengthMm : board.WidthMm;
        StartU = turned ? topTrim : left;
        EndU = turned ? bottomTrim : right;
        StartV = turned ? left : topTrim;
        EndV = turned ? right : bottomTrim;
      }

      public SheetTrim SheetTrim { get; }
      public bool Turned { get; }
      public double Length { get; }
      public double Width { get; }
      public double StartU { get; }
      public double EndU { get; }
      public double StartV { get; }
      public double EndV { get; }

      /// <summary>The cut gap. The usable sheet is laid out a gap longer and wider, as each piece is.</summary>
      public double Gap { get; }

      public double UsableLength => Length - StartU - EndU + Gap;
      public double UsableWidth => Width - StartV - EndV + Gap;
      public double UsableArea => Math.Max(0, UsableLength) * Math.Max(0, UsableWidth);

      public static double[] UsableAreaOf(List<BoardDefinition> boards, SheetTrim trim, double gap) =>
        boards.Select(board => new Frame(board, trim, false, gap).UsableArea).ToArray();
    }
  }
}
