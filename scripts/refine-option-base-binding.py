from pathlib import Path


def replace(path: str, old: str, new: str, count: int = 1) -> None:
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    actual = text.count(old)
    if actual != count:
        raise SystemExit(f'{path}: expected {count} matches, found {actual}')
    p.write_text(text.replace(old, new), encoding='utf-8')

# SemanticModel: OPTION BASE is lexical, so bind it to the declaration rather than storing one final module value.
replace(
    'PowerBasic.Compiler/Semantics/SemanticModel.cs',
    '''  /// <summary>Implicit lower bound supplied when an array dimension omits <c>TO</c>; set by module-level <c>OPTION BASE</c>.</summary>\n  public int OptionBase { get; set; }\n''',
    '''  /// <summary>Effective <c>OPTION BASE</c> at each source array declaration/REDIM, captured in source order.</summary>\n  public Dictionary<VariableDecl, int> ArrayOptionBases { get; } = new(ReferenceEqualityComparer.Instance);\n\n  /// <summary>Returns the implicit lower bound in effect at <paramref name="declaration"/> (zero when none was specified).</summary>\n  public int OptionBaseOf(VariableDecl declaration)\n    => this.ArrayOptionBases.TryGetValue(declaration, out var optionBase) ? optionBase : 0;\n\n  /// <summary>Returns array bounds with an omitted lower bound materialized from the declaration's <c>OPTION BASE</c>.</summary>\n  public IReadOnlyList<(Expression? Lower, Expression Upper)> ArrayBoundsOf(VariableDecl declaration) {\n    if (declaration.ArrayBounds is not { } bounds || this.OptionBaseOf(declaration) == 0)\n      return declaration.ArrayBounds ?? [];\n    return [.. bounds.Select(bound => (bound.Lower ?? new IntegerLiteralExpr(declaration.Position, 1, TypeSuffix.None), bound.Upper))];\n  }\n''')

# Capture lexical OPTION BASE before the later body-binding pass loses source-order state.
replace(
    'PowerBasic.Compiler/Semantics/Binder.cs',
    '''    binder.SeedInternalVariables();\n    binder.CollectRedims(binder._unit.Statements);\n    binder.ScanModule();\n''',
    '''    binder.SeedInternalVariables();\n    binder.CollectRedims(binder._unit.Statements);\n    binder.CaptureArrayOptionBases();\n    binder.ScanModule();\n''')

replace(
    'PowerBasic.Compiler/Semantics/Binder.cs',
    '''  private ConstantValue? FoldDesugared(Expression e)\n    => this._model.Desugared.TryGetValue(e, out var d) ? this._folder.TryFold(d) : null;\n\n  #region pass 1 - module scan\n''',
    '''  private ConstantValue? FoldDesugared(Expression e)\n    => this._model.Desugared.TryGetValue(e, out var d) ? this._folder.TryFold(d) : null;\n\n  /// <summary>\n  /// Captures the lexical OPTION BASE at every source DIM/REDIM before procedure bodies are bound.\n  /// PowerBASIC permits OPTION BASE to appear between declarations, so using the binder's final\n  /// module value later would retroactively change arrays that appeared before it.\n  /// </summary>\n  private void CaptureArrayOptionBases() {\n    var optionBase = 0;\n    foreach (var statement in this._unit.Statements) {\n      if (statement is CommandStmt { Keyword: "OPTION BASE", Arguments: [IntegerLiteralExpr { Value: 0 or 1 } b] }) {\n        optionBase = (int)b.Value;\n        continue;\n      }\n      this.CaptureArrayOptionBases([statement], optionBase);\n    }\n  }\n\n  private void CaptureArrayOptionBases(IReadOnlyList<Statement> statements, int optionBase) {\n    foreach (var statement in statements)\n      switch (statement) {\n        case DimStmt dim:\n          this.CaptureArrayOptionBases(dim.Variables, optionBase);\n          break;\n        case RedimStmt redim:\n          this.CaptureArrayOptionBases(redim.Variables, optionBase);\n          break;\n        case SubDecl sub:\n          this.CaptureArrayOptionBases(sub.Body, optionBase);\n          break;\n        case FunctionDecl function:\n          this.CaptureArrayOptionBases(function.Body, optionBase);\n          break;\n        case DefFnDecl { BlockBody: { } body }:\n          this.CaptureArrayOptionBases(body, optionBase);\n          break;\n        default:\n          foreach (var block in ChildBlocks(statement))\n            this.CaptureArrayOptionBases(block, optionBase);\n          break;\n      }\n  }\n\n  private void CaptureArrayOptionBases(IReadOnlyList<VariableDecl> variables, int optionBase) {\n    foreach (var variable in variables)\n      if (variable.ArrayBounds != null)\n        this._model.ArrayOptionBases[variable] = optionBase;\n  }\n\n  private int ArrayOptionBase(VariableDecl declaration)\n    => this._model.OptionBaseOf(declaration);\n\n  #region pass 1 - module scan\n''')

replace(
    'PowerBasic.Compiler/Semantics/Binder.cs',
    '''        case CommandStmt { Keyword: "OPTION BASE" } ob when ob.Arguments is [IntegerLiteralExpr { Value: 0 or 1 } b]:\n          this._optionBase = (int)b.Value;\n          this._model.OptionBase = this._optionBase;\n          break;\n''',
    '''        case CommandStmt { Keyword: "OPTION BASE" } ob when ob.Arguments is [IntegerLiteralExpr { Value: 0 or 1 } b]:\n          this._optionBase = (int)b.Value;\n          break;\n''')

replace(
    'PowerBasic.Compiler/Semantics/Binder.cs',
    '''      var lower = lowerExpr == null ? this._optionBase : (int?)(this._folder.TryFold(lowerExpr)?.Integer);\n''',
    '''      var lower = lowerExpr == null ? this.ArrayOptionBase(v) : (int?)(this._folder.TryFold(lowerExpr)?.Integer);\n''')

replace(
    'PowerBasic.Compiler/Semantics/Binder.cs',
    '''    var lower = this._optionBase;\n    int upper;\n    if (v.ArrayBounds is [var (lowerExpr, upperExpr)] && this._folder.TryFold(upperExpr)?.Integer is { } u) {\n''',
    '''    var lower = this.ArrayOptionBase(v);\n    int upper;\n    if (v.ArrayBounds is [var (lowerExpr, upperExpr)] && this._folder.TryFold(upperExpr)?.Integer is { } u) {\n''')

# Direct backend: normalize at the declaration boundary, then keep all existing allocator fallbacks at base zero.
replace(
    'PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs',
    '''      this.EmitClassedAllocation(symbol, v.ArrayBounds, dim.AtAddress, dim.Position, skipZero);\n''',
    '''      this.EmitClassedAllocation(symbol, model.ArrayBoundsOf(v), dim.AtAddress, dim.Position, skipZero);\n''')

replace(
    'PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs',
    '''      if (redim.Preserve) {\n        this.EmitRedimPreserve(symbol, v.ArrayBounds, redim.Position);\n        continue;\n      }\n      this.EmitClassedAllocation(symbol, v.ArrayBounds, null, redim.Position, skipZero);\n''',
    '''      var bounds = model.ArrayBoundsOf(v);\n      if (redim.Preserve) {\n        this.EmitRedimPreserve(symbol, bounds, redim.Position);\n        continue;\n      }\n      this.EmitClassedAllocation(symbol, bounds, null, redim.Position, skipZero);\n''')

replace('PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs', '      asm.Mov(Reg.AX, model.OptionBase);\n      asm.Xor(Reg.DX, Reg.DX);\n', '      asm.Xor(Reg.AX, Reg.AX);\n      asm.Xor(Reg.DX, Reg.DX);\n')
replace('PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs', '        asm.Mov(Mem.Word(descriptor, 8 + d * 4), model.OptionBase);\n', '        asm.Mov(Mem.Word(descriptor, 8 + d * 4), (Imm)0);\n', count=2)
replace(
    'PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs',
    '''      if (lower != null)\n        this.EmitInt16Argument(lower);\n      else\n        asm.Mov(Reg.AX, model.OptionBase);\n\n      var lowerOk = asm.DefineLabel();\n''',
    '''      if (lower != null)\n        this.EmitInt16Argument(lower);\n      else\n        asm.Xor(Reg.AX, Reg.AX);\n\n      var lowerOk = asm.DefineLabel();\n''')

# Routed conventional arrays: normalize each source declaration before allocation.
replace(
    'PowerBasic.Compiler/Ir/IrLowering.cs',
    '''      if (v.ArrayBounds is not { Count: > 0 } dims)\n        continue;\n      if (this.ArrayVariable(v) is not { Type: ArrayType arr } symbol || !arr.IsDynamic)\n        continue;                                    // static array or scalar: laid out at compile time\n      if (dims.Count != arr.Rank)\n        throw new IrLoweringException("DIM rank mismatch");\n      this.AllocateDynamicArray(symbol, arr, dims, preserve: false);\n''',
    '''      if (v.ArrayBounds is not { Count: > 0 })\n        continue;\n      if (this.ArrayVariable(v) is not { Type: ArrayType arr } symbol || !arr.IsDynamic)\n        continue;                                    // static array or scalar: laid out at compile time\n      var dims = this._model.ArrayBoundsOf(v);\n      if (dims.Count != arr.Rank)\n        throw new IrLoweringException("DIM rank mismatch");\n      this.AllocateDynamicArray(symbol, arr, dims, preserve: false);\n''')

replace(
    'PowerBasic.Compiler/Ir/IrLowering.cs',
    '''      if (v.ArrayBounds is not { } dims || dims.Count != arr.Rank)\n        throw new IrLoweringException("REDIM rank mismatch");\n''',
    '''      if (v.ArrayBounds == null)\n        throw new IrLoweringException("REDIM without bounds");\n      var dims = this._model.ArrayBoundsOf(v);\n      if (dims.Count != arr.Rank)\n        throw new IrLoweringException("REDIM rank mismatch");\n''')

replace('PowerBasic.Compiler/Ir/IrLowering.cs', '? new IrConstantInt(IrType.I32, this._model.OptionBase)\n', '? new IrConstantInt(IrType.I32, 0)\n')

# Routed paged DIM has its own entry point; REDIM already comes through LowerRedim above.
replace(
    'PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs',
    '''      if (v.ArrayBounds is not { } dims)\n        throw new IrLoweringException($"DIM {d.Class} {v.Name} without array bounds");\n      if (this.ArrayVariable(v) is not { Type: ArrayType arr } symbol)\n        throw new IrLoweringException($"DIM {d.Class}: no array symbol for {v.Name}");\n      this.LowerPagedAllocation(symbol, arr, dims);\n''',
    '''      if (v.ArrayBounds == null)\n        throw new IrLoweringException($"DIM {d.Class} {v.Name} without array bounds");\n      if (this.ArrayVariable(v) is not { Type: ArrayType arr } symbol)\n        throw new IrLoweringException($"DIM {d.Class}: no array symbol for {v.Name}");\n      this.LowerPagedAllocation(symbol, arr, this._model.ArrayBoundsOf(v));\n''')
replace('PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs', '? new IrConstantInt(IrType.I32, this._model.OptionBase)\n', '? new IrConstantInt(IrType.I32, 0)\n')

# Pin the lexical transition: the initial dynamic DIM is base 0, the later REDIM is base 1.
replace(
    'PowerBasic.Compiler.Tests/Backend/BackendArrayLayoutTests.cs',
    '''  [Test]\n  public void OptionBaseOne_DynamicDimAndRedimPreserve_UseOneAsEveryImplicitLowerBound() {\n''',
    '''  [Test]\n  public void OptionBaseChangedBetweenDynamicDimAndRedim_IsCapturedPerDeclaration() {\n    var (direct, routed, names) = RunBothWays("""\n      DIM a%(2)\n      PRINT LBOUND(a%)\n      PRINT UBOUND(a%)\n      OPTION BASE 1\n      REDIM a%(3)\n      PRINT LBOUND(a%)\n      PRINT UBOUND(a%)\n      """);\n\n    Assert.That(names, Does.Contain("main"));\n    Assert.That(routed, Is.EqualTo(direct));\n    Assert.That(Lines(routed), Is.EqualTo(new[] { "0", "2", "1", "3" }));\n  }\n\n  [Test]\n  public void OptionBaseOne_DynamicDimAndRedimPreserve_UseOneAsEveryImplicitLowerBound() {\n''')

# Keep the existing semantics-test documentation honest about static vs runtime bounds.
replace(
    'PowerBasic.Compiler.Tests/Semantics/OptionBaseTests.cs',
    '''/// The statement is read by the binder's module pre-pass rather than by the code generator, because\n/// it has to take effect on DIMs that come after it in the file but are processed in the same sweep.\n/// Nothing is emitted for it: by the time the code generator runs, the bounds already carry the\n/// answer. That is why the runtime checks below ask LBOUND and UBOUND rather than looking at bytes.\n''',
    '''/// The statement is read by the binder's module pre-pass rather than emitted at run time. Static\n/// bounds bake the answer into their array type; dynamic DIM/REDIM declarations keep the effective\n/// base in a semantic side table so later lowering sees the value that was in force at that exact\n/// source position. The runtime checks below therefore ask LBOUND/UBOUND rather than looking at bytes.\n''')
