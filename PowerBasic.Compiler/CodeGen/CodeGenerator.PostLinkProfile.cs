using PowerBasic.Compiler.Emit;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  /// <summary>
  /// Optional stable block/edge profile consumed by the final linker pass. Profile collection and
  /// persistence are separate concerns; setting this affects only post-link physical code layout.
  /// </summary>
  public PostLinkProfile? PostLinkProfile { get; set; }
}
