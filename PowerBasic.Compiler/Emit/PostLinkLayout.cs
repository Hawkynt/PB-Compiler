namespace PowerBasic.Compiler.Emit;

/// <summary>Stable identity of one linker-visible basic block.</summary>
public readonly record struct PostLinkBlockId(string Function, int BlockId) {
  public override string ToString() => $"{this.Function}#{this.BlockId}";
}

/// <summary>Stable identity of one directed linker-visible CFG edge.</summary>
public readonly record struct PostLinkEdgeId(PostLinkBlockId Source, PostLinkBlockId Target);

/// <summary>One sampled instruction pointer in the linked code image.</summary>
public readonly record struct PostLinkIpSample(uint Offset, ulong Count = 1);

/// <summary>
/// One final linked fragment. Offsets are relative to <see cref="LinkedImage.Code"/>; block IDs remain
/// function-local so a profile can be applied to a later link without depending on the old addresses.
/// </summary>
public sealed record LinkedFragment(
  string Unit,
  string Function,
  int BlockId,
  uint Offset,
  uint Length,
  IReadOnlyList<int> Successors);

/// <summary>
/// Sample/profile data consumed by O0276. Counts use stable block identities rather than addresses;
/// address attribution is performed by <see cref="PostLinkSampleAttribution"/> against a linked map.
/// </summary>
public sealed class PostLinkProfile {

  private readonly Dictionary<PostLinkBlockId, ulong> _blocks = [];
  private readonly Dictionary<PostLinkEdgeId, ulong> _edges = [];

  public IReadOnlyDictionary<PostLinkBlockId, ulong> BlockCounts => this._blocks;
  public IReadOnlyDictionary<PostLinkEdgeId, ulong> EdgeCounts => this._edges;

  public void AddBlockCount(PostLinkBlockId block, ulong count)
    => this._blocks[block] = SaturatingAdd(this._blocks.GetValueOrDefault(block), count);

  public void AddEdgeCount(PostLinkBlockId source, PostLinkBlockId target, ulong count) {
    var edge = new PostLinkEdgeId(source, target);
    this._edges[edge] = SaturatingAdd(this._edges.GetValueOrDefault(edge), count);
  }

  private static ulong SaturatingAdd(ulong left, ulong right)
    => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
}

/// <summary>
/// O0405 address attribution for point samples. Samples that hit opaque runtime/direct-emitter code
/// are intentionally ignored: there is no block identity to attach them to. For a sampled block with
/// one successor, flow conservation determines that edge exactly; for a branch, its observed outgoing
/// flow is distributed in proportion to sampled successor heat (equally when none was sampled).
/// Point samples cannot uniquely recover every edge at joins, so this preserves the source-side flow
/// constraint and uses successor heat only to resolve the under-determined split.
/// </summary>
public static class PostLinkSampleAttribution {

  public static PostLinkProfile Attribute(LinkedImage image, IEnumerable<PostLinkIpSample> samples) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(samples);

    var fragments = image.Fragments.OrderBy(fragment => fragment.Offset).ToArray();
    for (var index = 1; index < fragments.Length; ++index)
      if (fragments[index - 1].Offset + fragments[index - 1].Length > fragments[index].Offset)
        throw new InvalidDataException("linked fragment map contains overlapping ranges");

    var profile = new PostLinkProfile();
    foreach (var sample in samples) {
      if (sample.Count == 0)
        continue;
      if (sample.Offset >= image.Code.Length)
        throw new ArgumentOutOfRangeException(nameof(samples), sample.Offset,
          "sample offset lies outside the linked code image");
      if (FindFragment(fragments, sample.Offset) is { } fragment)
        profile.AddBlockCount(new(fragment.Function, fragment.BlockId), sample.Count);
    }

    InferEdges(fragments, profile);
    return profile;
  }

  private static LinkedFragment? FindFragment(IReadOnlyList<LinkedFragment> fragments, uint offset) {
    var low = 0;
    var high = fragments.Count - 1;
    while (low <= high) {
      var middle = low + ((high - low) >> 1);
      var fragment = fragments[middle];
      if (offset < fragment.Offset) {
        high = middle - 1;
        continue;
      }
      if (offset >= fragment.Offset + fragment.Length) {
        low = middle + 1;
        continue;
      }
      return fragment;
    }
    return null;
  }

  private static void InferEdges(IReadOnlyList<LinkedFragment> fragments, PostLinkProfile profile) {
    foreach (var function in fragments.GroupBy(fragment => fragment.Function, StringComparer.Ordinal)) {
      var byId = function.ToDictionary(fragment => fragment.BlockId);
      foreach (var source in function) {
        var sourceId = new PostLinkBlockId(source.Function, source.BlockId);
        var count = profile.BlockCounts.GetValueOrDefault(sourceId);
        if (count == 0 || source.Successors.Count == 0)
          continue;

        var successors = source.Successors.Distinct().ToArray();
        foreach (var successor in successors)
          if (!byId.ContainsKey(successor))
            throw new InvalidDataException($"linked fragment {sourceId} names missing successor {successor}");

        if (successors.Length == 1) {
          profile.AddEdgeCount(sourceId, new(source.Function, successors[0]), count);
          continue;
        }

        UInt128 successorHeat = 0;
        foreach (var successor in successors)
          successorHeat += profile.BlockCounts.GetValueOrDefault(new PostLinkBlockId(source.Function, successor));

        var remaining = count;
        for (var index = 0; index < successors.Length; ++index) {
          var successor = successors[index];
          ulong share;
          if (index == successors.Length - 1)
            share = remaining;
          else if (successorHeat == 0)
            share = count / (ulong)successors.Length;
          else {
            var heat = profile.BlockCounts.GetValueOrDefault(new PostLinkBlockId(source.Function, successor));
            share = (ulong)((UInt128)count * heat / successorHeat);
          }
          if (share > remaining)
            share = remaining;
          remaining -= share;
          if (share != 0)
            profile.AddEdgeCount(sourceId, new(source.Function, successor), share);
        }
      }
    }
  }
}

/// <summary>Builds the profile-guided block orders consumed by the binary rewriter.</summary>
public static class PostLinkLayoutPlanner {

  public static IReadOnlyDictionary<string, IReadOnlyList<int>> Plan(PbuFile unit, PostLinkProfile profile) {
    ArgumentNullException.ThrowIfNull(unit);
    ArgumentNullException.ThrowIfNull(profile);

    var result = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
    foreach (var function in unit.Fragments.GroupBy(fragment => fragment.Function, StringComparer.Ordinal)) {
      var fragments = function.OrderBy(fragment => fragment.Offset).ToArray();
      if (fragments.Length < 2)
        continue;
      var byId = fragments.ToDictionary(fragment => fragment.BlockId);
      if (!byId.ContainsKey(0))
        throw new LinkException($"fragment function {function.Key} has no entry block #0");

      var originalIndex = fragments
        .Select((fragment, index) => (fragment.BlockId, Index: index))
        .ToDictionary(item => item.BlockId, item => item.Index);
      var weights = new Dictionary<(int From, int To), ulong>();
      foreach (var (edge, count) in profile.EdgeCounts) {
        if (!string.Equals(edge.Source.Function, function.Key, StringComparison.Ordinal)
            && !string.Equals(edge.Target.Function, function.Key, StringComparison.Ordinal))
          continue;
        if (!string.Equals(edge.Source.Function, function.Key, StringComparison.Ordinal)
            || !string.Equals(edge.Target.Function, function.Key, StringComparison.Ordinal)
            || !byId.TryGetValue(edge.Source.BlockId, out var source)
            || !byId.ContainsKey(edge.Target.BlockId)
            || !source.Successors.Contains(edge.Target.BlockId))
          throw new LinkException($"stale post-link profile edge {edge.Source} -> {edge.Target}");
        if (count != 0)
          weights[(edge.Source.BlockId, edge.Target.BlockId)] = count;
      }
      if (weights.Count == 0)
        continue;

      var original = fragments.Select(fragment => fragment.BlockId).ToArray();
      var originalWeight = FallthroughWeight(original, weights);
      var chains = original.ToDictionary(block => block, block => new Chain(block));
      var chainOf = original.ToDictionary(block => block, block => chains[block]);

      foreach (var (edge, _) in weights
        .OrderByDescending(item => item.Value)
        .ThenBy(item => item.Key.From)
        .ThenBy(item => item.Key.To)) {
        var fromChain = chainOf[edge.From];
        var toChain = chainOf[edge.To];
        if (ReferenceEquals(fromChain, toChain)
            || fromChain.Blocks[^1] != edge.From
            || toChain.Blocks[0] != edge.To
            || toChain.Blocks.Contains(0))
          continue;
        fromChain.Blocks.AddRange(toChain.Blocks);
        foreach (var block in toChain.Blocks)
          chainOf[block] = fromChain;
        toChain.Active = false;
      }

      ulong Heat(Chain chain) {
        var heat = 0UL;
        foreach (var block in chain.Blocks) {
          heat = SaturatingAdd(heat, profile.BlockCounts.GetValueOrDefault(new(function.Key, block)));
          foreach (var (edge, count) in weights)
            if (edge.From == block || edge.To == block)
              heat = SaturatingAdd(heat, count);
        }
        return heat;
      }

      var entryChain = chainOf[0];
      var candidate = new List<int>(original.Length);
      candidate.AddRange(entryChain.Blocks);
      foreach (var chain in chains.Values
        .Where(chain => chain.Active && !ReferenceEquals(chain, entryChain))
        .OrderByDescending(Heat)
        .ThenBy(chain => originalIndex[chain.Blocks[0]]))
        candidate.AddRange(chain.Blocks);

      var candidateWeight = FallthroughWeight(candidate, weights);
      if (candidateWeight > originalWeight && !candidate.SequenceEqual(original))
        result.Add(function.Key, candidate);
    }
    return result;
  }

  private static ulong FallthroughWeight(IReadOnlyList<int> order,
      IReadOnlyDictionary<(int From, int To), ulong> weights) {
    var result = 0UL;
    for (var index = 0; index + 1 < order.Count; ++index)
      result = SaturatingAdd(result, weights.GetValueOrDefault((order[index], order[index + 1])));
    return result;
  }

  private static ulong SaturatingAdd(ulong left, ulong right)
    => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

  private sealed class Chain(int block) {
    public List<int> Blocks { get; } = [block];
    public bool Active { get; set; } = true;
  }
}

/// <summary>
/// O0360/O0382 linker-side fragment rewriter. It moves only code whose machine-block metadata is
/// complete, remaps every linker-visible code offset, rebuilds layout-dependent terminal control flow,
/// and then relaxes generated branches monotonically to the shortest valid 16-bit x86 spelling.
/// </summary>
public static class PostLinkLayoutRewriter {

  public static PbuFile Rewrite(PbuFile source, IReadOnlyDictionary<string, IReadOnlyList<int>> layouts) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(layouts);
    if (layouts.Count == 0 || source.Fragments.Count == 0)
      return source;

    var functionGroups = source.Fragments
      .GroupBy(fragment => fragment.Function, StringComparer.Ordinal)
      .Select(group => FunctionRegion.Create(group.Key, group.ToArray()))
      .OrderBy(region => region.Start)
      .ToArray();
    for (var index = 1; index < functionGroups.Length; ++index)
      if (functionGroups[index - 1].End > functionGroups[index].Start)
        throw new LinkException($"overlapping post-link fragment functions {functionGroups[index - 1].Name} and {functionGroups[index].Name}");

    var requested = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
    foreach (var region in functionGroups) {
      if (!layouts.TryGetValue(region.Name, out var order))
        continue;
      ValidateOrder(region, order);
      if (!order.SequenceEqual(region.Fragments.Select(fragment => fragment.BlockId)))
        requested.Add(region.Name, order);
    }
    if (requested.Count == 0)
      return source;

    var output = new List<byte>(source.Code.Length);
    var map = new OffsetMap((uint)source.Code.Length);
    var rewrittenFragments = new Dictionary<(string Function, int BlockId), PbuFragment>();
    var generatedRelativeFixups = new List<PbuRelativeFixup>();
    var rewrittenRegions = new List<(uint Start, uint End)>();
    var oldCursor = 0u;

    foreach (var region in functionGroups) {
      if (oldCursor < region.Start)
        CopyOpaque(source.Code, oldCursor, region.Start - oldCursor, output, map);

      if (!requested.TryGetValue(region.Name, out var order)) {
        var newStart = checked((uint)output.Count);
        CopyOpaque(source.Code, region.Start, region.End - region.Start, output, map);
        foreach (var fragment in region.Fragments)
          rewrittenFragments.Add((fragment.Function, fragment.BlockId), fragment with {
            Offset = checked(newStart + fragment.Offset - region.Start),
          });
      } else {
        var built = BuildFunction(source, region, order, checked((uint)output.Count));
        output.AddRange(built.Bytes);
        foreach (var span in built.BodySpans)
          map.AddSpan(span.OldStart, span.Length, span.NewStart);
        foreach (var point in built.BlockStarts)
          map.AddPoint(point.OldOffset, point.NewOffset);
        foreach (var fragment in built.Fragments)
          rewrittenFragments.Add((fragment.Function, fragment.BlockId), fragment);
        generatedRelativeFixups.AddRange(built.RelativeFixups);
        rewrittenRegions.Add((region.Start, region.End));
      }
      oldCursor = region.End;
    }
    if (oldCursor < source.Code.Length)
      CopyOpaque(source.Code, oldCursor, checked((uint)source.Code.Length - oldCursor), output, map);
    map.SetNewEnd(checked((uint)output.Count));

    var result = CloneShell(source, output.ToArray());
    foreach (var export in source.Exports)
      result.Exports.Add(export with { CodeOffset = map.Map(export.CodeOffset, $"export {export.Name}") });

    foreach (var fixup in source.Fixups) {
      if (fixup.InData) {
        result.Fixups.Add(fixup);
        if (fixup.Kind == PbuFixupKind.NearCode) {
          var oldTarget = Read16(source.Data, checked((int)fixup.Offset));
          var newTarget = map.Map(oldTarget, $"data-site near-code fixup at {fixup.Offset:X4}");
          Patch16(result.Data, checked((int)fixup.Offset), CheckedWord(newTarget, "near-code target"));
        }
        continue;
      }

      var newOffset = map.Map(fixup.Offset, $"fixup site {fixup.Offset:X4}");
      result.Fixups.Add(fixup with { Offset = newOffset });
      if (fixup.Kind == PbuFixupKind.NearCode) {
        var oldTarget = Read16(source.Code, checked((int)fixup.Offset));
        var newTarget = map.Map(oldTarget, $"near-code target from {fixup.Offset:X4}");
        Patch16(result.Code, checked((int)newOffset), CheckedWord(newTarget, "near-code target"));
      }
    }

    foreach (var relative in source.RelativeFixups) {
      if (IsReplacedTerminal(relative.InstructionOffset, source.Fragments, rewrittenRegions))
        continue;
      var instruction = map.Map(relative.InstructionOffset,
        $"relative instruction {relative.InstructionOffset:X4}");
      var target = map.Map(relative.TargetOffset, $"relative target {relative.TargetOffset:X4}");
      var rewritten = relative with { InstructionOffset = instruction, TargetOffset = target };
      PatchRelative(result.Code, rewritten);
      result.RelativeFixups.Add(rewritten);
    }
    result.RelativeFixups.AddRange(generatedRelativeFixups);

    foreach (var fragment in source.Fragments) {
      if (rewrittenFragments.TryGetValue((fragment.Function, fragment.BlockId), out var rewritten))
        result.Fragments.Add(rewritten);
      else
        throw new LinkException($"fragment {fragment.Function}#{fragment.BlockId} was lost during post-link rewriting");
    }

    return result;
  }

  private static FunctionBuild BuildFunction(PbuFile source, FunctionRegion region,
      IReadOnlyList<int> order, uint newRegionStart) {
    var byId = region.Fragments.ToDictionary(fragment => fragment.BlockId);
    var builds = order.Select(blockId => new FragmentBuild(byId[blockId])).ToArray();
    var physicalIndex = builds
      .Select((build, index) => (build.Fragment.BlockId, Index: index))
      .ToDictionary(item => item.BlockId, item => item.Index);
    var allow386 = source.CpuFlags.HasFlag(PbuCpuFlags.Needs386);

    for (var index = 0; index < builds.Length; ++index) {
      var next = index + 1 < builds.Length ? builds[index + 1].Fragment.BlockId : -1;
      builds[index].Control = ControlState.Create(builds[index].Fragment, next, allow386);
    }

    for (;;) {
      ComputeStarts(builds, newRegionStart);
      var changed = false;
      for (var index = 0; index < builds.Length; ++index)
        changed |= TryRelax(builds, index, physicalIndex);
      if (!changed)
        break;
    }
    ComputeStarts(builds, newRegionStart);

    var bytes = new List<byte>();
    var spans = new List<BodySpan>();
    var points = new List<BlockStartMap>();
    var fragments = new List<PbuFragment>();
    var relativeFixups = new List<PbuRelativeFixup>();

    foreach (var build in builds) {
      var fragment = build.Fragment;
      var bodyLength = checked((int)fragment.BodyLength);
      if (fragment.Offset + fragment.BodyLength > source.Code.Length)
        throw new LinkException($"fragment {fragment.Function}#{fragment.BlockId} body exceeds unit code");
      var body = source.Code.AsSpan(checked((int)fragment.Offset), bodyLength);
      bytes.AddRange(body.ToArray());
      spans.Add(new(fragment.Offset, fragment.BodyLength, build.Start));
      points.Add(new(fragment.Offset, build.Start));

      var controlStart = checked(build.Start + fragment.BodyLength);
      EmitControl(build.Control, controlStart, builds, physicalIndex, bytes, relativeFixups);
      fragments.Add(fragment with {
        Offset = build.Start,
        Length = checked(fragment.BodyLength + (uint)build.Control.TotalLength),
      });
    }

    return new(bytes.ToArray(), spans, points, fragments, relativeFixups);
  }

  private static void ComputeStarts(IReadOnlyList<FragmentBuild> builds, uint start) {
    var cursor = start;
    foreach (var build in builds) {
      build.Start = cursor;
      cursor = checked(cursor + build.Fragment.BodyLength + (uint)build.Control.TotalLength);
    }
  }

  private static bool TryRelax(IReadOnlyList<FragmentBuild> builds, int sourceIndex,
      IReadOnlyDictionary<int, int> physicalIndex) {
    var build = builds[sourceIndex];
    var state = build.Control;
    var changed = false;
    var oldTotal = state.TotalLength;

    if (state.ConditionalLength > 2) {
      var candidateTotal = oldTotal - state.ConditionalLength + 2;
      var target = AdjustedTarget(builds, physicalIndex, sourceIndex, state.ConditionalTarget,
        candidateTotal - oldTotal);
      var branchStart = checked((long)build.Start + build.Fragment.BodyLength);
      if (FitsSByte(target - (branchStart + 2))) {
        state.ConditionalLength = 2;
        changed = true;
      }
    }

    if (state.JumpLength > 2) {
      var beforeJump = state.ConditionalLength;
      var currentTotal = state.TotalLength;
      var candidateTotal = currentTotal - state.JumpLength + 2;
      var target = AdjustedTarget(builds, physicalIndex, sourceIndex, state.JumpTarget,
        candidateTotal - oldTotal);
      var branchStart = checked((long)build.Start + build.Fragment.BodyLength + (uint)beforeJump);
      if (FitsSByte(target - (branchStart + 2))) {
        state.JumpLength = 2;
        changed = true;
      }
    }

    return changed;
  }

  private static long AdjustedTarget(IReadOnlyList<FragmentBuild> builds,
      IReadOnlyDictionary<int, int> physicalIndex, int sourceIndex, int targetBlockId, int sourceLengthDelta) {
    if (targetBlockId < 0 || !physicalIndex.TryGetValue(targetBlockId, out var targetIndex))
      throw new LinkException($"fragment control references missing target block {targetBlockId}");
    var target = (long)builds[targetIndex].Start;
    if (targetIndex > sourceIndex)
      target += sourceLengthDelta;
    return target;
  }

  private static void EmitControl(ControlState state, uint start, IReadOnlyList<FragmentBuild> builds,
      IReadOnlyDictionary<int, int> physicalIndex, List<byte> output,
      List<PbuRelativeFixup> relativeFixups) {
    var cursor = start;
    if (state.ConditionalLength > 0) {
      var target = builds[physicalIndex[state.ConditionalTarget]].Start;
      EmitConditional(output, cursor, state.ConditionalCondition, state.ConditionalLength, target);
      relativeFixups.Add(new(cursor, checked((byte)state.ConditionalLength),
        PbuRelativeFixupKind.Conditional, state.ConditionalCondition, target));
      cursor += (uint)state.ConditionalLength;
    }
    if (state.JumpLength > 0) {
      var target = builds[physicalIndex[state.JumpTarget]].Start;
      EmitJump(output, cursor, state.JumpLength, target);
      relativeFixups.Add(new(cursor, checked((byte)state.JumpLength), PbuRelativeFixupKind.Jump, 0, target));
    }
  }

  private static void EmitConditional(List<byte> output, uint start, byte condition, int length, uint target) {
    switch (length) {
      case 2: {
        var displacement = checked((long)target - (start + 2L));
        if (!FitsSByte(displacement))
          throw new LinkException($"short conditional branch at {start:X4} is out of range ({displacement})");
        output.Add((byte)(0x70 | condition));
        output.Add(unchecked((byte)(sbyte)displacement));
        break;
      }
      case 4:
        output.Add(0x0F);
        output.Add((byte)(0x80 | condition));
        AddWord(output, unchecked((ushort)(target - (start + 4))));
        break;
      case 5:
        output.Add((byte)(0x70 | (condition ^ 1)));
        output.Add(0x03);
        output.Add(0xE9);
        AddWord(output, unchecked((ushort)(target - (start + 5))));
        break;
      default:
        throw new LinkException($"unsupported conditional encoding length {length}");
    }
  }

  private static void EmitJump(List<byte> output, uint start, int length, uint target) {
    switch (length) {
      case 2: {
        var displacement = checked((long)target - (start + 2L));
        if (!FitsSByte(displacement))
          throw new LinkException($"short jump at {start:X4} is out of range ({displacement})");
        output.Add(0xEB);
        output.Add(unchecked((byte)(sbyte)displacement));
        break;
      }
      case 3:
        output.Add(0xE9);
        AddWord(output, unchecked((ushort)(target - (start + 3))));
        break;
      default:
        throw new LinkException($"unsupported jump encoding length {length}");
    }
  }

  private static void PatchRelative(byte[] code, PbuRelativeFixup fixup) {
    var start = checked((int)fixup.InstructionOffset);
    var target = fixup.TargetOffset;
    switch (fixup.Kind, fixup.EncodedLength) {
      case (PbuRelativeFixupKind.Call, 3):
        if (code[start] != 0xE8)
          throw new LinkException($"relative CALL record at {start:X4} does not point at E8");
        Patch16(code, start + 1, unchecked((ushort)(target - (fixup.InstructionOffset + 3))));
        break;
      case (PbuRelativeFixupKind.Jump, 2): {
        if (code[start] != 0xEB)
          throw new LinkException($"relative short-JMP record at {start:X4} does not point at EB");
        var displacement = checked((long)target - (fixup.InstructionOffset + 2L));
        if (!FitsSByte(displacement))
          throw new LinkException($"retained short jump at {start:X4} became out of range ({displacement})");
        code[start + 1] = unchecked((byte)(sbyte)displacement);
        break;
      }
      case (PbuRelativeFixupKind.Jump, 3):
        if (code[start] != 0xE9)
          throw new LinkException($"relative near-JMP record at {start:X4} does not point at E9");
        Patch16(code, start + 1, unchecked((ushort)(target - (fixup.InstructionOffset + 3))));
        break;
      case (PbuRelativeFixupKind.Conditional, 2): {
        if (code[start] is not (>= 0x70 and <= 0x7F))
          throw new LinkException($"relative short-Jcc record at {start:X4} does not point at Jcc");
        var displacement = checked((long)target - (fixup.InstructionOffset + 2L));
        if (!FitsSByte(displacement))
          throw new LinkException($"retained short conditional at {start:X4} became out of range ({displacement})");
        code[start + 1] = unchecked((byte)(sbyte)displacement);
        break;
      }
      case (PbuRelativeFixupKind.Conditional, 4):
        if (code[start] != 0x0F || code[start + 1] is not (>= 0x80 and <= 0x8F))
          throw new LinkException($"relative near-Jcc record at {start:X4} does not point at 0F 8x");
        Patch16(code, start + 2, unchecked((ushort)(target - (fixup.InstructionOffset + 4))));
        break;
      case (PbuRelativeFixupKind.Conditional, 5):
        if (code[start] is not (>= 0x70 and <= 0x7F) || code[start + 1] != 0x03 || code[start + 2] != 0xE9)
          throw new LinkException($"relative 8086 long-Jcc record at {start:X4} is malformed");
        Patch16(code, start + 3, unchecked((ushort)(target - (fixup.InstructionOffset + 5))));
        break;
      default:
        throw new LinkException($"unsupported retained relative fixup {fixup.Kind}/{fixup.EncodedLength} at {start:X4}");
    }
  }

  private static bool IsReplacedTerminal(uint instructionOffset, IReadOnlyList<PbuFragment> fragments,
      IReadOnlyList<(uint Start, uint End)> rewrittenRegions) {
    if (!rewrittenRegions.Any(region => instructionOffset >= region.Start && instructionOffset < region.End))
      return false;
    foreach (var fragment in fragments)
      if (instructionOffset >= fragment.Offset + fragment.BodyLength
          && instructionOffset < fragment.Offset + fragment.Length)
        return true;
    return false;
  }

  private static void CopyOpaque(byte[] source, uint oldStart, uint length, List<byte> output, OffsetMap map) {
    if (length == 0)
      return;
    var newStart = checked((uint)output.Count);
    output.AddRange(source.AsSpan(checked((int)oldStart), checked((int)length)).ToArray());
    map.AddSpan(oldStart, length, newStart);
  }

  private static void ValidateOrder(FunctionRegion region, IReadOnlyList<int> order) {
    ArgumentNullException.ThrowIfNull(order);
    var ids = region.Fragments.Select(fragment => fragment.BlockId).ToHashSet();
    if (order.Count != ids.Count || order.Distinct().Count() != order.Count || order.Any(block => !ids.Contains(block)))
      throw new LinkException($"post-link order for {region.Name} must contain every block exactly once");
    if (order.Count != 0 && order[0] != 0)
      throw new LinkException($"post-link order for {region.Name} must keep entry block #0 first");
  }

  private static PbuFile CloneShell(PbuFile source, byte[] code) {
    var result = new PbuFile {
      Name = source.Name,
      CpuFlags = source.CpuFlags,
      Foreign = source.Foreign,
      Code = code,
      Data = source.Data.ToArray(),
      BssSize = source.BssSize,
    };
    result.Imports.AddRange(source.Imports);
    result.Commons.AddRange(source.Commons);
    return result;
  }

  private static ushort CheckedWord(uint value, string what) {
    if (value > ushort.MaxValue)
      throw new LinkException($"{what} {value:X} does not fit the 16-bit code model");
    return (ushort)value;
  }

  private static ushort Read16(byte[] image, int site) {
    if ((uint)site + 1 >= image.Length)
      throw new LinkException($"16-bit fixup site {site:X4} lies outside its image");
    return (ushort)(image[site] | image[site + 1] << 8);
  }

  private static void Patch16(byte[] image, int site, ushort value) {
    if ((uint)site + 1 >= image.Length)
      throw new LinkException($"16-bit fixup site {site:X4} lies outside its image");
    image[site] = (byte)value;
    image[site + 1] = (byte)(value >> 8);
  }

  private static void AddWord(List<byte> output, ushort value) {
    output.Add((byte)value);
    output.Add((byte)(value >> 8));
  }

  private static bool FitsSByte(long value) => value is >= sbyte.MinValue and <= sbyte.MaxValue;

  private sealed class ControlState {
    public int ConditionalLength { get; set; }
    public int ConditionalTarget { get; private init; } = -1;
    public byte ConditionalCondition { get; private init; }
    public int JumpLength { get; set; }
    public int JumpTarget { get; private init; } = -1;
    public int TotalLength => this.ConditionalLength + this.JumpLength;

    public static ControlState Create(PbuFragment fragment, int nextBlockId, bool allow386) {
      var conditionalLong = allow386 ? 4 : 5;
      return fragment.Control switch {
        PbuFragmentControlKind.Preserve => new(),
        PbuFragmentControlKind.Unconditional when fragment.PrimaryTargetBlockId == nextBlockId => new(),
        PbuFragmentControlKind.Unconditional when fragment.PrimaryTargetBlockId >= 0 => new() {
          JumpLength = 3,
          JumpTarget = fragment.PrimaryTargetBlockId,
        },
        PbuFragmentControlKind.Conditional when fragment.PrimaryTargetBlockId < 0 || fragment.SecondaryTargetBlockId < 0
          => throw new LinkException($"conditional fragment {fragment.Function}#{fragment.BlockId} has incomplete targets"),
        PbuFragmentControlKind.Conditional when fragment.SecondaryTargetBlockId == nextBlockId => new() {
          ConditionalLength = conditionalLong,
          ConditionalTarget = fragment.PrimaryTargetBlockId,
          ConditionalCondition = fragment.Condition,
        },
        PbuFragmentControlKind.Conditional when fragment.PrimaryTargetBlockId == nextBlockId => new() {
          ConditionalLength = conditionalLong,
          ConditionalTarget = fragment.SecondaryTargetBlockId,
          ConditionalCondition = (byte)(fragment.Condition ^ 1),
        },
        PbuFragmentControlKind.Conditional => new() {
          ConditionalLength = conditionalLong,
          ConditionalTarget = fragment.PrimaryTargetBlockId,
          ConditionalCondition = fragment.Condition,
          JumpLength = 3,
          JumpTarget = fragment.SecondaryTargetBlockId,
        },
        _ => throw new LinkException($"invalid control metadata for fragment {fragment.Function}#{fragment.BlockId}"),
      };
    }
  }

  private sealed class FragmentBuild(PbuFragment fragment) {
    public PbuFragment Fragment { get; } = fragment;
    public ControlState Control { get; set; } = null!;
    public uint Start { get; set; }
  }

  private sealed record FunctionBuild(
    byte[] Bytes,
    IReadOnlyList<BodySpan> BodySpans,
    IReadOnlyList<BlockStartMap> BlockStarts,
    IReadOnlyList<PbuFragment> Fragments,
    IReadOnlyList<PbuRelativeFixup> RelativeFixups);

  private readonly record struct BodySpan(uint OldStart, uint Length, uint NewStart);
  private readonly record struct BlockStartMap(uint OldOffset, uint NewOffset);

  private sealed record FunctionRegion(string Name, uint Start, uint End, IReadOnlyList<PbuFragment> Fragments) {
    public static FunctionRegion Create(string name, PbuFragment[] fragments) {
      if (fragments.Length == 0)
        throw new LinkException($"fragment function {name} is empty");
      var ordered = fragments.OrderBy(fragment => fragment.Offset).ToArray();
      var ids = new HashSet<int>();
      for (var index = 0; index < ordered.Length; ++index) {
        var fragment = ordered[index];
        if (!ids.Add(fragment.BlockId))
          throw new LinkException($"fragment function {name} repeats block #{fragment.BlockId}");
        if (fragment.BodyLength > fragment.Length)
          throw new LinkException($"fragment {name}#{fragment.BlockId} body exceeds its byte range");
        if (fragment.Control == PbuFragmentControlKind.Preserve && fragment.BodyLength != fragment.Length)
          throw new LinkException($"preserved fragment {name}#{fragment.BlockId} must retain its complete byte range");
        if (index > 0 && ordered[index - 1].Offset + ordered[index - 1].Length != fragment.Offset)
          throw new LinkException($"fragment function {name} does not form one contiguous code region");
      }
      return new(name, ordered[0].Offset,
        checked(ordered[^1].Offset + ordered[^1].Length), ordered);
    }
  }

  private sealed class OffsetMap(uint oldEnd) {
    private readonly List<(uint OldStart, uint Length, uint NewStart)> _spans = [];
    private readonly Dictionary<uint, uint> _points = [];
    private uint _newEnd;

    public void AddSpan(uint oldStart, uint length, uint newStart) {
      if (length != 0)
        this._spans.Add((oldStart, length, newStart));
    }

    public void AddPoint(uint oldOffset, uint newOffset) => this._points[oldOffset] = newOffset;
    public void SetNewEnd(uint value) => this._newEnd = value;

    public uint Map(uint oldOffset, string what) {
      if (oldOffset == oldEnd)
        return this._newEnd;
      if (this._points.TryGetValue(oldOffset, out var point))
        return point;
      foreach (var span in this._spans)
        if (oldOffset >= span.OldStart && oldOffset < span.OldStart + span.Length)
          return checked(span.NewStart + oldOffset - span.OldStart);
      throw new LinkException($"{what} points into layout-dependent terminal bytes at {oldOffset:X4}");
    }
  }
}
