using PowerBasic.Compiler.Runtime;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// The one thing inline assembly makes the routing refuse: an instruction the DECLARED CPU cannot
/// execute.
///
/// <para>
/// The direct emitter does not pass such an instruction through. It EMULATES it - that is what the
/// <c>InlineAsmVirtualization</c> family is - lowering the packed-integer and 32-bit surfaces onto
/// plain 8086 instructions held in compiler-owned state. So <c>$CPU 8086</c> plus
/// <c>! PADDW MM0, MM1</c> produces a program that runs on an 8086.
/// </para>
/// <para>
/// The routed path has no such lowering: <c>IrInlineAsm</c> carries the text, and the machine
/// emitter assembles it verbatim. That is correct wherever the target really has the ISA and a
/// silent miscompile where it does not - the image gets a real <c>0F FD</c> and the machine the
/// source named faults on it. Measured across tiers, the two paths agree only when the instruction
/// is natively supported:
/// </para>
/// <list type="table">
///   <item><c>$CPU MMX</c> + PADDW</item><item>both emit it natively - agreed</item>
///   <item><c>$CPU 8086</c> / <c>80386</c> / <c>SSE2</c> + PADDW</item>
///   <item>direct emulates, routed emits the raw instruction</item>
/// </list>
/// <para>
/// So the routing declines a body carrying one, and the direct emitter picks it up. That is a
/// genuine decline class rather than a placeholder, and it is the reason the fallback is still
/// load-bearing: this is the one thing <c>CodeGen/</c> can still do that <c>Ir/</c> cannot. Closing
/// it means teaching the IR path to emulate, not deleting the emulator.
/// </para>
/// </summary>
public sealed partial class CodeGenerator {

  /// <summary>
  /// Emits one inline-asm statement for the ROUTED path through the target's ISA policy - the same
  /// entry the direct emitter uses, so the two reach the same decision about the same instruction.
  ///
  /// <para>
  /// <see cref="Backend.MachineEmitter"/> takes this as a callback rather than calling the policy
  /// itself, for the reason it takes callee labels and data cells that way: what a target can execute
  /// is knowledge the CODE GENERATOR holds, and the machine emitter should not have to grow a second
  /// copy of it. Returning false leaves the emitter to assemble the text verbatim, which is the right
  /// answer for everything the policy has no opinion about.
  /// </para>
  /// </summary>
  private bool EmitRoutedInlineAsm(string text, Asm.IAsmSymbolResolver resolver) {
    var target = this.RuntimeTargetForRuntime();
    if (!this.TryEmitPolicyInlineAsm(text, resolver, target, out var error))
      return false;
    if (error != null)
      this.Errors.Add(new(new("", 0, 0), $"inline asm '{text.Trim()}': {error}"));
    return true;
  }

  /// <summary>
  /// The inline-asm mnemonic in <paramref name="statements"/> that the declared target cannot
  /// execute, or null when every one of them can.
  ///
  /// <para>
  /// The feature set asked for here is exactly the one <c>EmitPolicyInlineAsm</c> computes before
  /// deciding to emulate, so the two answer the same question and cannot drift into a body that
  /// routes and then needs emulating.
  /// </para>
  /// </summary>
  private string? InlineAsmAboveTarget(IEnumerable<Statement> statements) {
    var target = this.RuntimeTargetForRuntime();
    foreach (var statement in statements) {
      if (statement is InlineAsmStmt asm) {
        foreach (var line in asm.Text.Split('\n')) {
          var instruction = InlineInstruction.Parse(line);
          if (instruction.Mnemonic.Length == 0)
            continue;
          var x87 = IsX87InlineMnemonic(instruction.Mnemonic);
          var required = x87 ? RuntimeCpuFeatures.X87 : RequiredFeature(instruction);
          if (!x87)
            required |= RequiredBitManipulationFeature(instruction) | RequiredSupplementalFeature(instruction)
              | RequiredCryptoFeature(instruction) | RequiredBmiFeature(instruction);
          if (required != RuntimeCpuFeatures.None && !target.Has(required))
            return $"{instruction.Mnemonic} needs {target.DescribeMissing(required)}";
        }
        continue;
      }
      if (statement is SubDecl or FunctionDecl or DefFnDecl)
        continue;                                   // a nested procedure is asked about on its own
      foreach (var block in ChildStatementBlocks(statement))
        if (this.InlineAsmAboveTarget(block) is { } nested)
          return nested;
    }
    return null;
  }
}
