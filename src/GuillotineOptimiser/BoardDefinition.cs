namespace S3Ware.Guillotine
{
  /// <summary>
  /// Sheet / board stock available for nesting.
  /// Length × Width are the sheet size; thickness is metadata for material selection.
  /// </summary>
  public class BoardDefinition
  {
    /// <summary>Board length in mm (stock DimX).</summary>
    public double LengthMm { get; set; }

    /// <summary>Board width in mm (stock DimY).</summary>
    public double WidthMm { get; set; }

    /// <summary>Board thickness in mm (e.g. 16).</summary>
    public double ThicknessMm { get; set; }

    /// <summary>
    /// Number of boards available. Ignored when <see cref="QuantityUnlimited"/> is true.
    /// </summary>
    public int Quantity { get; set; } = 1;

    /// <summary>
    /// When true, nesting uses a large quantity (999) — for unlimited stock.
    /// </summary>
    public bool QuantityUnlimited { get; set; }

    /// <summary>
    /// Sheets of this size are laid before any other size, until its <see cref="Quantity"/> is used (older stock to use
    /// up). Ignored when <see cref="QuantityUnlimited"/>.
    /// </summary>
    public bool UseFirst { get; set; }

    /// <summary>Whether sheets of this size are laid before the others.</summary>
    public bool IsFirstChoice => UseFirst && !QuantityUnlimited;

    /// <summary>Optional stock id / label.</summary>
    public string Id { get; set; }

    /// <summary>Display / material name (e.g. White 16mm).</summary>
    public string Name { get; set; }

    /// <summary>True if this stock is a remnant / waste piece.</summary>
    public bool IsWaste { get; set; }

    /// <summary>This sheet's trim on each edge. Null uses the optimiser's trim.</summary>
    public SheetTrim Trim { get; set; }

    /// <summary>Most of these sheets the beam saw cuts together. Null is as many as the saw's book height takes.</summary>
    public int? Book { get; set; }

    /// <summary>Whether this sheet may be turned 90°. Null uses the optimiser's setting.</summary>
    public bool? CanTurn { get; set; }

    /// <summary>Quantity used when adding stock to the nesting engine.</summary>
    public int EffectiveQuantity => QuantityUnlimited ? 999 : Quantity;

    public static BoardDefinition DefaultCncSheet() => new BoardDefinition
    {
      LengthMm = 3620,
      WidthMm = 1810,
      ThicknessMm = 16,
      QuantityUnlimited = true,
      Name = "CNC sheet"
    };
  }
}
