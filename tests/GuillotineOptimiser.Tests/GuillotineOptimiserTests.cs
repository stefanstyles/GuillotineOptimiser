using S3Ware.Guillotine;

namespace S3Ware.Guillotine.Tests
{
  public class GuillotineOptimiserTests
  {
    [Fact]
    public void A_thin_offcut_between_two_cuts_cannot_break()
    {
      var sheet = Sheet(SheetTrim.None(), Vertical(500), Vertical(510));
      sheet.Offcuts.Add(new SheetGap { X = 500, Y = 0, Width = 10, Height = 1000 });

      Assert.Single(sheet.FindUnbreakableOffcuts(20));
    }

    [Fact]
    public void The_last_sheet_share_of_spare_is_its_pieces_over_the_room_left_inside_the_trims_of_the_others()
    {
      var first = Sheet(new SheetTrim { RightMm = 100 });
      first.Pieces.Add(new PlacedPart { Width = 700, Height = 1000 });
      var last = Sheet(SheetTrim.None());
      last.Pieces.Add(new PlacedPart { Width = 100, Height = 500 });
      var result = new GuillotineResult { Sheets = { first, last } };

      // First sheet: 0.9 m² inside the trim, 0.7 m² of pieces, 0.2 spare. Last sheet: 0.05 m².
      Assert.Equal(0.25, result.LastSheetShareOfSpare(), 6);
      Assert.Equal(double.PositiveInfinity, new GuillotineResult { Sheets = { last } }.LastSheetShareOfSpare());
    }

    [Fact]
    public void The_search_ends_when_a_sheet_fewer_is_out_of_reach()
    {
      // Two 700 squares need two 1000 sheets though their area fits one: the last holds 0.49 m² against 0.51 spare.
      var boards = new[] { new BoardDefinition { LengthMm = 1000, WidthMm = 1000, QuantityUnlimited = true } };
      var pieces = Enumerable.Range(1, 2).Select(i => new GuillotinePiece { Id = $"P{i}", LengthMm = 700, WidthMm = 700 }).ToList();

      var smart = new GuillotineOptimiser { MinBreakDistanceMm = 20, StallRounds = 3, ReachShare = 0.3 }.Optimise(boards, pieces);
      var off = new GuillotineOptimiser { MinBreakDistanceMm = 20, StallRounds = 3, ReachShare = 0 }.Optimise(boards, pieces);

      Assert.Equal(2, smart.Sheets.Count);
      Assert.Equal(GuillotineStop.SheetFewerOutOfReach, smart.StoppedBy);
      Assert.DoesNotContain(smart.Builds, build => build.Rework);
      Assert.Equal(2, off.Sheets.Count);
      Assert.Equal(GuillotineStop.Stalled, off.StoppedBy);
      Assert.Equal(3, off.Builds.Count(build => build.Rework));
    }

    [Fact]
    public void A_piece_larger_than_every_sheet_inside_its_trim_says_so()
    {
      var boards = new[] { Board(1000, 1000) };
      var trim = new SheetTrim { LeftMm = 20, RightMm = 20, TopMm = 20, BottomMm = 20 };
      var big = Piece("BIG", 990, 500);

      var result = new GuillotineOptimiser { Trim = trim, MinBreakDistanceMm = 20 }.Optimise(boards, new[] { big, Piece("OK", 400, 400) });

      Assert.Same(big, Assert.Single(result.UnplacedPieces));
      Assert.Contains("larger than every sheet size", result.UnplacedReason(big, boards, trim, true));
    }

    [Fact]
    public void A_piece_left_when_the_sheet_quantities_are_used_says_the_sheets_ran_out()
    {
      var boards = new[] { new BoardDefinition { LengthMm = 1000, WidthMm = 1000, Quantity = 1 } };
      var pieces = new[] { Piece("A", 700, 700), Piece("B", 700, 700) };

      var result = new GuillotineOptimiser { MinBreakDistanceMm = 20 }.Optimise(boards, pieces);

      var left = Assert.Single(result.UnplacedPieces);
      Assert.StartsWith("The sheets ran out", result.UnplacedReason(left, boards, SheetTrim.None(), true));
    }

    [Fact]
    public void A_thin_offcut_against_a_trim_breaks_off_with_the_trim()
    {
      var sheet = Sheet(new SheetTrim { RightMm = 20 }, Vertical(970));
      sheet.Offcuts.Add(new SheetGap { X = 970, Y = 0, Width = 10, Height = 1000 });

      Assert.Empty(sheet.FindUnbreakableOffcuts(20));
    }

    [Theory]
    [InlineData("top", 1)]
    [InlineData("left", 1)]
    [InlineData("bottom", 0)]
    [InlineData("right", 0)]
    public void A_thin_offcut_against_the_front_or_left_trim_cannot_break(string edge, int unbreakable)
    {
      var trim = new SheetTrim { TopMm = 40, BottomMm = 40, LeftMm = 40, RightMm = 40 };
      var (cut, offcut) = edge switch
      {
        "top" => (Horizontal(48), new SheetGap { X = 40, Y = 40, Width = 920, Height = 8 }),
        "left" => (Vertical(48), new SheetGap { X = 40, Y = 40, Width = 8, Height = 920 }),
        "bottom" => (Horizontal(952), new SheetGap { X = 40, Y = 952, Width = 920, Height = 8 }),
        _ => (Vertical(952), new SheetGap { X = 952, Y = 40, Width = 8, Height = 920 }),
      };
      var sheet = Sheet(trim, cut);
      sheet.Offcuts.Add(offcut);

      Assert.Equal(unbreakable, sheet.FindUnbreakableOffcuts(30).Count);
    }

    [Fact]
    public void Glass_optimise_leaves_no_strip_under_the_break_distance_against_the_front_or_left_trim()
    {
      // An 860 x 2162 in a 2170 strip left 8 mm, moved to the front against the 40 mm
      // top trim. The trim broke off first, so the 8 mm stayed on the piece. Against the back or right trim it comes
      // off with the trim, at least the break distance together.
      var trim = new SheetTrim { TopMm = 40, BottomMm = 40, LeftMm = 40, RightMm = 40 };
      var optimiser = new GuillotineOptimiser { Trim = trim, MinBreakDistanceMm = 30, TimeLimitSeconds = 5 };
      var pieces = new[] { Piece("A", 1082, 2002), Piece("B", 1068, 2002), Piece("C", 860, 2162), Piece("D", 699, 2145) };

      var result = optimiser.Optimise(new[] { Board(3660, 2250) }, pieces);

      Assert.Empty(result.UnplacedPieces);
      foreach (var sheet in result.Sheets)
      {
        Assert.Empty(sheet.FindUnbreakableOffcuts(30));
        Assert.DoesNotContain(sheet.Offcuts, offcut =>
          (offcut.Height < 30 && (offcut.Height + trim.BottomMm < 30 || Math.Abs(offcut.Y + offcut.Height - (sheet.WidthMm - trim.BottomMm)) > 0.5))
          || (offcut.Width < 30 && (offcut.Width + trim.RightMm < 30 || Math.Abs(offcut.X + offcut.Width - (sheet.LengthMm - trim.RightMm)) > 0.5)));
        AssertGuillotine(sheet, optimiser.MaxCutLevel);
      }
    }

    [Fact]
    public void A_thin_offcut_against_an_untrimmed_edge_cannot_break()
    {
      var sheet = Sheet(SheetTrim.None(), Horizontal(990));
      sheet.Offcuts.Add(new SheetGap { X = 0, Y = 990, Width = 1000, Height = 10 });

      Assert.Single(sheet.FindUnbreakableOffcuts(20));
    }

    [Fact]
    public void A_thin_offcut_with_no_cut_on_one_side_joins_the_next_offcut()
    {
      var sheet = Sheet(SheetTrim.None(), Vertical(500));
      sheet.Offcuts.Add(new SheetGap { X = 500, Y = 0, Width = 10, Height = 1000 });

      Assert.Empty(sheet.FindUnbreakableOffcuts(20));
    }

    [Fact]
    public void An_offcut_at_the_break_distance_breaks()
    {
      var sheet = Sheet(SheetTrim.None(), Vertical(500), Vertical(520));
      sheet.Offcuts.Add(new SheetGap { X = 500, Y = 0, Width = 20, Height = 1000 });

      Assert.Empty(sheet.FindUnbreakableOffcuts(20));
    }

    [Fact]
    public void Staged_optimiser_leaves_no_leftover_under_a_millimetre_beside_a_trim()
    {
      // 1770.5 + 1167 + 682 left 0.5 mm inside a 20 mm trim, which breaks off with the trim but scores a
      // cut half a millimetre from it.
      var optimiser = new GuillotineOptimiser { Trim = new SheetTrim { TopMm = 20, LeftMm = 20, BottomMm = 20, RightMm = 20 }, MinBreakDistanceMm = 20, AllowTurnedSheets = false };
      var pieces = new[] { Piece("A", 1770.5, 2064), Piece("B", 1167, 2084), Piece("C", 682, 2064) };
      foreach (var piece in pieces)
        piece.CanRotate = false;

      var result = optimiser.Optimise(new[] { Board(3660, 2440) }, pieces);

      Assert.Empty(result.UnplacedPieces);
      Assert.Equal(3, result.Sheets.Sum(sheet => sheet.Pieces.Count));
      foreach (var sheet in result.Sheets)
      {
        Assert.DoesNotContain(sheet.Offcuts, offcut => Math.Min(offcut.Width, offcut.Height) < 1);
        AssertGuillotine(sheet, 4);
      }
    }

    [Fact]
    public void Staged_optimiser_builds_rows_of_several_pieces_side_by_side()
    {
      // A better layout cuts these from 2 sheets with rows such as 632 + 692 + 632 under a 1990 x 1058, the shorter
      // pieces trimmed by a W cut. One piece per row with the sides filled afterwards needs 3. The 2 come from a rework
      // the 0.3 reach stop does not wait for, so it is off here.
      var optimiser = new GuillotineOptimiser { MinBreakDistanceMm = 15, AllowTurnedSheets = false, ReachShare = 0 };
      var pieces = new List<GuillotinePiece>();
      void Add(int count, double length, double width)
      {
        for (int i = 0; i < count; i++)
          pieces.Add(Piece($"{length}x{width}-{i}", length, width));
      }
      Add(6, 761, 631);
      Add(6, 692, 632);
      Add(6, 690, 632);
      Add(2, 1058, 1990);
      Add(2, 753, 1989);

      var result = optimiser.Optimise(new[] { Board(3660, 2440) }, pieces);

      Assert.Empty(result.UnplacedPieces);
      Assert.Equal(2, result.Sheets.Count);
      Assert.Equal(pieces.Count, result.Sheets.Sum(sheet => sheet.Pieces.Count));
      Assert.All(result.Sheets, sheet => Assert.Empty(sheet.FindUnbreakableOffcuts(15)));
      Assert.All(result.Sheets, sheet => AssertGuillotine(sheet, 4));
    }

    [Fact]
    public void Staged_optimiser_takes_a_strip_width_from_two_pieces_side_by_side()
    {
      // A better layout cuts these from 3 sheets with strip widths such as 1044 + 1024 = 2068 that no single piece
      // has. Strip widths from single pieces only need 4.
      var optimiser = new GuillotineOptimiser { Trim = new SheetTrim { TopMm = 20, LeftMm = 20, BottomMm = 20, RightMm = 20 }, MinBreakDistanceMm = 20 };
      var sizes = new (double Length, double Width)[]
      {
        (667, 1337), (644, 1024), (2371, 1102), (544, 1024), (983, 1058), (2108, 908), (744, 1044),
        (957, 976), (1969, 902), (2002, 963), (2352, 1002), (2568, 899), (1362, 2357), (2093, 922),
      };
      var pieces = sizes.Select((size, i) => Piece($"P{i}", size.Length, size.Width)).ToList();

      var result = optimiser.Optimise(new[] { Board(3660, 2440) }, pieces);

      Assert.Empty(result.UnplacedPieces);
      Assert.Equal(3, result.Sheets.Count);
      Assert.Equal(pieces.Count, result.Sheets.Sum(sheet => sheet.Pieces.Count));
      Assert.All(result.Sheets, sheet => Assert.Empty(sheet.FindUnbreakableOffcuts(20)));
      Assert.All(result.Sheets, sheet => AssertGuillotine(sheet, 4));
    }

    [Fact]
    public void Staged_optimiser_gives_near_equal_narrow_pieces_their_own_strips()
    {
      // Lining these up side by side leaves 1 mm slivers; one strip per piece cuts all three from one sheet.
      var optimiser = new GuillotineOptimiser { Trim = new SheetTrim { TopMm = 15, LeftMm = 15, BottomMm = 15, RightMm = 15 }, MinBreakDistanceMm = 15 };
      var pieces = new[] { Piece("A", 1666, 637), Piece("B", 1665, 638), Piece("C", 1666, 638) };

      var result = optimiser.Optimise(new[] { Board(3660, 2440) }, pieces);

      var sheet = Assert.Single(result.Sheets);
      Assert.Equal(3, sheet.Pieces.Count);
      Assert.Empty(sheet.FindUnbreakableOffcuts(15));
      AssertGuillotine(sheet, 4);
    }

    [Fact]
    public void Staged_optimiser_at_z_cuts_never_needs_a_w_cut()
    {
      var optimiser = new GuillotineOptimiser { MinBreakDistanceMm = 20, MaxCutLevel = 3 };
      var pieces = Enumerable.Range(0, 30).Select(i => Piece("P" + i, 400 + i * 37 % 900, 300 + i * 53 % 1500)).ToArray();

      var result = optimiser.Optimise(new[] { Board(3660, 2440) }, pieces);

      Assert.Empty(result.UnplacedPieces);
      Assert.Empty(result.Warnings);
      Assert.All(result.Sheets, sheet =>
      {
        Assert.True(sheet.Stages <= 3, $"Sheet needs {sheet.Stages} stages.");
        Assert.Empty(sheet.FindUnbreakableOffcuts(20));
        AssertGuillotine(sheet, 3);
      });
    }

    [Fact]
    public void Staged_optimiser_reports_progress_and_ends_with_the_result()
    {
      var reports = new ProgressList();
      var optimiser = new GuillotineOptimiser { MinBreakDistanceMm = 20, TimeLimitSeconds = 5 };
      var pieces = Enumerable.Range(0, 40).Select(i => Piece("P" + i, 400 + i * 37 % 900, 300 + i * 53 % 1500)).ToArray();

      var result = optimiser.Optimise(new[] { Board(3660, 2440) }, pieces, progress: reports);

      Assert.NotNull(result.StoppedBy);
      var better = reports.Where(report => report.Better).ToList();
      Assert.NotEmpty(better);
      Assert.True(better.Zip(better.Skip(1), (a, b) => b.Sheets <= a.Sheets).All(ok => ok), "A better layout used more sheets.");
      var last = reports[^1];
      Assert.Equal(result.StoppedBy, last.StoppedBy);
      Assert.Single(reports, report => report.StoppedBy != null);
      Assert.Equal((result.Sheets.Count, result.FewestSheetsPossible, result.SettingsTried), (last.Sheets, last.FewestSheetsPossible, last.Builds));
      Assert.Equal(result.WastePercent, last.WastePercent, 6);
      Assert.InRange(last.WastePercent, 0, 100);
      Assert.Equal(last.AtFewestSheets, result.StoppedBy == GuillotineStop.FewestSheetsPossible);
    }

    [Fact]
    public void Staged_optimiser_stopped_before_it_starts_still_finishes_its_first_layout()
    {
      using var stop = new CancellationTokenSource();
      stop.Cancel();
      var pieces = Enumerable.Range(0, 40).Select(i => Piece("P" + i, 400 + i * 37 % 900, 300 + i * 53 % 1500)).ToArray();

      var result = new GuillotineOptimiser { MinBreakDistanceMm = 20 }.Optimise(new[] { Board(3660, 2440) }, pieces, stop.Token);

      Assert.Equal(1, result.SettingsTried);
      Assert.Empty(result.UnplacedPieces);
      Assert.Equal(pieces.Length, result.Sheets.Sum(sheet => sheet.Pieces.Count));
      Assert.Contains(result.StoppedBy, new GuillotineStop?[] { GuillotineStop.Stopped, GuillotineStop.FewestSheetsPossible });
    }

    [Fact]
    public void Staged_optimiser_stopped_part_way_keeps_its_best_layout()
    {
      using var stop = new CancellationTokenSource();
      var reports = new ProgressList(report => { if (report.Better) stop.Cancel(); });
      var pieces = Enumerable.Range(0, 120).Select(i => Piece("P" + i, 300 + i * 37 % 900, 200 + i * 53 % 1500)).ToArray();

      var result = new GuillotineOptimiser { MinBreakDistanceMm = 20 }.Optimise(new[] { Board(3660, 2440) }, pieces, stop.Token, reports);

      Assert.Equal(1, result.SettingsTried);
      Assert.Equal(pieces.Length, result.Sheets.Sum(sheet => sheet.Pieces.Count));
      Assert.Equal(result.Sheets.Count, reports.First(report => report.Better).Sheets);
    }

    /// <summary>Reports on the optimiser's thread, in order; <see cref="Progress{T}"/> would post them elsewhere.</summary>
    private sealed class ProgressList : List<GuillotineProgress>, IProgress<GuillotineProgress>
    {
      private readonly Action<GuillotineProgress> _then;

      public ProgressList(Action<GuillotineProgress> then = null) => _then = then;

      public void Report(GuillotineProgress value)
      {
        Add(value);
        _then?.Invoke(value);
      }
    }
    [Fact]
    public void Beam_saw_prefers_horizontal_rips_first_when_fill_is_tied()
    {
      // PreferHorizontalRipsFirst loads a turned frame so level-0 cuts are horizontal: the sheet is not turned first,
      // strip turns come next. OrderStripsNarrowestFirst is only the strip breakout order.
      var pieces = new[]
      {
        new GuillotinePiece { Id = "A", LengthMm = 2000, WidthMm = 500, CanRotate = true },
        new GuillotinePiece { Id = "B", LengthMm = 2000, WidthMm = 500, CanRotate = true }
      };
      var beam = new GuillotineOptimiser
      {
        PreferHorizontalRipsFirst = true,
        GapMm = 4.8, MinBreakDistanceMm = 0, MaxCutLevel = 3, AllowTurnedSheets = true,
        OrderStripsNarrowestFirst = true, TimeLimitSeconds = 5
      }.Optimise(new[] { Board(2100, 1200) }, pieces);
      var sheet = Assert.Single(beam.Sheets);
      var first = sheet.Cuts.OrderBy(cut => cut.Level).First();
      Assert.False(first.IsVertical);
    }

    [Fact]
    public void Beam_saw_keeps_horizontal_rips_when_remnant_would_favour_vertical_first()
    {
      // Unequal strip widths: remnant can differ by frame orientation. Beam still picks horizontal level-0
      // (sheets load that way) rather than a vertical-first layout that needs a sheet turn.
      var pieces = new[]
      {
        new GuillotinePiece { Id = "A", LengthMm = 2000, WidthMm = 778, CanRotate = true },
        new GuillotinePiece { Id = "B", LengthMm = 1953, WidthMm = 411, CanRotate = true }
      };
      var beam = new GuillotineOptimiser
      {
        PreferHorizontalRipsFirst = true,
        GapMm = 4.8, MinBreakDistanceMm = 0, MaxCutLevel = 3, AllowTurnedSheets = true,
        OrderStripsNarrowestFirst = true, TimeLimitSeconds = 5
      }.Optimise(new[] { Board(2100, 1200) }, pieces);
      var sheet = Assert.Single(beam.Sheets);
      var first = sheet.Cuts.OrderBy(cut => cut.Level).First();
      Assert.False(first.IsVertical);
    }


    [Fact]
    public void Staged_optimiser_keeps_the_gap_between_pieces_but_not_at_the_sheet_edge()
    {
      var pieces = new[] { Piece("A", 1000, 1000), Piece("B", 1000, 1000) };
      var exact = new GuillotineOptimiser { GapMm = 13, MinBreakDistanceMm = 0, AllowTurnedSheets = false }
        .Optimise(new[] { Board(2013, 1000) }, pieces);
      var sheet = Assert.Single(exact.Sheets);
      Assert.Equal(new[] { 0.0, 1013.0 }, sheet.Pieces.Select(p => p.X).OrderBy(x => x).ToArray());
      Assert.All(sheet.Pieces, p => Assert.Equal((1000.0, 1000.0), (p.Width, p.Height)));
      var cut = Assert.Single(sheet.Cuts);
      Assert.Equal((1006.5, 1006.5, 0.0, 1000.0), (cut.X1, cut.X2, cut.Y1, cut.Y2));

      var tooShort = new GuillotineOptimiser { GapMm = 13, MinBreakDistanceMm = 0, AllowTurnedSheets = false }
        .Optimise(new[] { Board(2012, 1000) }, pieces);
      Assert.Equal(2, tooShort.Sheets.Count);
    }

    [Fact]
    public void Parts_as_wide_as_an_untrimmed_strip_board_fit_it()
    {
      var optimiser = new GuillotineOptimiser
      {
        GapMm = 4.8, MinBreakDistanceMm = 0, MaxCutLevel = 3, TimeLimitSeconds = 5,
        Trim = new SheetTrim { LeftMm = 10, RightMm = 10 }
      };
      var board = Board(2410, 440);
      board.Trim = SheetTrim.None();
      var pieces = new[] { 1766.0, 1470, 710, 2383, 1606, 935, 433 }
        .Select((length, i) => new GuillotinePiece { Id = "P" + i, LengthMm = length, WidthMm = 440, CanRotate = true })
        .ToList();

      var result = optimiser.Optimise(new[] { board }, pieces);

      Assert.Empty(result.UnplacedPieces);
    }

    [Fact]
    public void Staged_optimiser_with_a_gap_and_trim_lays_out_board_apart_and_guillotine()
    {
      const double gap = 13;
      var trim = new SheetTrim { LeftMm = 10, RightMm = 10, TopMm = 10, BottomMm = 10 };
      var optimiser = new GuillotineOptimiser { GapMm = gap, MinBreakDistanceMm = 0, Trim = trim, MaxCutLevel = 4 };
      var pieces = Enumerable.Range(0, 60)
        .Select(i => new GuillotinePiece { Id = "P" + i, LengthMm = 300 + i * 97 % 1900, WidthMm = 100 + i * 61 % 500, CanRotate = i % 4 != 0 })
        .ToArray();

      var result = optimiser.Optimise(new[] { Board(3620, 1810) }, pieces);

      Assert.Empty(result.UnplacedPieces);
      Assert.Equal(pieces.Length, result.Sheets.Sum(sheet => sheet.Pieces.Count));
      Assert.Same(pieces[0], result.Sheets.SelectMany(sheet => sheet.SourcePieces).Single(p => p.Id == "P0"));
      foreach (var sheet in result.Sheets)
      {
        foreach (var p in sheet.Pieces)
        {
          var given = pieces.Single(piece => piece.Id == p.Id);
          Assert.True(p.RotationDegrees == 0 || given.CanRotate, $"{p.Id} turned");
          Assert.Equal(p.RotationDegrees == 0 ? (given.LengthMm, given.WidthMm) : (given.WidthMm, given.LengthMm), (p.Width, p.Height));
          Assert.True(p.X >= 10 - 0.001 && p.Y >= 10 - 0.001 && p.X + p.Width <= 3610 + 0.001 && p.Y + p.Height <= 1800 + 0.001, $"{p.Id} outside the trim");
        }
        for (int i = 0; i < sheet.Pieces.Count; i++)
        {
          for (int j = i + 1; j < sheet.Pieces.Count; j++)
          {
            var a = sheet.Pieces[i];
            var b = sheet.Pieces[j];
            Assert.True(a.X + a.Width + gap <= b.X + 0.001 || b.X + b.Width + gap <= a.X + 0.001
              || a.Y + a.Height + gap <= b.Y + 0.001 || b.Y + b.Height + gap <= a.Y + 0.001, $"{a.Id} and {b.Id} closer than the gap");
          }
        }
        foreach (var o in sheet.Offcuts)
        {
          Assert.True(o.X >= 10 - 0.001 && o.Y >= 10 - 0.001 && o.X + o.Width <= 3610 + 0.001 && o.Y + o.Height <= 1800 + 0.001, $"{o.Id} outside the trim");
          Assert.All(sheet.Pieces, p => Assert.True(o.X + o.Width + gap <= p.X + 0.001 || p.X + p.Width + gap <= o.X + 0.001
            || o.Y + o.Height + gap <= p.Y + 0.001 || p.Y + p.Height + gap <= o.Y + 0.001, $"{o.Id} closer than the gap to {p.Id}"));
        }
        AssertGuillotine(sheet, 4, gap);
      }
    }
    [Fact]
    public void Pieces_a_few_thousandths_apart_are_laid_with_their_shapes_inside_their_boxes()
    {
      var optimiser = new GuillotineOptimiser { MinBreakDistanceMm = 20, MaxCutLevel = 4, Trim = new SheetTrim { LeftMm = 20, RightMm = 20, TopMm = 20, BottomMm = 20 } };
      var sizes = new[] { (881, 1987.006849), (872, 1964), (867, 1987.006936), (867, 1987.0124), (133, 1964), (2064, 202) };
      var pieces = sizes
        .Select((size, i) => new GuillotinePiece
        {
          Id = "P" + i,
          LengthMm = size.Item1,
          WidthMm = size.Item2,
          CanRotate = true,
          Outline =
          {
            OutlineEdge.Line(0, 0, size.Item1, 0), OutlineEdge.Line(size.Item1, 0, size.Item1, size.Item2),
            OutlineEdge.Line(size.Item1, size.Item2, 6, size.Item2), OutlineEdge.Line(6, size.Item2, 0, 0)
          }
        })
        .ToArray();

      var result = optimiser.Optimise(new[] { Board(3660, 2250) }, pieces);

      Assert.Empty(result.UnplacedPieces);
      foreach (var sheet in result.Sheets)
      {
        foreach (var p in sheet.Pieces)
        {
          var given = pieces.Single(piece => piece.Id == p.Id);
          var (width, height) = p.RotationDegrees == 0 ? (given.LengthMm, given.WidthMm) : (given.WidthMm, given.LengthMm);
          Assert.True(Math.Abs(p.Width - width) < 0.05 && Math.Abs(p.Height - height) < 0.05,
            $"{p.Id} turned {p.RotationDegrees} in a {p.Width} x {p.Height} box");
        }
        foreach (var shape in sheet.Shapes())
        {
          var p = sheet.Pieces[shape.PieceIndex];
          Assert.All(shape.Edges, edge => Assert.True(
            Math.Min(edge.X1, edge.X2) >= p.X - 0.05 && Math.Max(edge.X1, edge.X2) <= p.X + p.Width + 0.05
            && Math.Min(edge.Y1, edge.Y2) >= p.Y - 0.05 && Math.Max(edge.Y1, edge.Y2) <= p.Y + p.Height + 0.05,
            $"{p.Id}'s shape runs outside its box"));
        }
      }
    }

    [Fact]
    public void Staged_optimiser_keeps_a_fixed_direction_piece_and_sheet_strips_along_the_length()
    {
      var optimiser = new GuillotineOptimiser { MinBreakDistanceMm = 20, AllowTurnedSheets = false };
      var pieces = new[]
      {
        new GuillotinePiece { Id = "reed", LengthMm = 600, WidthMm = 2000, CanRotate = false },
        Piece("A", 1000, 1200), Piece("B", 1500, 800), Piece("C", 900, 2440)
      };

      var result = optimiser.Optimise(new[] { Board(3660, 2440) }, pieces);

      Assert.Empty(result.UnplacedPieces);
      Assert.Empty(result.Warnings);
      var reed = result.Sheets.SelectMany(sheet => sheet.Pieces).Single(part => part.Id == "reed");
      Assert.Equal((600.0, 2000.0, 0.0), (reed.Width, reed.Height, reed.RotationDegrees));
      foreach (var sheet in result.Sheets)
      {
        Assert.All(sheet.Cuts.Where(cut => cut.Level == 0), cut => Assert.True(cut.IsVertical));
        Assert.Empty(sheet.FindUnbreakableOffcuts(20));
        AssertGuillotine(sheet, 4);
      }
    }

    private static List<GuillotinePiece> Halves(int count) =>
      Enumerable.Range(1, count).Select(i => new GuillotinePiece { Id = "P" + i, LengthMm = 1000, WidthMm = 500, CanRotate = false }).ToList();

    // Two pieces fill a 1000 x 1000 sheet exactly, so the optimiser on its own never lays the 1100 x 1000 older sheet.
    private static BoardDefinition Older(bool useFirst) =>
      new BoardDefinition { LengthMm = 1100, WidthMm = 1000, Quantity = 2, UseFirst = useFirst, Name = "Older" };

    [Fact]
    public void A_size_to_use_first_is_laid_until_its_sheets_run_out()
    {
      var optimiser = new GuillotineOptimiser { MinBreakDistanceMm = 0, AllowTurnedSheets = false, TimeLimitSeconds = 2 };

      var result = optimiser.Optimise(new[] { Board(1000, 1000), Older(true) }, Halves(8));

      Assert.Empty(result.UnplacedPieces);
      Assert.Equal(4, result.Sheets.Count);
      // UseFirst still consumes both Older sheets; GroupSheetsBySize then orders by size (1000 before 1100), so assert
      // by count rather than nest/cut order.
      Assert.Equal(2, result.Sheets.Count(sheet => sheet.LengthMm == 1100));
      Assert.Equal(2, result.Sheets.Count(sheet => sheet.LengthMm == 1000));
    }

    [Fact]
    public void Without_use_first_the_fuller_size_is_laid()
    {
      var optimiser = new GuillotineOptimiser { MinBreakDistanceMm = 0, AllowTurnedSheets = false, TimeLimitSeconds = 2 };

      var result = optimiser.Optimise(new[] { Board(1000, 1000), Older(false) }, Halves(8));

      Assert.All(result.Sheets, sheet => Assert.Equal(1000, sheet.LengthMm));
    }

    [Fact]
    public void A_size_to_use_first_that_takes_no_piece_is_passed_over()
    {
      var small = new BoardDefinition { LengthMm = 300, WidthMm = 300, Quantity = 5, UseFirst = true, Name = "Offcut" };
      var optimiser = new GuillotineOptimiser { MinBreakDistanceMm = 0, AllowTurnedSheets = false, TimeLimitSeconds = 2 };

      var result = optimiser.Optimise(new[] { small, Board(2000, 2000) }, Halves(2));

      Assert.Empty(result.UnplacedPieces);
      Assert.All(result.Sheets, sheet => Assert.Equal(2000, sheet.LengthMm));
    }

    [Fact]
    public void An_unlimited_size_is_not_used_first()
    {
      Assert.False(new BoardDefinition { UseFirst = true, QuantityUnlimited = true }.IsFirstChoice);
      Assert.True(new BoardDefinition { UseFirst = true, Quantity = 3 }.IsFirstChoice);
    }
    [Fact]
    public void A_group_of_drawer_fronts_is_cut_end_to_end_in_slot_order_with_a_cut_between_each()
    {
      var optimiser = new GuillotineOptimiser { GapMm = 4, MinBreakDistanceMm = 0, AllowTurnedSheets = false, TimeLimitSeconds = 5 };
      var pieces = new List<GuillotinePiece>
      {
        InGroup(Piece("F3", 400, 500), "Stack", 3), InGroup(Piece("F1", 400, 500), "Stack", 1), InGroup(Piece("F2", 300, 500), "Stack", 2),
        Piece("A", 900, 600), Piece("B", 700, 300), Piece("C", 500, 450)
      };
      foreach (var piece in pieces.Where(piece => piece.Group.Length > 0))
        piece.CanRotate = false;

      var result = optimiser.Optimise(new[] { Board(2400, 1200) }, pieces);

      Assert.Empty(result.UnplacedPieces);
      var sheet = Assert.Single(result.Sheets);
      var fronts = new[] { "F1", "F2", "F3" }.Select(id => sheet.Pieces.Single(part => part.Id == id)).ToList();
      Assert.All(fronts, front => Assert.Equal(0, front.RotationDegrees));
      Assert.All(fronts, front => Assert.Equal(fronts[0].Y, front.Y, 3));
      Assert.Equal(fronts[0].X + 404, fronts[1].X, 3);
      Assert.Equal(fronts[1].X + 304, fronts[2].X, 3);
      Assert.Contains(sheet.Cuts, cut => cut.IsVertical && Math.Abs(cut.X1 - (fronts[0].X + 402)) < 0.01);
      Assert.Contains(sheet.Cuts, cut => cut.IsVertical && Math.Abs(cut.X1 - (fronts[1].X + 302)) < 0.01);
      Assert.True(sheet.Stages <= 4);
      AssertGuillotine(sheet, 4, 4);
    }

    [Fact]
    public void A_two_by_two_group_lays_out_as_a_grid_on_the_glass_table()
    {
      var optimiser = new GuillotineOptimiser { MinBreakDistanceMm = 20, TimeLimitSeconds = 5 };
      var pieces = new List<GuillotinePiece>
      {
        InGroup(Piece("1", 300, 200), "Grid", 1, "((1,2),(3,4))"), InGroup(Piece("2", 300, 200), "Grid", 2),
        InGroup(Piece("3", 300, 200), "Grid", 3), InGroup(Piece("4", 300, 200), "Grid", 4),
        Piece("A", 1200, 700), Piece("B", 800, 500)
      };

      var result = optimiser.Optimise(new[] { Board(2000, 1500) }, pieces);

      Assert.Empty(result.UnplacedPieces);
      var sheet = Assert.Single(result.Sheets);
      var grid = new[] { "1", "2", "3", "4" }.Select(id => sheet.Pieces.Single(part => part.Id == id)).ToList();
      Assert.Equal(4, grid.Select(part => (Math.Round(part.X), Math.Round(part.Y))).Distinct().Count());
      Assert.Equal(2, grid.Select(part => Math.Round(part.X)).Distinct().Count());
      Assert.Equal(2, grid.Select(part => Math.Round(part.Y)).Distinct().Count());
      double left = grid.Min(part => part.X), top = grid.Min(part => part.Y);
      Assert.Equal(600, grid.Max(part => part.X + part.Width) - left, 3);
      Assert.Equal(400, grid.Max(part => part.Y + part.Height) - top, 3);
      AssertGuillotine(sheet, 4);
    }

    [Fact]
    public void A_group_narrower_in_one_slot_has_its_waste_trimmed_off()
    {
      var optimiser = new GuillotineOptimiser { GapMm = 4, MinBreakDistanceMm = 0, TimeLimitSeconds = 5 };
      var pieces = new List<GuillotinePiece>
      {
        InGroup(Piece("1", 500, 300), "Pair", 1), InGroup(Piece("2", 500, 200), "Pair", 2), Piece("A", 1000, 800)
      };

      var result = optimiser.Optimise(new[] { Board(2400, 1200) }, pieces);

      Assert.Empty(result.UnplacedPieces);
      var sheet = Assert.Single(result.Sheets);
      AssertGuillotine(sheet, 4, 4);
      var first = sheet.Pieces.Single(part => part.Id == "1");
      var second = sheet.Pieces.Single(part => part.Id == "2");
      Assert.True(Math.Abs(first.X - second.X) < 0.01 || Math.Abs(first.Y - second.Y) < 0.01);
    }

    [Fact]
    public void A_group_that_needs_cuts_deeper_than_the_table_is_not_laid()
    {
      var optimiser = new GuillotineOptimiser { MinBreakDistanceMm = 20, MaxCutLevel = 2, TimeLimitSeconds = 5 };
      var pieces = new List<GuillotinePiece>
      {
        InGroup(Piece("1", 300, 200), "Side", 1, "(((1,2)))"), InGroup(Piece("2", 300, 200), "Side", 2), Piece("A", 500, 500)
      };

      var result = optimiser.Optimise(new[] { Board(2000, 1500) }, pieces);

      Assert.Equal(new[] { "1", "2" }, result.UnplacedPieces.Select(piece => piece.Id).OrderBy(id => id));
      Assert.Contains(result.Warnings, warning => warning.Contains("Side"));
      Assert.Contains(result.Sheets.Single().Pieces, part => part.Id == "A");
    }

    [Fact]
    public void A_glass_group_leaving_waste_too_thin_to_break_is_not_laid()
    {
      var optimiser = new GuillotineOptimiser { MinBreakDistanceMm = 20, TimeLimitSeconds = 5 };
      var pieces = new List<GuillotinePiece> { InGroup(Piece("1", 300, 200), "Thin", 1), InGroup(Piece("2", 300, 190), "Thin", 2) };

      var result = optimiser.Optimise(new[] { Board(2000, 1500) }, pieces);

      Assert.Equal(2, result.UnplacedPieces.Count);
      Assert.Contains(result.Warnings, warning => warning.Contains("Thin"));
    }

    [Theory]
    [InlineData("(1,2,4)")]
    [InlineData("(1,2")]
    [InlineData("1,2,3")]
    [InlineData("(1,1,2)")]
    public void A_group_layout_that_does_not_match_its_slots_is_refused(string layout)
    {
      var pieces = new[] { InGroup(Piece("1", 300, 200), "G", 1, layout), InGroup(Piece("2", 300, 200), "G", 2), InGroup(Piece("3", 300, 200), "G", 3) };

      Assert.Throws<InvalidOperationException>(() => new GuillotineOptimiser().Optimise(new[] { Board(2000, 1500) }, pieces));
    }

    private static GuillotinePiece InGroup(GuillotinePiece piece, string group, int slot, string layout = "")
    {
      piece.Group = group;
      piece.GroupSlot = slot;
      piece.GroupLayout = layout;
      return piece;
    }

    private static GuillotineSheet Sheet(SheetTrim trim, params GuillotineCut[] cuts) => new GuillotineSheet
    {
      LengthMm = 1000,
      WidthMm = 1000,
      Trim = trim,
      Cuts = cuts.ToList()
    };

    private static GuillotineCut Vertical(double x) => new GuillotineCut { X1 = x, Y1 = 0, X2 = x, Y2 = 1000 };

    private static GuillotineCut Horizontal(double y) => new GuillotineCut { X1 = 0, Y1 = y, X2 = 1000, Y2 = y };

    private static GuillotinePiece Piece(string id, double length, double width) =>
      new GuillotinePiece { Id = id, LengthMm = length, WidthMm = width };

    private static BoardDefinition Board(double length, double width) =>
      new BoardDefinition { LengthMm = length, WidthMm = width, QuantityUnlimited = true, Name = "Glass" };

    /// <summary>
    /// The sheet breaks out by straight cuts only: each region splits along a listed cut that runs edge to edge
    /// and crosses no piece, until each region is one piece. A change of cut direction is a new stage.
    /// </summary>
    /// <summary>
    /// With a cut gap, a piece's region is the piece plus half the gap on a side, and a leftover narrower than the gap
    /// is not cut off: up to one and a half gaps.
    /// </summary>
    private static void AssertGuillotine(GuillotineSheet sheet, int maxStages, double gapMm = 0)
    {
      var trim = sheet.Trim;
      var region = new Rect(trim.LeftMm, trim.TopMm, sheet.LengthMm - trim.RightMm, sheet.WidthMm - trim.BottomMm);
      var pieces = sheet.Pieces.Select(p => new Rect(p.X, p.Y, p.X + p.Width, p.Y + p.Height)).ToList();
      int stages = Split(region, pieces, sheet.Cuts, null, 1.5 * gapMm);
      Assert.True(stages <= maxStages,
        $"Sheet needs {stages} cut stages; the table takes {maxStages}. Cuts: " +
        string.Join("; ", sheet.Cuts.Select(c => $"L{c.Level} ({c.X1},{c.Y1})-({c.X2},{c.Y2})")) +
        " Pieces: " + string.Join("; ", sheet.Pieces.Select(p => $"({p.X},{p.Y}) {p.Width}x{p.Height}")) +
        $" Trim L{trim.LeftMm} T{trim.TopMm} R{trim.RightMm} B{trim.BottomMm}");
    }

    private static int Split(Rect region, List<Rect> allPieces, List<GuillotineCut> cuts, bool? parentVertical, double slack)
    {
      const double tol = 0.5;
      double fit = tol + slack;
      var inside = allPieces.Where(p => p.X1 >= region.X1 - tol && p.X2 <= region.X2 + tol && p.Y1 >= region.Y1 - tol && p.Y2 <= region.Y2 + tol).ToList();
      if (inside.Count == 0)
        return 0;
      if (inside.Count == 1 && Math.Abs(inside[0].X1 - region.X1) < fit && Math.Abs(inside[0].X2 - region.X2) < fit
        && Math.Abs(inside[0].Y1 - region.Y1) < fit && Math.Abs(inside[0].Y2 - region.Y2) < fit)
        return 0;

      foreach (var cut in cuts.OrderBy(c => c.Level))
      {
        bool vertical = cut.IsVertical;
        double at = vertical ? cut.X1 : cut.Y1;
        double start = vertical ? Math.Min(cut.Y1, cut.Y2) : Math.Min(cut.X1, cut.X2);
        double end = vertical ? Math.Max(cut.Y1, cut.Y2) : Math.Max(cut.X1, cut.X2);
        double low = vertical ? region.X1 : region.Y1, high = vertical ? region.X2 : region.Y2;
        double from = vertical ? region.Y1 : region.X1, to = vertical ? region.Y2 : region.X2;
        if (at <= low + tol || at >= high - tol || start > from + tol || end < to - tol)
          continue;
        if (inside.Any(p => vertical ? p.X1 < at - tol && p.X2 > at + tol : p.Y1 < at - tol && p.Y2 > at + tol))
          continue;

        var first = vertical ? region with { X2 = at } : region with { Y2 = at };
        var second = vertical ? region with { X1 = at } : region with { Y1 = at };
        int stage = parentVertical == vertical ? 0 : 1;
        int below = Math.Max(Split(first, inside, cuts, vertical, slack), Split(second, inside, cuts, vertical, slack));
        return stage + below;
      }

      Assert.Fail($"Region {region} with {inside.Count} piece(s) has no straight cut that frees a piece. Pieces: "
        + string.Join("; ", inside) + " Cuts touching: " + string.Join("; ", cuts
          .Where(c => Math.Max(c.X1, c.X2) >= region.X1 - tol && Math.Min(c.X1, c.X2) <= region.X2 + tol && Math.Max(c.Y1, c.Y2) >= region.Y1 - tol && Math.Min(c.Y1, c.Y2) <= region.Y2 + tol)
          .Select(c => $"L{c.Level} ({c.X1},{c.Y1})-({c.X2},{c.Y2})")));
      return 0;
    }

    private sealed record Rect(double X1, double Y1, double X2, double Y2);
  }
}
