using PowerBasic.Compiler.Backend.Targets;

namespace PowerBasic.Compiler.Asm;

public sealed partial class Assembler {
  /// <summary>
  /// Appends bytes produced by a target-owned machine emitter and materializes its symbolic fixups in
  /// the image assembler. Encoding is owned by the target; this method is only the DOS image/linker
  /// boundary and does not select or synthesize instructions.
  /// </summary>
  public void AppendMachineCode(MachineCode code, Func<string, Label?> resolveSymbol) {
    ArgumentNullException.ThrowIfNull(resolveSymbol);
    var start = this.Position;
    this.Db(code.Bytes);
    foreach (var relocation in code.Relocations) {
      var label = resolveSymbol(relocation.Symbol)
        ?? throw new InvalidOperationException($"machine relocation references unknown symbol '{relocation.Symbol}'");
      var position = start + relocation.Offset;
      switch (relocation.Kind) {
        case MachineRelocationKind.Relative16:
          this._fixups.Add(new(position, FixupKind.Rel16, label, relocation.Addend));
          break;
        case MachineRelocationKind.Relative32:
          throw new NotSupportedException("32-bit relocations cannot be appended to a DOS image");
        case MachineRelocationKind.Absolute16:
          this._fixups.Add(new(position, FixupKind.Abs16, label, relocation.Addend));
          break;
        default:
          throw new NotSupportedException($"machine relocation kind '{relocation.Kind}' is not supported by the DOS image");
      }
    }
  }
}
