namespace S3Ware.Guillotine.Tests
{
  public class ReadmeExampleTests
  {
    [Fact]
    public void The_readme_example_lays_out_both_pieces()
    {
      var boards = new[] { new BoardDefinition { LengthMm = 3660, WidthMm = 2440, QuantityUnlimited = true } };
      var pieces = new[]
      {
        new GuillotinePiece { Id = "A", LengthMm = 1200, WidthMm = 800 },
        new GuillotinePiece { Id = "B", LengthMm = 900, WidthMm = 600, CanRotate = false },
      };

      var optimiser = new GuillotineOptimiser
      {
        Trim = new SheetTrim { TopMm = 15, LeftMm = 15, BottomMm = 15, RightMm = 15 },
        MinBreakDistanceMm = 15,
        TimeLimitSeconds = 10,
      };

      GuillotineResult result = optimiser.Optimise(boards, pieces);

      Assert.Empty(result.UnplacedPieces);
      var sheet = Assert.Single(result.Sheets);
      Assert.Equal(2, sheet.Pieces.Count);
    }
  }
}
