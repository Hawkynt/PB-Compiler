using System.Diagnostics;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Eliminates single-use substring allocations whose only consumer asks for their length.
///
/// <para>
/// O0286 is the general allocation-elimination idea, but the IR does not need general escape
/// analysis for this useful string case: a substring with exactly one user, <c>rt_str_len</c>, is
/// already proven not to escape. LEFT$/RIGHT$/MID$ determine their result length entirely from the
/// source length and their integer arguments, so materializing the bytes is wasted work.
/// </para>
///
/// <para>
/// The replacement <c>rt_str_len(source)</c> is deliberately inserted where the substring call used
/// to execute, rather than where its outer LEN executes. The substring routines consume their owned
/// source handle; keeping that consumption at the original program point preserves call ordering and
/// temporary lifetime even when the SSA value is carried into another block.
/// </para>
/// </summary>
public static class StringAllocationElimination {

  private const string _LEN = "rt_str_len";
  private const string _LEFT = "rt_str_left";
  private const string _RIGHT = "rt_str_right";
  private const string _MID = "rt_str_mid";
  private const string _MID2 = "rt_str_mid2";

  /// <summary>Replaces length-only substring temporaries with scalar length arithmetic; the number removed.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);

    var rewritten = 0;
    int sweep;
    do {
      sweep = 0;
      foreach (var function in module.Functions.ToList()) {
        if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
          continue;

        foreach (var length in function.AllInstructions.OfType<IrCall>().ToList()) {
          if (length.Parent is null || length.Callee is not IrFunction { Name: _LEN } || length.ArgCount != 1)
            continue;
          if (length.GetOperand(1) is not IrCall { Parent: not null, Callee: IrFunction sliceCallee } slice
              || slice.Users.Count != 1)
            continue;

          var expectedArgs = sliceCallee.Name switch {
            _LEFT or _RIGHT => 2,
            _MID => 3,
            _MID2 => 2,
            _ => 0,
          };
          if (expectedArgs == 0 || slice.ArgCount != expectedArgs)
            continue;

          var block = slice.Parent!;
          var source = slice.GetOperand(1);
          var sourceLength = block.InsertBefore(new IrCall(length.Type, length.Callee, [source]), slice);
          var replacement = sliceCallee.Name switch {
            _LEFT or _RIGHT => ClampCount(block, slice, slice.GetOperand(2), sourceLength),
            _MID => MidLength(block, slice, sourceLength, slice.GetOperand(2), slice.GetOperand(3)),
            _MID2 => MidToEndLength(block, slice, sourceLength, slice.GetOperand(2)),
            _ => throw new UnreachableException(),
          };

          length.ReplaceAllUsesWith(replacement);
          length.EraseFromParent();
          slice.EraseFromParent();
          ++sweep;
        }
      }
      rewritten += sweep;
    } while (sweep != 0);

    return rewritten;
  }

  /// <summary>LEFT$/RIGHT$: min(max(count, 0), sourceLength).</summary>
  private static IrValue ClampCount(IrBasicBlock block, IrInstruction at, IrValue count, IrValue sourceLength) {
    var zero = new IrConstantInt(count.Type, 0);
    var negative = block.InsertBefore(new IrCmp(IrCmpPred.Slt, count, zero), at);
    var nonNegative = block.InsertBefore(new IrSelect(negative, zero, count), at);
    var pastEnd = block.InsertBefore(new IrCmp(IrCmpPred.Sgt, nonNegative, sourceLength), at);
    return block.InsertBefore(new IrSelect(pastEnd, sourceLength, nonNegative), at);
  }

  /// <summary>MID$(s,start): max(0, sourceLength - max(start,1) + 1).</summary>
  private static IrValue MidToEndLength(IrBasicBlock block, IrInstruction at, IrValue sourceLength, IrValue start) {
    var (clampedStart, pastEnd) = ClampStart(block, at, sourceLength, start);
    var one = new IrConstantInt(start.Type, 1);
    var remainingMinusOne = block.InsertBefore(new IrBinary(IrBinaryOp.Sub, sourceLength, clampedStart), at);
    var remaining = block.InsertBefore(new IrBinary(IrBinaryOp.Add, remainingMinusOne, one), at);
    return block.InsertBefore(new IrSelect(pastEnd, new IrConstantInt(sourceLength.Type, 0), remaining), at);
  }

  /// <summary>MID$(s,start,count), including its start&lt;1, start&gt;LEN and count&lt;=0 rules.</summary>
  private static IrValue MidLength(IrBasicBlock block, IrInstruction at, IrValue sourceLength, IrValue start, IrValue count) {
    var (clampedStart, pastEnd) = ClampStart(block, at, sourceLength, start);
    var one = new IrConstantInt(start.Type, 1);
    var zero = new IrConstantInt(count.Type, 0);
    var remainingMinusOne = block.InsertBefore(new IrBinary(IrBinaryOp.Sub, sourceLength, clampedStart), at);
    var remaining = block.InsertBefore(new IrBinary(IrBinaryOp.Add, remainingMinusOne, one), at);
    var nonPositive = block.InsertBefore(new IrCmp(IrCmpPred.Sle, count, zero), at);
    var tooLong = block.InsertBefore(new IrCmp(IrCmpPred.Sgt, count, remaining), at);
    var bounded = block.InsertBefore(new IrSelect(tooLong, remaining, count), at);
    var positive = block.InsertBefore(new IrSelect(nonPositive, zero, bounded), at);
    return block.InsertBefore(new IrSelect(pastEnd, zero, positive), at);
  }

  private static (IrValue Start, IrValue PastEnd) ClampStart(
      IrBasicBlock block, IrInstruction at, IrValue sourceLength, IrValue start) {
    var one = new IrConstantInt(start.Type, 1);
    var beforeFirst = block.InsertBefore(new IrCmp(IrCmpPred.Slt, start, one), at);
    var clamped = block.InsertBefore(new IrSelect(beforeFirst, one, start), at);
    var pastEnd = block.InsertBefore(new IrCmp(IrCmpPred.Sgt, clamped, sourceLength), at);
    return (clamped, pastEnd);
  }
}
