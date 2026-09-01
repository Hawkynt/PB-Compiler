using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0182 — small local array scalar replacement, on the IR.
///
/// A tiny non-escaping array indexed only by constants is N variables wearing one name. Splitting it
/// lets mem2reg promote each element into SSA, which is what makes the rest of the pipeline able to
/// see through it: left as an array, one store to <c>a(0)</c> makes every later read of <c>a(1)</c>
/// unanalysable, because nothing proves they do not alias.
///
/// Two things are tested, and only one is about the IR: that it actually splits (a pass that quietly
/// declines everything passes any behavioural test), and that the program still prints the same -
/// checked by rendering the IR back to BASIC and running it.
/// </summary>
[TestFixture]
public sealed class ScalarReplaceArraysTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  /// <summary>
  /// Lowered and folded. The pass needs constant subscripts, and a subscript is not constant in the
  /// raw lowering - it is index * sizeof(element) with the index still an expression. Constant
  /// propagation has to have run first, which is why the pass belongs after SCCP in the pipeline
  /// rather than before it.
  /// </summary>
  private static IrModule Lowered(string source) {
    var module = IrLowering.TryLowerModule(Bind(source), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);
    return module!;
  }

  private static int Split(IrModule module) {
    var count = 0;
    foreach (var fn in module.Functions)
      if (!fn.IsDeclaration)
        count += ScalarReplaceArrays.Run(fn);
    return count;
  }

  private static string Run(string source) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  // the array has to be LOCAL: a module-level DIM becomes a global, which is storage the whole
  // program shares and nothing here may split
  private const string _constantSubscripts = """
    PRINT Total%()
    END
    FUNCTION Total%()
      DIM a(0 TO 3) AS INTEGER
      a(0) = 10
      a(1) = 20
      a(2) = 30
      a(3) = a(0) + a(1) + a(2)
      Total% = a(3)
    END FUNCTION
    """;

  /// <summary>
  /// The pass is in the standard pipeline, so by the time a module has been through it the array is
  /// already gone - which is what this asserts. Calling the pass by hand here would find nothing
  /// left to do and prove only that it is idempotent.
  /// </summary>
  [Test]
  public void Split_GivenConstantSubscripts_ThenTheArrayBecomesIndividualSlots() {
    var module = Lowered(_constantSubscripts);
    var main = module.Functions.Single(f => !f.IsDeclaration
      && !f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    Assert.That(main.Blocks.SelectMany(b => b.Instructions).OfType<IrAlloca>().Any(a => a.Count > 1),
      Is.False, "no multi-element slot should be left");
    Assert.That(main.Blocks.SelectMany(b => b.Instructions).OfType<IrGep>(), Is.Empty,
      "and nothing indexes into one any more");
  }

  /// <summary>The whole point: split, promote, and the values become visible to everything after.</summary>
  [Test]
  public void Split_GivenConstantSubscripts_ThenThePipelineCanSeeThroughTheArray() {
    var module = Lowered(_constantSubscripts);
    Split(module);
    IrPassManager.Standard().RunOnModule(module);

    var main = module.FindFunction("main")!;
    Assert.That(main.Blocks.SelectMany(b => b.Instructions).OfType<IrBinary>(), Is.Empty,
      "10 + 20 + 30 should be folded away once the elements are ordinary values");
  }

  [Test]
  public void Split_GivenConstantSubscripts_ThenTheProgramStillPrintsTheSame() {
    var expected = Run(_constantSubscripts);

    Assert.That(Run(IrBasicWriter.Write(Lowered(_constantSubscripts))), Is.EqualTo(expected));
  }

  /// <summary>The pass itself, on IR that has not been through the pipeline's copy of it.</summary>
  [Test]
  public void Split_CalledDirectly_ThenItReportsWhatItSplit() {
    var module = IrLowering.TryLowerModule(Bind(_constantSubscripts), out _)!;
    foreach (var fn in module.Functions)
      if (!fn.IsDeclaration) {
        Mem2Reg.Run(fn);
        InstCombine.Run(fn);
        Sccp.Run(fn);
      }

    Assert.That(Split(module), Is.EqualTo(1), "the one local array should be split");
  }

  /// <summary>A computed subscript could name any element, so splitting would lose the connection.</summary>
  [Test]
  public void Split_GivenARuntimeSubscript_ThenItDeclines() {
    var module = Lowered("""
      PRINT Pick%(2)
      END
      FUNCTION Pick%(BYVAL i%)
        DIM a(0 TO 3) AS INTEGER
        a(0) = 7
        Pick% = a(i%)
      END FUNCTION
      """);

    Assert.That(Split(module), Is.Zero);
  }

  /// <summary>Too many elements to be worth splitting: it declines rather than mint fifty variables.</summary>
  [Test]
  public void Split_GivenALargeArray_ThenItDeclines() {
    var module = Lowered("""
      PRINT Big%()
      END
      FUNCTION Big%()
        DIM a(0 TO 49) AS INTEGER
        a(0) = 1
        a(49) = 2
        Big% = a(0) + a(49)
      END FUNCTION
      """);

    Assert.That(Split(module), Is.Zero);
  }

  /// <summary>
  /// Opaque pointers permit a word store through an i8-array address. That store spans two elements,
  /// so treating it as the write of element zero would drop the high byte when the array is split.
  /// O0339's tiny memcpy expansion exposed exactly this shape for packed UDT storage.
  /// </summary>
  [Test]
  public void Split_GivenAByteArrayWithAWideStore_ThenItDeclines() {
    var fn = new IrFunction("wide", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var bytes = entry.Append(new IrAlloca(IrType.I8) { Count = 4 });
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, -123), bytes));
    var second = entry.Append(new IrGep(bytes, new IrConstantInt(IrType.I32, 1)));
    entry.Append(new IrLoad(IrType.I8, second));
    entry.Append(new IrRet());

    Assert.That(ScalarReplaceArrays.Run(fn), Is.Zero,
      "a wider access aliases adjacent byte elements and cannot be scalarized one byte at a time");
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }
}
