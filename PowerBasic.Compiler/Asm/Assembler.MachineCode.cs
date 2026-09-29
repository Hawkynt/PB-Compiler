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
    var labels = new Dictionary<string, Label>(StringComparer.Ordinal);
    if (code.Labels is { Count: > 0 })
      foreach (var name in code.Labels.Keys)
        // MachineCode.Labels are definition-local symbols. Asking the external resolver about them
        // lets an image/linker resolver mint an unbound same-named label (s_0, s_1, ...), which then
        // escapes the function instead of being bound at the recorded machine-code offset.
        labels[name] = this.DefineLabel();
    var cursor = 0;
    foreach (var (name, offset) in (code.Labels ?? new Dictionary<string, int>()).OrderBy(pair => pair.Value)) {
      if (offset < cursor || offset > code.Bytes.Length)
        throw new InvalidOperationException($"invalid machine label offset {offset} for '{name}'");
      if (offset > cursor)
        this.Db(code.Bytes[cursor..offset]);
      this.MarkLabel(labels[name]);
      cursor = offset;
    }
    if (cursor < code.Bytes.Length)
      this.Db(code.Bytes[cursor..]);
    foreach (var relocation in code.Relocations) {
      var label = labels.GetValueOrDefault(relocation.Symbol) ?? resolveSymbol(relocation.Symbol)
        ?? throw new InvalidOperationException($"machine relocation references unknown symbol '{relocation.Symbol}'");
      var position = start + relocation.Offset;
      switch (relocation.Kind) {
        case MachineRelocationKind.Relative16:
          this._fixups.Add(new(position, FixupKind.Rel16, label, ToFixupAddend(relocation, Rel16FieldBytes)));
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

  private const int Rel16FieldBytes = 2;

  /// <summary>
  /// A relative <see cref="MachineRelocation"/>'s addend in the assembler's own fixup convention.
  ///
  /// <para>
  /// The two disagree about who accounts for the end of the displacement field. A MachineRelocation
  /// uses the ELF form - the stored value is <c>S + A - P</c>, with the field's width already folded
  /// into <c>A</c>, so a JMP rel16 carries <c>A = -2</c>. The assembler's Rel16 fixup subtracts
  /// <c>P + 2</c> itself. Handing the addend over unchanged applied the width twice and every
  /// displacement came out two short: the jump to the very next instruction encoded as FFFE, not 0.
  /// </para>
  /// <para>
  /// The conversion lives here, once, because this is the one place both conventions meet.
  /// </para>
  /// </summary>
  private static int ToFixupAddend(MachineRelocation relocation, int fieldBytes) => relocation.Addend + fieldBytes;
}
