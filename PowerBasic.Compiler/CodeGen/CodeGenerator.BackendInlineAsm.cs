using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// Bridges machine-IR inline assembly into the target ISA policy. Instructions above the declared
/// CPU are virtualized here; instructions the target supports are emitted natively. Consequently an
/// <c>IrInlineAsm</c> block stays inside the mandatory production route in both cases.
/// </summary>
public sealed partial class CodeGenerator {

  /// <summary>
  /// Emits one inline-asm statement from machine IR through the target's ISA policy.
  ///
  /// <para>
  /// hosted target machine emitter takes this as a callback rather than calling the policy
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
  /// Whether the ISA POLICY will emit this line, whatever the plain assembler makes of it.
  ///
  /// <para>
  /// <c>TextAssembler</c>'s table predates <c>POPCNT</c>, the BMI sets, AES/PCLMUL and the 0F38/0F3A
  /// SIMD maps - <see cref="TryEmitPolicyInlineAsm"/> says so itself - so it answers "unknown
  /// mnemonic" for instructions this compiler emits natively on a capable target and EMULATES on
  /// everything else. The lowering was taking that answer as "this text cannot be assembled" and
  /// marking the block un-routable. The policy ownership check keeps those instructions in the
  /// production route whether the declared CPU emits or virtualizes them.
  /// </para>
  /// <para>
  /// Naming a CPU feature is the test, because that is the same question
  /// <see cref="TryEmitPolicyInlineAsm"/> asks before it decides between native and emulated. A
  /// mnemonic the policy has no feature for is one it has no opinion about, and an unknown one of
  /// those really is unknown.
  /// </para>
  /// </summary>
  internal static bool PolicyOwnsInlineAsmLine(string line) {
    var instruction = InlineInstruction.Parse(line);
    if (instruction.Mnemonic.Length == 0)
      return false;
    if (IsX87InlineMnemonic(instruction.Mnemonic))
      return true;

    var required = RequiredFeature(instruction)
      | RequiredBitManipulationFeature(instruction) | RequiredSupplementalFeature(instruction)
      | RequiredCryptoFeature(instruction) | RequiredBmiFeature(instruction);
    return required != RuntimeCpuFeatures.None;
  }

}
