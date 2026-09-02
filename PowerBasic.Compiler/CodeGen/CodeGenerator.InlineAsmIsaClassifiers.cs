namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private static bool IsPclmulInstruction(string mnemonic) => mnemonic == "PCLMULQDQ";
}
