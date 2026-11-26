using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace S3Ware.Guillotine
{
  /// <summary>
  /// The pieces of a group laid out as one piece by the group's layout, a gap between each. The outer brackets hold
  /// slots end to end along the length, the brackets inside them side by side across the width, and so on in turn:
  /// (1,2,3) is three end to end, ((1,2)) two side by side, ((1,2),(3,4)) two by two. A slot is the size of its piece;
  /// a piece narrower than the slots beside it is trimmed off its waste.
  /// </summary>
  internal sealed class GuillotineBlock
  {
    private readonly double _gap;

    private GuillotineBlock(string group, double gap)
    {
      Group = group;
      _gap = gap;
    }

    public string Group { get; }
    public double LengthMm { get; private set; }
    public double WidthMm { get; private set; }

    /// <summary>The deepest cut inside the block: 0 for the cuts between the outer slots. -1 with no cut.</summary>
    public int Depth { get; private set; } = -1;

    /// <summary>The thinnest waste beside a piece narrower than its slot; 0 when every piece fills its slot.</summary>
    public double ThinnestWasteMm { get; private set; }

    /// <summary>Members in layout order and where each lies in the block: along the length and across the width.</summary>
    public List<(GuillotinePiece Piece, double AlongLength, double AcrossWidth)> Members { get; } = new List<(GuillotinePiece, double, double)>();

    /// <summary>
    /// Cuts inside the block at the far side of the gap before what they cut off, as the optimiser lays out grown
    /// pieces: L is along the length, W across the width, depth 0 between the outer slots.
    /// </summary>
    public List<(double L1, double W1, double L2, double W2, int Depth)> Cuts { get; } = new List<(double, double, double, double, int)>();

    /// <summary>Waste beside pieces narrower than their slot, grown by the gap: start, size and the depth of the cut that made it.</summary>
    public List<(double L, double W, double Length, double Width, int Depth)> Waste { get; } = new List<(double, double, double, double, int)>();

    /// <summary>
    /// The first cut level inside a block laid in a cell: a cut across the length runs on as the cuts beside a piece
    /// filling its row (Z), one along the length as the cuts between rows when the piece fills its strip (Y), and
    /// otherwise each one deeper than the cell's own cuts.
    /// </summary>
    public static int FirstLevel(bool lengthAlongStrip, bool fillsStrip, bool fillsRow) =>
      lengthAlongStrip ? (fillsRow ? 2 : 4) : (fillsStrip ? 1 : 3);

    /// <summary>Whether the cuts inside stay within <paramref name="maxLevel"/> laid this way in such a cell.</summary>
    public bool Fits(bool lengthAlongStrip, bool fillsStrip, bool fillsRow, int maxLevel) =>
      Depth < 0 || FirstLevel(lengthAlongStrip, fillsStrip, fillsRow) + Depth <= maxLevel;

    /// <summary>
    /// The block for <paramref name="members"/>, which share a group. Their slots number them in the layout; with no
    /// layout they lie end to end in slot order.
    /// </summary>
    public static GuillotineBlock Build(string group, List<GuillotinePiece> members, double gap)
    {
      string text = members.Select(piece => piece.GroupLayout?.Trim() ?? "").FirstOrDefault(layout => layout.Length > 0) ?? "";
      var ordered = members.Select((piece, index) => (Piece: piece, Index: index))
        .OrderBy(item => item.Piece.GroupSlot > 0 ? item.Piece.GroupSlot : int.MaxValue)
        .ThenBy(item => item.Index)
        .Select(item => item.Piece)
        .ToList();
      Node layout;
      var bySlot = new Dictionary<int, GuillotinePiece>();
      if (text.Length == 0)
      {
        layout = new Node { Children = ordered.Select((_, i) => new Node { Slot = i + 1 }).ToList() };
        for (int i = 0; i < ordered.Count; i++)
          bySlot[i + 1] = ordered[i];
      }
      else
      {
        layout = Parse(group, text);
        var slots = layout.Slots().ToList();
        if (slots.Count != slots.Distinct().Count())
          throw new InvalidOperationException($"Group '{group}' layout {text} has a slot twice.");
        foreach (var piece in members)
        {
          if (!slots.Contains(piece.GroupSlot) || !bySlot.TryAdd(piece.GroupSlot, piece))
            throw new InvalidOperationException($"Group '{group}' piece '{piece.Id}' is slot {piece.GroupSlot}, which is not a slot of layout {text} or is taken.");
        }
        if (bySlot.Count != slots.Count)
          throw new InvalidOperationException($"Group '{group}' layout {text} has {slots.Count} slots for {members.Count} pieces.");
      }

      var block = new GuillotineBlock(group, gap);
      var (length, width) = block.Measure(layout, 0, bySlot);
      block.LengthMm = length;
      block.WidthMm = width;
      block.Lay(layout, 0, 0, 0, bySlot);
      return block;
    }

    /// <summary>The size of a node: a group at an even depth runs along the length, at an odd depth across the width.</summary>
    private (double Length, double Width) Measure(Node node, int depth, Dictionary<int, GuillotinePiece> bySlot)
    {
      if (node.Children == null)
        return (bySlot[node.Slot].LengthMm, bySlot[node.Slot].WidthMm);
      var sizes = node.Children.Select(child => Measure(child, depth + 1, bySlot)).ToList();
      double gaps = _gap * (sizes.Count - 1);
      node.Size = depth % 2 == 0
        ? (sizes.Sum(size => size.Length) + gaps, sizes.Max(size => size.Width))
        : (sizes.Max(size => size.Length), sizes.Sum(size => size.Width) + gaps);
      node.ChildSizes = sizes;
      return node.Size;
    }

    private void Lay(Node node, int depth, double l, double w, Dictionary<int, GuillotinePiece> bySlot)
    {
      if (node.Children == null)
      {
        Members.Add((bySlot[node.Slot], l, w));
        return;
      }
      bool alongLength = depth % 2 == 0;
      double at = alongLength ? l : w;
      double side = alongLength ? node.Size.Width : node.Size.Length;
      for (int i = 0; i < node.Children.Count; i++)
      {
        var size = node.ChildSizes[i];
        double run = alongLength ? size.Length : size.Width;
        double across = alongLength ? size.Width : size.Length;
        if (alongLength)
          Lay(node.Children[i], depth + 1, at, w, bySlot);
        else
          Lay(node.Children[i], depth + 1, l, at, bySlot);
        double waste = side - across;
        if (waste > 0.001)
        {
          ThinnestWasteMm = ThinnestWasteMm > 0 ? Math.Min(ThinnestWasteMm, waste) : waste;
          double trim = (alongLength ? w : l) + across + _gap;
          AddCut(alongLength, false, trim, at, at + run + _gap, depth + 1);
          if (alongLength)
            Waste.Add((at, trim, run + _gap, waste, depth + 1));
          else
            Waste.Add((trim, at, waste, run + _gap, depth + 1));
        }
        at += run + _gap;
        if (i < node.Children.Count - 1)
        {
          double from = alongLength ? w : l;
          AddCut(alongLength, true, at, from, from + side + _gap, depth);
        }
      }
    }

    /// <summary>A cut across the run of a group (between its slots) or along it (a trim), the run along the length or across the width.</summary>
    private void AddCut(bool alongLength, bool acrossRun, double at, double from, double to, int depth)
    {
      bool constantLength = alongLength == acrossRun;
      Cuts.Add(constantLength ? (at, from, at, to, depth) : (from, at, to, at, depth));
      Depth = Math.Max(Depth, depth);
    }

    private static Node Parse(string group, string text)
    {
      int i = 0;
      Node node;
      try
      {
        node = ParseNode(text, ref i);
        Skip(text, ref i);
        if (i != text.Length)
          throw new FormatException($"Unexpected '{text[i]}' at {i + 1}.");
      }
      catch (FormatException ex)
      {
        throw new InvalidOperationException($"Group '{group}' layout {text} cannot be read: {ex.Message}", ex);
      }
      if (node.Children == null)
        throw new InvalidOperationException($"Group '{group}' layout {text} needs brackets round its slots.");
      return node;
    }

    private static Node ParseNode(string text, ref int i)
    {
      Skip(text, ref i);
      if (i >= text.Length)
        throw new FormatException("It ends before a slot.");
      if (text[i] == '(')
      {
        i++;
        var node = new Node { Children = new List<Node>() };
        while (true)
        {
          node.Children.Add(ParseNode(text, ref i));
          Skip(text, ref i);
          if (i >= text.Length)
            throw new FormatException("A bracket is not closed.");
          if (text[i] == ')')
          {
            i++;
            return node;
          }
          if (text[i] != ',')
            throw new FormatException($"Unexpected '{text[i]}' at {i + 1}.");
          i++;
        }
      }
      int start = i;
      while (i < text.Length && char.IsDigit(text[i]))
        i++;
      if (i == start || !int.TryParse(text.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out int slot) || slot <= 0)
        throw new FormatException($"A slot number is expected at {start + 1}.");
      return new Node { Slot = slot };
    }

    private static void Skip(string text, ref int i)
    {
      while (i < text.Length && char.IsWhiteSpace(text[i]))
        i++;
    }

    private sealed class Node
    {
      public int Slot { get; set; }
      public List<Node> Children { get; set; }
      public (double Length, double Width) Size { get; set; }
      public List<(double Length, double Width)> ChildSizes { get; set; }

      public IEnumerable<int> Slots() => Children == null ? new[] { Slot } : Children.SelectMany(child => child.Slots());
    }
  }
}
