namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  // No caller in the compiler, and not dead: InlineAsmIsaCoverageTests finds the ISA classifiers by
  // name, and this one is how it learns that PCLMULQDQ is advertised and must have a resolution.
  private static bool IsPclmulInstruction(string mnemonic) => mnemonic == "PCLMULQDQ";
}
