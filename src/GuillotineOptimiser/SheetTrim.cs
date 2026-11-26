using System;
using System.Collections.Generic;
using System.Linq;

namespace S3Ware.Guillotine
{
  /// <summary>
  /// Strip left at each sheet edge, in mm. Top and left (the 0,0 corner of the layout) are the edges the board is
  /// pushed against on the machine, so the cutter always takes a strip off them. Bottom and right allow for a sheet
  /// that is a little under its stated size.
  /// </summary>
  public sealed class SheetTrim
  {
    public double TopMm { get; set; }
    public double LeftMm { get; set; }
    public double BottomMm { get; set; }
    public double RightMm { get; set; }

    public static SheetTrim None() => new SheetTrim();

    /// <summary>The largest trim on each edge, so one layout suits every machine.</summary>
    public static SheetTrim Largest(IEnumerable<SheetTrim> trims)
    {
      var list = (trims ?? Enumerable.Empty<SheetTrim>()).Where(trim => trim != null).ToList();
      return new SheetTrim
      {
        TopMm = list.Select(trim => Math.Max(0, trim.TopMm)).DefaultIfEmpty(0).Max(),
        LeftMm = list.Select(trim => Math.Max(0, trim.LeftMm)).DefaultIfEmpty(0).Max(),
        BottomMm = list.Select(trim => Math.Max(0, trim.BottomMm)).DefaultIfEmpty(0).Max(),
        RightMm = list.Select(trim => Math.Max(0, trim.RightMm)).DefaultIfEmpty(0).Max()
      };
    }

    /// <summary>The board the layout packs into: the sheet less the trim. Length runs left to right, width top to bottom.</summary>
    public BoardDefinition Inside(BoardDefinition board)
    {
      if (board == null)
        throw new ArgumentNullException(nameof(board));
      double length = board.LengthMm - LeftMm - RightMm;
      double width = board.WidthMm - TopMm - BottomMm;
      if (length <= 0 || width <= 0)
        throw new InvalidOperationException(
          $"Board '{board.Name ?? board.Id}' ({board.LengthMm} × {board.WidthMm}) is too small for the sheet trim.");
      return new BoardDefinition
      {
        LengthMm = length,
        WidthMm = width,
        ThicknessMm = board.ThicknessMm,
        Quantity = board.Quantity,
        QuantityUnlimited = board.QuantityUnlimited,
        UseFirst = board.UseFirst,
        Id = board.Id,
        Name = board.Name,
        IsWaste = board.IsWaste
      };
    }
  }
}
