using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>Memory-only projection of an operation's semantics for alias/MemorySSA consumers.</summary>
public readonly record struct IrModRefSummary(bool ReadsMemory, bool WritesMemory) {
  public bool AccessesMemory => this.ReadsMemory || this.WritesMemory;
}

/// <summary>
/// Shared mod/ref query for one function-analysis lifetime. External/primitive operations project the
/// central <see cref="IrEffects"/> contract; direct internal calls reuse cached interprocedural function
/// summaries when a module analysis manager is available. Standalone function pipelines remain
/// conservative because they do not own a closed module context.
/// </summary>
public sealed class IrModRefAnalysis {
  private readonly FunctionSummaries? _functionSummaries;

  internal IrModRefAnalysis(FunctionSummaries? functionSummaries)
    => this._functionSummaries = functionSummaries;

  public IrModRefSummary ForInstruction(IrInstruction instruction) {
    ArgumentNullException.ThrowIfNull(instruction);

    if (instruction is IrCall { Callee: IrFunction { IsDeclaration: false } callee }
        && this._functionSummaries is not null) {
      var summary = this._functionSummaries.For(callee);
      return new(summary.ReadsMemory, summary.WritesMemory);
    }

    var effects = IrEffects.ForInstruction(instruction);
    return new(effects.MayReadMemory, effects.DefinesMemory);
  }
}
