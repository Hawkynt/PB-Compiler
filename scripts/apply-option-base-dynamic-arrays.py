from pathlib import Path


def replace(path: str, old: str, new: str, count: int = 1) -> None:
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    actual = text.count(old)
    if actual != count:
        raise SystemExit(f'{path}: expected {count} matches, found {actual}')
    p.write_text(text.replace(old, new), encoding='utf-8')

replace(
    'PowerBasic.Compiler/Semantics/SemanticModel.cs',
    '''  /// <summary>The dialect that governs runtime quirk emulation: the <c>$COMPAT</c> override when set, else the compile dialect.</summary>\n  public Dialect EffectiveDialect => this.CompatDialect ?? this.Dialect;\n''',
    '''  /// <summary>The dialect that governs runtime quirk emulation: the <c>$COMPAT</c> override when set, else the compile dialect.</summary>\n  public Dialect EffectiveDialect => this.CompatDialect ?? this.Dialect;\n\n  /// <summary>Implicit lower bound supplied when an array dimension omits <c>TO</c>; set by module-level <c>OPTION BASE</c>.</summary>\n  public int OptionBase { get; set; }\n''')

replace(
    'PowerBasic.Compiler/Semantics/Binder.cs',
    '''        case CommandStmt { Keyword: "OPTION BASE" } ob when ob.Arguments is [IntegerLiteralExpr { Value: 0 or 1 } b]:\n          this._optionBase = (int)b.Value;\n          break;\n''',
    '''        case CommandStmt { Keyword: "OPTION BASE" } ob when ob.Arguments is [IntegerLiteralExpr { Value: 0 or 1 } b]:\n          this._optionBase = (int)b.Value;\n          this._model.OptionBase = this._optionBase;\n          break;\n''')

replace(
    'PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs',
    '''    } else {\n      asm.Xor(Reg.AX, Reg.AX);\n      asm.Xor(Reg.DX, Reg.DX);\n    }\n    asm.Mov(Mem.Word(descriptor, 8), Reg.AX);\n''',
    '''    } else {\n      asm.Mov(Reg.AX, model.OptionBase);\n      asm.Xor(Reg.DX, Reg.DX);\n    }\n    asm.Mov(Mem.Word(descriptor, 8), Reg.AX);\n''')

replace(
    'PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs',
    '''      } else\n        asm.Mov(Mem.Word(descriptor, 8 + d * 4), (Imm)0);\n''',
    '''      } else\n        asm.Mov(Mem.Word(descriptor, 8 + d * 4), model.OptionBase);\n''',
    count=2)

replace(
    'PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs',
    '''      if (lower != null)\n        this.EmitInt16Argument(lower);\n      else\n        asm.Xor(Reg.AX, Reg.AX);\n\n      var lowerOk = asm.DefineLabel();\n''',
    '''      if (lower != null)\n        this.EmitInt16Argument(lower);\n      else\n        asm.Mov(Reg.AX, model.OptionBase);\n\n      var lowerOk = asm.DefineLabel();\n''')

replace(
    'PowerBasic.Compiler/Ir/IrLowering.cs',
    '''    for (var k = 0; k < dims.Count; ++k) {\n      var (lower, upper) = dims[k];\n      var lo = lower is null\n        ? new IrConstantInt(IrType.I32, 0)\n        : this.Coerce(this.LowerExpr(lower), this._model.TypeOf(lower), PbType.Long);\n''',
    '''    for (var k = 0; k < dims.Count; ++k) {\n      var (lower, upper) = dims[k];\n      var lo = lower is null\n        ? new IrConstantInt(IrType.I32, this._model.OptionBase)\n        : this.Coerce(this.LowerExpr(lower), this._model.TypeOf(lower), PbType.Long);\n''')

replace(
    'PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs',
    '''    var (lower, upper) = dims[0];\n    var lo = lower is null\n      ? new IrConstantInt(IrType.I32, 0)\n      : this.Coerce(this.LowerExpr(lower), this._model.TypeOf(lower), PbType.Long);\n''',
    '''    var (lower, upper) = dims[0];\n    var lo = lower is null\n      ? new IrConstantInt(IrType.I32, this._model.OptionBase)\n      : this.Coerce(this.LowerExpr(lower), this._model.TypeOf(lower), PbType.Long);\n''')

replace(
    'PowerBasic.Compiler.Tests/Backend/BackendArrayLayoutTests.cs',
    '''  [Test]\n  public void RedimPreserve_WhenOnlyTheLastDimensionGrows_KeepsEveryExistingElementInPlace() {\n''',
    '''  [Test]\n  public void OptionBaseOne_DynamicDimAndRedimPreserve_UseOneAsEveryImplicitLowerBound() {\n    var (direct, routed, names) = RunBothWays("""\n      OPTION BASE 1\n      DIM a%(4, 5)\n      a%(4, 5) = 45\n      PRINT LBOUND(a%, 1)\n      PRINT UBOUND(a%, 1)\n      PRINT LBOUND(a%, 2)\n      PRINT UBOUND(a%, 2)\n\n      REDIM PRESERVE a%(4, 6)\n      PRINT LBOUND(a%, 1)\n      PRINT UBOUND(a%, 1)\n      PRINT LBOUND(a%, 2)\n      PRINT UBOUND(a%, 2)\n      PRINT a%(4, 5)\n      """);\n\n    Assert.That(names, Does.Contain("main"));\n    Assert.That(routed, Is.EqualTo(direct));\n    Assert.That(Lines(routed), Is.EqualTo(new[] { "1", "4", "1", "5", "1", "4", "1", "6", "45" }));\n  }\n\n  [Test]\n  public void RedimPreserve_WhenOnlyTheLastDimensionGrows_KeepsEveryExistingElementInPlace() {\n''')
