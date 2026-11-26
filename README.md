# S3Ware.Guillotine

A staged guillotine cutting optimiser for rectangular pieces on stock sheets, written in C# for .NET 8 with no
dependencies. It was built for glass cutting tables and beam saws / routers at [S3Ware](https://www.s3ware.com)
and released under the MIT licence.

A sheet is cut into strips (X cuts), a strip into rows (Y cuts), a row into pieces side by side (Z cuts) and,
optionally, a piece is trimmed or split once more (W, V and U cuts). Every leftover is either flush or at least
`MinBreakDistanceMm` wide, so each sheet can be broken out as laid out.

## Features

- Multiple stock sizes, with quantities, unlimited stock and "use this size first" (older stock)
- Sheet trim on each edge, saw kerf / router bit gap, minimum break distance
- Cut levels from 3 to 6 stages
- Pieces that may or may not rotate, priority pieces, and groups laid out together (for example drawer fronts)
- Shaped pieces (outline edges and arcs travel with the placed piece)
- Fill leftovers with spare pieces, by guillotine cuts
- Anytime search: stops at the fewest sheets possible, when it stalls, at a time limit, or when cancelled; reports progress

## Example

```csharp
using S3Ware.Guillotine;

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

foreach (var sheet in result.Sheets)
  foreach (var part in sheet.Pieces)
    Console.WriteLine($"{part.Id}: x={part.X} y={part.Y} {part.Width}x{part.Height}");
```

Coordinates are millimetres with a top-left origin; length runs left to right and width top to bottom.

## Build and test

```
dotnet test GuillotineOptimiser.sln
```

## Licence

[MIT](LICENSE). The library has no third-party dependencies. The test project uses xUnit.
