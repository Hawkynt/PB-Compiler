namespace PowerBasic.Compiler.Ir;

/// <summary>
/// The shape <c>IrLowering</c> gives a run-time error: a block of its own holding
/// <c>call rt_error(code)</c> and <c>unreachable</c>, since no runtime's <c>rt_error</c> returns. The
/// passes that rewrite checks - overflow, bounds - recognize a check by this shape, and recognizing it
/// in one place is what keeps them agreeing about it.
/// </summary>
public static class IrRaise {

  /// <summary>
  /// Whether <paramref name="unreachable"/> directly follows a call that never returns, so a back end
  /// has nothing to emit for it: control left through the call.
  /// </summary>
  public static bool FollowsRaise(IrUnreachable unreachable)
    => unreachable.Parent is { } block && block.Instructions.Count >= 2
       && block.Instructions[^2] is IrCall { Callee: IrFunction { Name: "rt_error" or "rt_resume_same" or "rt_resume_next" or "rt_efar_go" or "sys_exit" } };

  /// <summary>The constant error code <paramref name="block"/> raises, or null when it is not a raise block.</summary>
  public static int? Code(IrBasicBlock block)
    => block.Instructions is [IrCall { Callee: IrFunction { Name: "rt_error" }, ArgCount: 1 } call, IrUnreachable]
       && call.GetOperand(1) is IrConstantInt code
      ? checked((int)code.Value)
      : null;
}
