using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  private readonly List<PostLinkFunctionRange> _postLinkFunctionRanges = [];

  private sealed record PostLinkFunctionRange(
    ProcedureSymbol Procedure,
    MFunction Machine,
    Label Start,
    Label End);

  /// <summary>Starts a fresh O0360 metadata census for one emitted artifact.</summary>
  private void ResetPostLinkFunctions() => this._postLinkFunctionRanges.Clear();

  /// <summary>
  /// Remembers the exact assembled byte range owned by one routed machine function. Direct-emitter
  /// procedures deliberately do not participate: without machine-block boundaries their bytes are
  /// opaque and O0276 must not pretend otherwise.
  /// </summary>
  private void TrackPostLinkFunction(ProcedureSymbol procedure, MFunction machine, Label start, Label end)
    => this._postLinkFunctionRanges.Add(new(procedure, machine, start, end));

  /// <summary>
  /// Converts routed machine blocks plus the assembler's surviving internal branch records into the
  /// PBU2 fragment map consumed by the final linker rewriter.
  /// </summary>
  private void PopulatePostLinkMetadata(PbuFile unit, RelocatableImage image, int codeLength) {
    ArgumentNullException.ThrowIfNull(unit);
    ArgumentNullException.ThrowIfNull(image);
    if (this._postLinkFunctionRanges.Count == 0)
      return;

    foreach (var relative in image.RelativeFixups) {
      if ((uint)relative.InstructionOffset >= (uint)codeLength || relative.TargetOffset < 0
          || relative.TargetOffset >= codeLength)
        continue;
      unit.RelativeFixups.Add(new(
        (uint)relative.InstructionOffset,
        checked((byte)relative.EncodedLength),
        relative.Kind switch {
          AsmRelativeFixupKind.Call => PbuRelativeFixupKind.Call,
          AsmRelativeFixupKind.Jump => PbuRelativeFixupKind.Jump,
          _ => PbuRelativeFixupKind.Conditional,
        },
        relative.Condition,
        (uint)relative.TargetOffset));
    }

    foreach (var range in this._postLinkFunctionRanges)
      this.TryAppendFragments(unit, image, range, codeLength);
  }

  private void TryAppendFragments(PbuFile unit, RelocatableImage image,
      PostLinkFunctionRange range, int codeLength) {
    var functionStart = range.Start.Position;
    var functionEnd = range.End.Position;
    if (functionStart < 0 || functionEnd <= functionStart || functionEnd > codeLength)
      return;
    if (range.Machine.Blocks.Count == 0)
      return;

    var starts = new int[range.Machine.Blocks.Count];
    for (var index = 0; index < range.Machine.Blocks.Count; ++index) {
      var label = range.Machine.Blocks[index].Label;
      var matches = image.AllBoundLabels
        .Where(bound => bound.Name == label && bound.Offset >= functionStart && bound.Offset < functionEnd)
        .Select(bound => bound.Offset)
        .Distinct()
        .Take(2)
        .ToArray();
      if (matches.Length != 1)
        return;
      starts[index] = matches[0];
    }

    // The prologue belongs to the entry fragment, so its physical range starts at the procedure's
    // exported entry rather than at the machine block label a few instructions later.
    var physicalStarts = starts.ToArray();
    physicalStarts[0] = functionStart;
    if (!physicalStarts.SequenceEqual(physicalStarts.Order()))
      return; // selection/emission stopped being a simple block-order walk: refuse rather than guess

    var blockByLabel = range.Machine.Blocks
      .Select((block, index) => (block.Label, Index: index))
      .ToDictionary(item => item.Label, item => item.Index, StringComparer.Ordinal);
    var blockByTargetOffset = starts
      .Select((offset, index) => (offset, index))
      .ToDictionary(item => item.offset, item => item.index);

    var staged = new List<PbuFragment>(range.Machine.Blocks.Count);
    for (var index = 0; index < range.Machine.Blocks.Count; ++index) {
      var block = range.Machine.Blocks[index];
      var start = physicalStarts[index];
      var end = index + 1 < physicalStarts.Length ? physicalStarts[index + 1] : functionEnd;
      if (end <= start)
        return;

      var successors = new List<int>();
      foreach (var successor in block.Successors) {
        if (!blockByLabel.TryGetValue(successor, out var successorIndex))
          return;
        if (!successors.Contains(successorIndex))
          successors.Add(successorIndex);
      }

      var control = PbuFragmentControlKind.Preserve;
      var primary = -1;
      var secondary = -1;
      var condition = (byte)0;
      var bodyLength = end - start;

      if (successors.Count == 1) {
        control = PbuFragmentControlKind.Unconditional;
        primary = successors[0];
        var targetOffset = starts[primary];
        var terminal = image.RelativeFixups
          .Where(fixup => fixup.Kind == AsmRelativeFixupKind.Jump
            && fixup.TargetOffset == targetOffset
            && fixup.InstructionOffset >= start
            && fixup.InstructionOffset + fixup.EncodedLength == end)
          .OrderByDescending(fixup => fixup.InstructionOffset)
          .FirstOrDefault();
        if (terminal.EncodedLength > 0)
          bodyLength = terminal.InstructionOffset - start;
      } else if (successors.Count == 2) {
        var successorSet = successors.Select(successor => starts[successor]).ToHashSet();
        var tail = image.RelativeFixups
          .Where(fixup => fixup.InstructionOffset >= start && fixup.InstructionOffset < end
            && successorSet.Contains(fixup.TargetOffset)
            && fixup.Kind is AsmRelativeFixupKind.Conditional or AsmRelativeFixupKind.Jump)
          .OrderBy(fixup => fixup.InstructionOffset)
          .ToArray();
        if (tail.Length == 0 || tail[^1].InstructionOffset + tail[^1].EncodedLength != end)
          return;

        var firstTail = tail.Length - 1;
        while (firstTail > 0
            && tail[firstTail - 1].InstructionOffset + tail[firstTail - 1].EncodedLength
              == tail[firstTail].InstructionOffset)
          --firstTail;
        var controlTail = tail[firstTail..];
        var conditional = controlTail.LastOrDefault(fixup => fixup.Kind == AsmRelativeFixupKind.Conditional);
        if (conditional.EncodedLength == 0 || !blockByTargetOffset.TryGetValue(conditional.TargetOffset, out primary))
          return;
        secondary = successors.Single(successor => successor != primary);
        condition = conditional.Condition;
        control = PbuFragmentControlKind.Conditional;
        bodyLength = controlTail[0].InstructionOffset - start;
      } else if (successors.Count > 2) {
        // Indexed/switch dispatch keeps its terminal bytes. All absolute table entries are ordinary
        // PBU fixups and get remapped by the linker; no fall-through has to be reconstructed.
        control = PbuFragmentControlKind.Preserve;
      }

      if (bodyLength < 0 || bodyLength > end - start)
        return;
      staged.Add(new(
        range.Procedure.Name,
        index,
        (uint)start,
        (uint)(end - start),
        (uint)bodyLength,
        control,
        primary,
        secondary,
        condition,
        successors));
    }

    unit.Fragments.AddRange(staged);
  }
}
