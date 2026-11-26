using System.Collections.Generic;

namespace S3Ware.Guillotine
{
  /// <summary>The numbers of an optimise for its summary and printed run sheet. Areas are in m².</summary>
  public sealed class GuillotineSummary
  {
    public int Sheets { get; init; }
    public int Pieces { get; init; }
    public int FillPieces { get; init; }
    public int UnplacedPieces { get; init; }

    /// <summary>The whole area of the sheets used.</summary>
    public double StockM2 { get; init; }
    public double PieceM2 { get; init; }
    public double FillM2 { get; init; }
    public double TrimM2 { get; init; }

    /// <summary>The last sheet's clear end; null when nothing was laid out.</summary>
    public GuillotineRemnant? Remnant { get; init; }

    public List<GuillotineSizeSummary> BySize { get; init; } = new List<GuillotineSizeSummary>();

    /// <summary>Stock not cut into pieces or fill: trim, offcuts and the remnant.</summary>
    public double WasteM2 => StockM2 - PieceM2 - FillM2;

    public double WastePercent => StockM2 > 0 ? 100 * WasteM2 / StockM2 : 0;

    /// <summary>Waste when the remnant goes back to stock: the remnant leaves both the waste and the stock.</summary>
    public double WasteLessRemnantPercent
    {
      get
      {
        double remnant = Remnant?.AreaM2 ?? 0;
        double stock = StockM2 - remnant;
        return stock > 0 ? 100 * (WasteM2 - remnant) / stock : 0;
      }
    }

    public double TrimPercent => StockM2 > 0 ? 100 * TrimM2 / StockM2 : 0;
  }

  /// <summary>The clear end of a sheet, by sheet number from 1.</summary>
  public sealed record GuillotineRemnant(int SheetNumber, double LengthMm, double WidthMm)
  {
    public double AreaM2 => LengthMm * WidthMm / 1e6;
  }

  /// <summary>The sheets used of one stock size.</summary>
  public sealed record GuillotineSizeSummary(double LengthMm, double WidthMm, int Sheets, double StockM2, double PieceM2, double FillM2)
  {
    public double UsedPercent => StockM2 > 0 ? 100 * (PieceM2 + FillM2) / StockM2 : 0;
  }
}
