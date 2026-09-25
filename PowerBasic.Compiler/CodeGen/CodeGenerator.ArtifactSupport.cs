using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// Non-emission support retained by the IR/x86-16 artifact shell after retirement of the syntax
/// emitter. This file contains configuration, source-validation and ABI/image helpers only.
/// </summary>
public sealed partial class CodeGenerator {

  /// <summary>Master middle-end optimizer gate.</summary>
  public bool Optimize { get; set; } = model.Dialect == Dialect.Pb36;

  /// <summary>The normalized integer-core generation selected by $CPU.</summary>
  private int CpuLevel => this.RuntimeTargetForRuntime().CpuLevel;

  /// <summary>True when 32-bit general-purpose instructions are legal.</summary>
  private bool Has32BitCpu => this.CpuLevel >= 386;

  /// <summary>True when 80486-or-later target policy may be used.</summary>
  private bool Cpu486 => this.CpuLevel >= 486;

  /// <summary>Target profitability model shared by the IR middle end, selector and machine emission.</summary>
  private TargetCost Cost => TargetCost.For(this.CpuLevel, this.OptimizeSpeed, this.OptimizeSize);

  /// <summary>
  /// Packed DOS descriptor bytes used only for source globals whose storage is shared with the
  /// IR/x86-16 artifact shell. This is an image-layout constant, not a syntax-emitter operation.
  /// </summary>
  private const int _PAGED_ARRAY_DESCRIPTOR_BYTES = 20;

  private MetaStmt? ResolveOptimizeMetastatement() {
    var metas = model.MetaStatements
      .Where(m => m.Command.Equals("OPTIMIZE", StringComparison.OrdinalIgnoreCase))
      .ToList();
    if (metas.Count > 1)
      this.Errors.Add(new(metas[1].Position, "only one $OPTIMIZE per module"));
    var meta = metas.FirstOrDefault();
    if (meta?.Arguments is [{ } mode, ..]
        && mode.Text.Equals("OFF", StringComparison.OrdinalIgnoreCase))
      this.Optimize = false;
    return meta;
  }

  private void ResolveOptimizeObjective(MetaStmt? meta) {
    if (meta?.Arguments is not [{ } mode, ..])
      return;
    this.OptimizeSpeed = mode.Text.Equals("SPEED", StringComparison.OrdinalIgnoreCase);
    this.OptimizeSize = mode.Text.Equals("SIZE", StringComparison.OrdinalIgnoreCase);
  }

  private ConstantFolder? _artifactFolder;
  private ConstantFolder OptFolder => this._artifactFolder ??= new(model.Equates, model.EnumMembers);

  /// <summary>
  /// Compatibility helper used by standalone optimization analyses and their tests; the production
  /// artifact shell no longer performs syntax-level folding.
  /// </summary>
  public static long WrapToType(long value, ScalarType type) => type switch {
    { ByteSize: 1, Signed: true } => (sbyte)value,
    { ByteSize: 1 } => (byte)value,
    { ByteSize: 2, Signed: true } => (short)value,
    { ByteSize: 2 } => (ushort)value,
    { ByteSize: 4, Signed: true } => (int)value,
    { ByteSize: 4 } => (uint)value,
    _ => value,
  };

  // ---- source validation retained before IR lowering -------------------------------------------

  private HashSet<DeferredSourceStmt>? _unreachableDeferred;

  private static HashSet<DeferredSourceStmt>? UnreachableDeferredSource(
      IReadOnlyList<Statement> body,
      ConstantFolder folder) {
    var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var node in body.SelectMany(OptReachability.DescendantNodes).Prepend(body))
      switch (node) {
        case GotoStmt g: referenced.Add(g.Target); break;
        case GosubStmt g: referenced.Add(g.Target); break;
        case OnGotoStmt og: foreach (var t in og.Targets) referenced.Add(t); break;
      }

    HashSet<DeferredSourceStmt>? dead = null;
    var reachable = true;
    foreach (var statement in body)
      switch (statement) {
        case LabelStmt label:
          if (referenced.Contains(label.Name))
            reachable = true;
          break;
        case DeferredSourceStmt d when !reachable:
          (dead ??= []).Add(d);
          break;
        default:
          if (Transfers(statement))
            reachable = false;
          break;
      }
    return dead;

    bool Transfers(Statement s) => s switch {
      GotoStmt or EndStmt => true,
      IfStmt { Then.Count: > 0 } i when i.ElseIfs.Count == 0 && i.Else == null
        && folder.TryFold(i.Condition) is { Integer: { } c } && c != 0 => Transfers(i.Then[^1]),
      _ => false,
    };
  }

  private bool ValidateDeferredInterpreterSource() {
    var before = this.Errors.Count;
    ValidateStatements(model.MainBody, this._unreachableDeferred);
    return this.Errors.Count == before;

    void ValidateStatements(IReadOnlyList<Statement> body, IReadOnlySet<DeferredSourceStmt>? unreachable = null) {
      foreach (var statement in body)
        switch (statement) {
          case DeferredSourceStmt deferred when unreachable?.Contains(deferred) == true:
            break;
          case DeferredSourceStmt deferred:
            this.Unsupported(deferred.Position,
              $"deferred {model.Dialect.DisplayName()} source whose path is not provably unreachable: {deferred.Text}");
            break;
          case IfStmt conditional:
            ValidateIf(conditional);
            break;
          case GroupStmt group:
            ValidateStatements(group.Body);
            break;
          case SelectStmt select:
            foreach (var arm in select.Arms)
              ValidateStatements(arm.Body);
            break;
          case ForStmt loop:
            ValidateStatements(loop.Body);
            break;
          case DoLoopStmt loop:
            ValidateStatements(loop.Body);
            break;
          case ForEachStmt loop:
            ValidateStatements(loop.Body);
            break;
          case TryStmt @try:
            ValidateStatements(@try.Body);
            if (@try.Catch is { } @catch)
              ValidateStatements(@catch);
            if (@try.Finally is { } @finally)
              ValidateStatements(@finally);
            break;
          case DeferStmt defer:
            ValidateStatements([defer.Deferred]);
            break;
        }
    }

    void ValidateIf(IfStmt conditional) {
      if (this.OptFolder.TryFold(conditional.Condition)?.Integer is not { } first) {
        ValidateStatements(conditional.Then);
        foreach (var (_, body) in conditional.ElseIfs)
          ValidateStatements(body);
        if (conditional.Else is { } unknownElse)
          ValidateStatements(unknownElse);
        return;
      }

      if (first != 0) {
        ValidateStatements(conditional.Then);
        return;
      }

      foreach (var (condition, body) in conditional.ElseIfs) {
        if (this.OptFolder.TryFold(condition)?.Integer is not { } folded) {
          ValidateStatements(body);
          continue;
        }
        if (folded == 0)
          continue;
        ValidateStatements(body);
        return;
      }

      if (conditional.Else is { } selectedElse)
        ValidateStatements(selectedElse);
    }
  }

  // ---- x86-16 source ABI metadata --------------------------------------------------------------

  private static int ParamSlotSize(VariableSymbol parameter)
    => parameter.ByVal ? Math.Max(2, (parameter.Type.Size + 1) & ~1) : 2;

  private static Reg[] ConventionRegisters(CallConvention convention) => convention switch {
    CallConvention.Watcall => [Reg.AX, Reg.DX, Reg.BX, Reg.CX],
    CallConvention.Fastcall => [Reg.AX, Reg.DX, Reg.BX],
    _ => [],
  };

  private static bool IsRegisterConvention(ProcedureSymbol procedure)
    => ConventionRegisters(procedure.CallConv).Length > 0;

  private static int RegisterParamCount(ProcedureSymbol procedure)
    => Math.Min(ConventionRegisters(procedure.CallConv).Length, procedure.Parameters.Count);

  private static bool HasUnsupportedRegisterParam(ProcedureSymbol procedure)
    => IsRegisterConvention(procedure) && procedure.Parameters.Any(parameter => ParamSlotSize(parameter) != 2);

  private static string UnsupportedRegisterParamMessage(ProcedureSymbol procedure)
    => $"{procedure.CallConv} {procedure.Name}: a register-convention parameter must be word-sized "
      + "(BYVAL <= 2 bytes or BYREF); multiword values need the full per-compiler ABI rules";

  private static bool PushesRightToLeft(ProcedureSymbol procedure)
    => procedure.CallConv is CallConvention.Cdecl or CallConvention.Stdcall or CallConvention.Watcall;

  private static bool CallerCleansStack(ProcedureSymbol procedure)
    => procedure.CallConv == CallConvention.Cdecl;

  /// <summary>
  /// Whether a source-visible procedure uses the ordinary stack ABI shape shared by the IR backend
  /// and DOS artifact/linker metadata. Register conventions and caller-clean/reversed-stack variants
  /// are represented explicitly elsewhere and are not interchangeable with this shape.
  /// </summary>
  private static bool IsBackendAbiConvention(ProcedureSymbol procedure)
    => !IsRegisterConvention(procedure)
       && !PushesRightToLeft(procedure)
       && !CallerCleansStack(procedure);

  private int LayoutFrame(ProcedureSymbol procedure) {
    this._frameLocalBytes = 0;
    if (HasUnsupportedRegisterParam(procedure))
      this.Errors.Add(new(procedure.Position, UnsupportedRegisterParamMessage(procedure)));

    var registerCount = RegisterParamCount(procedure);
    for (var i = 0; i < registerCount; ++i) {
      this._frameLocalBytes += 2;
      procedure.Parameters[i].Offset = -this._frameLocalBytes;
    }

    var offset = 4;
    var stackParameters = Enumerable.Range(
      registerCount, procedure.Parameters.Count - registerCount).ToList();
    foreach (var i in PushesRightToLeft(procedure)
      ? stackParameters
      : Enumerable.Reverse(stackParameters)) {
      procedure.Parameters[i].Offset = offset;
      offset += ParamSlotSize(procedure.Parameters[i]);
    }

    foreach (var symbol in this.StackLocalsOf(procedure)) {
      this._frameLocalBytes += Math.Max(2, (symbol.Type.Size + 1) & ~1);
      symbol.Offset = -this._frameLocalBytes;
    }

    return offset - 4;
  }

  private IEnumerable<VariableSymbol> StackLocalsOf(ProcedureSymbol procedure) {
    var seen = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
    foreach (var symbol in procedure.Variables.Values)
      if (symbol.Storage == VariableStorage.Local
          && (!symbol.IsArray || symbol.ArrayClass == ArrayClass.Stack)
          && seen.Add(symbol))
        yield return symbol;
  }

  // ---- source error-handler shape used to wrap selected machine functions ----------------------

  private static bool ContainsErrorHandling(IEnumerable<Statement> statements) {
    foreach (var statement in statements) {
      switch (statement) {
        case OnErrorStmt or ResumeStmt or TryStmt:
          return true;
        case SubDecl or FunctionDecl or DefFnDecl:
          continue;
      }
      if (ChildStatementBlocks(statement).Any(ContainsErrorHandling))
        return true;
    }
    return false;
  }

  private static IEnumerable<IReadOnlyList<Statement>> ChildStatementBlocks(Statement statement) {
    switch (statement) {
      case IfStmt conditional:
        yield return conditional.Then;
        foreach (var (_, body) in conditional.ElseIfs)
          yield return body;
        if (conditional.Else != null)
          yield return conditional.Else;
        break;
      case SelectStmt select:
        foreach (var arm in select.Arms)
          yield return arm.Body;
        break;
      case ForStmt loop:
        yield return loop.Body;
        break;
      case DoLoopStmt loop:
        yield return loop.Body;
        break;
      case TryStmt @try:
        yield return @try.Body;
        if (@try.Catch != null)
          yield return @try.Catch;
        if (@try.Finally != null)
          yield return @try.Finally;
        break;
    }
  }

  /// <summary>Direct storage cell used by IR global resolution and inline-asm symbol binding.</summary>
  private Mem? TryDirectCell(VariableSymbol symbol) => symbol.Storage switch {
    VariableStorage.Global or VariableStorage.Static => Mem.At(this.SlotOf(symbol)),
    _ when symbol.IsArray => Mem.At(this.SlotOf(symbol)),
    VariableStorage.Local => Mem.At(Reg.BP, symbol.Offset),
    VariableStorage.Parameter when symbol.ByVal => Mem.At(Reg.BP, symbol.Offset),
    _ => null,
  };

  // ---- CODEPTR32 far entry thunk plumbing ------------------------------------------------------

  private readonly Dictionary<ProcedureSymbol, Label> _farThunks
    = new(ReferenceEqualityComparer.Instance);

  private Label ThunkOf(ProcedureSymbol procedure) {
    if (!this._farThunks.TryGetValue(procedure, out var label))
      this._farThunks[procedure] = label =
        this._asm.DefineLabel($"thk_{Ir.IrLowering.IrNameOf(procedure)}");
    return label;
  }

  private void EmitFarThunks() {
    var asm = this._asm;
    foreach (var (procedure, label) in this._farThunks) {
      asm.MarkLabel(label);
      asm.Pop(Reg.AX);
      asm.Pop(Reg.DX);
      asm.Push(Reg.AX);
      asm.Jmp(this.ProcLabelOf(procedure));
    }
  }

  // Temporary compatibility category for mixed artifact helpers still being reduced. It is not an
  // executable lowering path.
  private enum ValueKind { Int16, Int32, Int64, Float, Str }

  private static ValueKind KindOf(PbType type) => type switch {
    ScalarType { IsFloat: true } => ValueKind.Float,
    ScalarType { ByteSize: <= 2 } => ValueKind.Int16,
    ScalarType { ByteSize: 8 } => ValueKind.Int64,
    ScalarType => ValueKind.Int32,
    PointerType or ProcPtrType => ValueKind.Int32,
    BcdType or MbfType => ValueKind.Float,
    StringType or FixedStringType or FlexType or AsciizType => ValueKind.Str,
    _ => ValueKind.Int16,
  };
}
