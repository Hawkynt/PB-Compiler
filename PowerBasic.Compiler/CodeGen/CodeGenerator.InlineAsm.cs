using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  private TextAssembler? _textAssembler;

  /// <summary>
  /// Emits one <c>!</c> inline-assembly statement. PB identifier semantics:
  /// locals/params resolve to their [BP+disp] cell (a BYREF parameter name
  /// denotes the pointer slot itself), module variables to their data label,
  /// BASIC labels of the enclosing scope to jump targets, %equates to
  /// constants.
  /// </summary>
  private void EmitInlineAsm(InlineAsmStmt ia) {
    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParse(ia.Text, new InlineAsmResolver(this), out var error))
      this.Errors.Add(new(ia.Position, $"inline asm '{ia.Text.Trim()}': {error}"));
  }

  /// <summary>Suffix spellings tried when an inline-asm name has no explicit suffix.</summary>
  private static readonly TypeSuffix[] _ASM_SUFFIXES = [
    TypeSuffix.None, TypeSuffix.Integer, TypeSuffix.Long, TypeSuffix.Single, TypeSuffix.Double, TypeSuffix.Ext, TypeSuffix.String,
  ];

  private sealed class InlineAsmResolver(CodeGenerator owner) : IAsmSymbolResolver {

    public bool TryResolve(string name, out AsmSymbol symbol) {
      foreach (var suffix in _ASM_SUFFIXES)
        if (owner.LookupVariable(name, suffix) is { } variable) {
          symbol = AsmSymbol.OfMemory(owner.AsmCellOf(variable));
          return true;
        }

      var scope = owner._currentProc?.Name ?? "";
      if (owner.Model.Labels.TryGetValue(scope, out var labels) && labels.Contains(name)) {
        symbol = AsmSymbol.OfLabel(owner.UserLabel(name));
        return true;
      }

      if (owner.Model.Equates.TryGetValue(name, out var equate) && equate.Text is null) {
        symbol = AsmSymbol.Constant((int)equate.AsInteger);
        return true;
      }

      symbol = default;
      return false;
    }
  }

  /// <summary>
  /// The memory cell an inline-asm reference to <paramref name="symbol"/>
  /// denotes. 1- and 2-byte scalars carry their natural operand size so
  /// immediate stores work without a size keyword; wider types stay unsized
  /// (the register partner or an explicit WORD/DWORD PTR sizes the access).
  /// </summary>
  private Mem AsmCellOf(VariableSymbol symbol) {
    var cell = this.TryDirectCell(symbol) ?? Mem.At(Reg.BP, symbol.Offset);   // BYREF parameter: the pointer slot
    return symbol.Type switch {
      ScalarType { ByteSize: 1 } => cell.WithSize(OperandSize.Byte),
      ScalarType { ByteSize: 2 } or StringType or FlexType => cell.WithSize(OperandSize.Word),
      _ when symbol is { Storage: VariableStorage.Parameter, ByVal: false } => cell.WithSize(OperandSize.Word),
      _ => cell,
    };
  }

  private SemanticModel Model => model;
}
