using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;
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
  /// <summary>
  /// pb36 inline-asm scheduling pre-pass: reorders maximal runs of consecutive single-instruction
  /// <c>!</c> statements in the main body and every procedure body to group memory/ALU operations
  /// (see <see cref="InlineAsmScheduler"/>) - dependency-preserving and therefore output-identical.
  /// Gated to $OPTIMIZE SPEED on pb36 with no error handler in scope (the "special environment"
  /// caveat: reordering must not be observable through a fault's resume point).
  /// </summary>
  private void ScheduleInlineAsmBlocks() {
    // EmitExecutable calls this before the runtime entry/procedure sections are generated. Configure
    // the normalized target here even when scheduling itself is disabled, so every runtime partial
    // sees the same architecture/feature surface.
    this._rt.Target = this.Optimize ? this.RuntimeTargetForRuntime() : RuntimeTarget.Baseline;

    if (!this.Optimize || !this.OptimizeSpeed || model.Dialect != Dialect.Pb36)
      return;

    if (!ContainsErrorHandling(model.MainBody)) {
      var reordered = ReorderInlineAsmRuns(model.MainBody);
      if (!ReferenceEquals(reordered, model.MainBody)) {
        model.MainBody.Clear();
        model.MainBody.AddRange(reordered);
      }
    }
    foreach (var proc in model.ProcedureList)
      if (proc.Body is { } body && !ContainsErrorHandling(body))
        proc.Body = ReorderInlineAsmRuns(body);
  }

  /// <summary>Returns <paramref name="body"/> with each consecutive inline-asm run reordered by the scheduler (the same list when nothing changed).</summary>
  private static IReadOnlyList<Statement> ReorderInlineAsmRuns(IReadOnlyList<Statement> body) {
    List<Statement>? result = null;
    var i = 0;
    while (i < body.Count) {
      if (body[i] is not InlineAsmStmt) {
        ++i;
        continue;
      }
      var j = i;
      while (j < body.Count && body[j] is InlineAsmStmt)
        ++j;
      var runLength = j - i;
      if (runLength >= 3) {
        var lines = new string[runLength];
        for (var k = 0; k < runLength; ++k)
          lines[k] = ((InlineAsmStmt)body[i + k]).Text;
        if (InlineAsmScheduler.Schedule(lines) is { } order) {
          result ??= [.. body];
          for (var k = 0; k < runLength; ++k)
            result[i + k] = body[i + order[k]];
        }
      }
      i = j;
    }
    return result ?? body;
  }

  private void EmitInlineAsm(InlineAsmStmt ia) {
    var resolver = new InlineAsmResolver(this);
    var target = this.RuntimeTargetForRuntime();
    if (this.TryEmitPolicyInlineAsm(ia.Text, resolver, target, out var policyError)) {
      if (policyError != null)
        this.Errors.Add(new(ia.Position, $"inline asm '{ia.Text.Trim()}': {policyError}"));
      return;
    }
    if (this.TryEmitTargetedInlineAsm(ia.Text, resolver, target, out var targetedError)) {
      if (targetedError != null)
        this.Errors.Add(new(ia.Position, $"inline asm '{ia.Text.Trim()}': {targetedError}"));
      return;
    }

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParse(ia.Text, resolver, out var error))
      this.Errors.Add(new(ia.Position, $"inline asm '{ia.Text.Trim()}': {error}"));
  }

  /// <summary>Suffix spellings tried when an inline-asm name has no explicit suffix.</summary>
  private static readonly TypeSuffix[] _ASM_SUFFIXES = [
    TypeSuffix.None, TypeSuffix.Integer, TypeSuffix.Long, TypeSuffix.Single, TypeSuffix.Double, TypeSuffix.Ext, TypeSuffix.String,
  ];

  private sealed class InlineAsmResolver(CodeGenerator owner) : IAsmSymbolResolver {

    public bool TryResolve(string name, out AsmSymbol symbol) {
      // FUNCTION denotes the result variable of the enclosing FUNCTION
      if (name.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase)
          && owner._currentProc is { IsFunction: true } fn
          && fn.Variables.TryGetValue(fn.Name, out var result)) {
        symbol = AsmSymbol.OfMemory(owner.AsmCellOf(result));
        return true;
      }

      // explicit BASIC type suffix on the operand name (Foff%, x??, d#)
      if (SplitSuffix(name) is var (bare, explicitSuffix) && explicitSuffix != TypeSuffix.None) {
        if (owner.LookupVariable(bare, explicitSuffix) is { } suffixed) {
          symbol = AsmSymbol.OfMemory(owner.AsmCellOf(suffixed));
          return true;
        }
        name = bare; // fall through: an AS-declared variable may match the bare name
      }

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

      // string-manager runtime exports callable from inline asm (PB manual ABI)
      if (Runtime.InlineAsmExports.Canonical(name) is { } canonical) {
        symbol = AsmSymbol.OfLabel(owner._asm.Lbl(canonical));
        return true;
      }

      symbol = default;
      return false;
    }
  }

  /// <summary>Splits a trailing BASIC type suffix off an inline-asm operand name.</summary>
  private static (string Bare, TypeSuffix Suffix) SplitSuffix(string name) {
    foreach (var (text, suffix) in _suffixSpellings)
      if (name.Length > text.Length && name.EndsWith(text, StringComparison.Ordinal))
        return (name[..^text.Length], suffix);
    return (name, TypeSuffix.None);
  }

  private static readonly (string Text, TypeSuffix Suffix)[] _suffixSpellings = [
    ("???", TypeSuffix.Dword), ("??", TypeSuffix.Word), ("?", TypeSuffix.Byte),
    ("&&", TypeSuffix.Quad), ("&", TypeSuffix.Long),
    ("##", TypeSuffix.Ext), ("#", TypeSuffix.Double),
    ("%", TypeSuffix.Integer), ("!", TypeSuffix.Single), ("$", TypeSuffix.String),
  ];

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
