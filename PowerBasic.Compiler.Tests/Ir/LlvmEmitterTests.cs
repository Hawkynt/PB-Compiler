using System.Diagnostics;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// The strict LLVM text emitter. The snapshot tests pin the spelling; the toolchain
/// tests feed the emitted module to the real LLVM tools (llvm-as / llc), proving the
/// IR is valid LLVM and can be lowered to a native non-16-bit-DOS target.
/// </summary>
[TestFixture]
public sealed class LlvmEmitterTests {

  private static IrModule LowerOptimizeToModule(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var fn = IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35))!;
    IrMiddleEndPipeline.Standard().RunToFixpoint(fn);
    var module = new IrModule("T");
    module.AddFunction(fn);
    return module;
  }

  [Test]
  public void Emit_AddFunction_UsesLlvmSpelling() {
    var a = new IrArgument(IrType.I32, 0, "a");
    var b = new IrArgument(IrType.I32, 1, "b");
    var fn = new IrFunction("add", IrType.I32, [a, b]);
    var builder = new IrBuilder(fn.CreateBlock("entry"));
    var sum = builder.Add(a, b);
    sum.Name = "r";
    builder.Ret(sum);

    Assert.That(LlvmEmitter.Emit(fn), Is.EqualTo(
      "define i32 @add(i32 %a, i32 %b) {\n" +
      "entry:\n" +
      "  %r = add i32 %a, %b\n" +
      "  ret i32 %r\n" +
      "}\n"));
  }

  [Test]
  public void Emit_FloatTypes_UseLlvmNames() {
    var fn = new IrFunction("f", IrType.F64, [new IrArgument(IrType.F32, 0, "x")]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var wide = b.Cast(IrCastOp.FPExt, fn.Parameters[0], IrType.F64);
    b.Ret(wide);

    var text = LlvmEmitter.Emit(fn);
    Assert.That(text, Does.Contain("define double @f(float %x)"));
    Assert.That(text, Does.Contain("fpext float %x to double"));
  }

  [Test]
  public void Emit_Gep_UsesGetElementPtrI8Form() {
    var p = new IrArgument(IrType.Ptr, 0, "p");
    var fn = new IrFunction("g", IrType.Ptr, [p]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    b.Ret(b.Gep(p, IrBuilder.ConstI32(4)));

    Assert.That(LlvmEmitter.Emit(fn), Does.Contain("getelementptr i8, ptr %p, i32 4"));
  }

  [Test]
  public void Emit_LoadAndStoreWithoutAlignmentFacts_UseConservativeByteAlignment() {
    var p = new IrArgument(IrType.Ptr, 0, "p");
    var v = new IrArgument(IrType.I32, 1, "v");
    var fn = new IrFunction("memory", IrType.I32, [p, v]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    b.Store(v, p);
    b.Ret(b.Load(IrType.I32, p));

    var text = LlvmEmitter.Emit(fn);
    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("store i32 %v, ptr %p, align 1"));
      Assert.That(text, Does.Contain("load i32, ptr %p, align 1"));
    });
  }

  [Test]
  public void LlvmAs_AcceptsTheEmittedModule() {
    RequireTool("llvm-as");
    var module = LowerOptimizeToModule(
      "s% = 0\nFOR i% = 1 TO 10\n  s% = s% + i%\nNEXT i%\nIF s% > 5 THEN\n  s% = s% - 1\nEND IF");
    var ll = LlvmEmitter.Emit(module, "x86_64-unknown-linux-gnu");

    var (code, err) = Run("llvm-as", "-o /dev/null -", ll);

    Assert.That(code, Is.EqualTo(0), $"llvm-as rejected the module:\n{err}\n--- IR ---\n{ll}");
  }

  [Test]
  public void Llc_LowersTheEmittedModuleToNativeX8664() {
    RequireTool("llc");
    var module = LowerOptimizeToModule("a% = 7\nb% = 3\nc% = a% * b% + 1");
    var ll = LlvmEmitter.Emit(module, "x86_64-unknown-linux-gnu");

    var (code, err) = Run("llc", "-filetype=asm -o /dev/null -", ll);

    Assert.That(code, Is.EqualTo(0), $"llc could not lower the module to x86-64:\n{err}\n--- IR ---\n{ll}");
  }

  // ---- local names: one LLVM symbol table per function ----------------------
  //
  // LLVM keeps the values AND the block labels of a function in one symbol table, and a name it
  // reads twice is "multiple definition of local value". The IR names are only hints - two FOR i%
  // loops, two GOSUB stacks - so the emitter has to make them unique, and quote the ones LLVM
  // cannot read bare (`division by zero.ok7`, the label RaiseWhen gives its continuation block).

  /// <summary>Two values called <c>i</c>, as two <c>FOR i%</c> loops of one procedure produce.</summary>
  private static IrFunction TwoValuesNamedI() {
    var a = new IrArgument(IrType.I32, 0, "a");
    var fn = new IrFunction("twice", IrType.I32, [a]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var first = b.Add(a, IrBuilder.ConstI32(1));
    first.Name = "i";
    var second = b.Add(first, IrBuilder.ConstI32(2));
    second.Name = "i";
    b.Ret(second);
    return fn;
  }

  [Test]
  public void Emit_GivenTwoValuesWithOneName_ThenEachGetsItsOwnLocalName() {
    Assert.That(LlvmEmitter.Emit(TwoValuesNamedI()), Is.EqualTo(
      "define i32 @twice(i32 %a) {\n" +
      "entry:\n" +
      "  %i = add i32 %a, 1\n" +
      "  %i.1 = add i32 %i, 2\n" +
      "  ret i32 %i.1\n" +
      "}\n"));
  }

  [Test]
  public void Emit_GivenTheSuffixedNameIsTakenToo_ThenTheNextFreeSuffixIsUsed() {
    var fn = new IrFunction("taken", IrType.I32, [new IrArgument(IrType.I32, 0, "a")]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var x = b.Add(fn.Parameters[0], IrBuilder.ConstI32(1));
    x.Name = "a.1";
    var y = b.Add(x, IrBuilder.ConstI32(2));
    y.Name = "a";
    b.Ret(y);

    var text = LlvmEmitter.Emit(fn);
    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("define i32 @taken(i32 %a)"));
      Assert.That(text, Does.Contain("%a.1 = add i32 %a, 1"));
      Assert.That(text, Does.Contain("%a.2 = add i32 %a.1, 2"));
      Assert.That(text, Does.Contain("ret i32 %a.2"));
    });
  }

  [Test]
  public void Emit_GivenAValueNamedLikeABlock_ThenTheLabelKeepsItsNameAndTheValueMoves() {
    var fn = new IrFunction("clash", IrType.I32, [new IrArgument(IrType.I32, 0, "a")]);
    var entry = fn.CreateBlock("entry");
    var next = fn.CreateBlock("next");
    var b = new IrBuilder(entry);
    var v = b.Add(fn.Parameters[0], IrBuilder.ConstI32(1));
    v.Name = "next";
    b.Br(next);
    new IrBuilder(next).Ret(v);

    var text = LlvmEmitter.Emit(fn);
    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("%next.1 = add i32 %a, 1"));
      Assert.That(text, Does.Contain("br label %next\n"));
      Assert.That(text, Does.Contain("\nnext:\n"));
      Assert.That(text, Does.Contain("ret i32 %next.1"));
    });
  }

  [Test]
  public void Emit_GivenTwoBlocksWithOneLabel_ThenEachBlockGetsItsOwnLabel() {
    var fn = new IrFunction("blocks", IrType.Void, []);
    var entry = fn.CreateBlock("ok");
    var second = fn.CreateBlock("ok");
    new IrBuilder(entry).Br(second);
    new IrBuilder(second).Ret();

    Assert.That(LlvmEmitter.Emit(fn), Is.EqualTo(
      "define void @blocks() {\n" +
      "ok:\n" +
      "  br label %ok.1\n" +
      "ok.1:\n" +
      "  ret void\n" +
      "}\n"));
  }

  /// <summary>The shape of <c>NARROW.BAS</c>: a checked division continues in a block whose label has spaces.</summary>
  private static IrFunction SpacedLabelFunction() {
    var a = new IrArgument(IrType.I16, 0, "a");
    var fn = new IrFunction("narrow", IrType.I16, [a]);
    var entry = fn.CreateBlock("entry");
    var trap = fn.CreateBlock("division by zero.trap6");
    var ok = fn.CreateBlock("division by zero.ok7");
    var b = new IrBuilder(entry);
    b.CondBr(b.Cmp(IrCmpPred.Eq, a, new IrConstantInt(IrType.I16, 0)), trap, ok);
    new IrBuilder(trap).Br(ok);
    var join = new IrBuilder(ok);
    var phi = join.Phi(IrType.I16);
    phi.Name = "i";
    phi.AddIncoming(a, entry);
    phi.AddIncoming(new IrConstantInt(IrType.I16, 1), trap);
    join.Ret(phi);
    return fn;
  }

  [Test]
  public void Emit_GivenALabelWithASpace_ThenItIsQuotedWhereDefinedAndWhereUsed() {
    var text = LlvmEmitter.Emit(SpacedLabelFunction());
    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("label %\"division by zero.trap6\", label %\"division by zero.ok7\""));
      Assert.That(text, Does.Contain("\n\"division by zero.trap6\":\n  br label %\"division by zero.ok7\"\n"));
      Assert.That(text, Does.Contain("\n\"division by zero.ok7\":\n"));
      Assert.That(text, Does.Contain("%i = phi i16 [ %a, %entry ], [ 1, %\"division by zero.trap6\" ]"));
    });
  }

  [TestCase("x y", "%\"x y\"", TestName = "Emit_GivenAValueNameWithASpace_ThenItIsQuoted")]
  [TestCase("1st", "%\"1st\"", TestName = "Emit_GivenAValueNameStartingWithADigit_ThenItIsQuoted")]
  [TestCase("a\"b\\c", "%\"a\\22b\\5Cc\"", TestName = "Emit_GivenAQuoteOrBackslashInAName_ThenItIsEscaped")]
  [TestCase("$x-y.z_9", "%$x-y.z_9", TestName = "Emit_GivenEveryCharacterLlvmReadsBare_ThenTheNameIsNotQuoted")]
  public void Emit_ValueName_IsSpelledTheWayLlvmReadsIt(string name, string expected) {
    var fn = new IrFunction("named", IrType.I32, [new IrArgument(IrType.I32, 0, "a")]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var v = b.Add(fn.Parameters[0], IrBuilder.ConstI32(1));
    v.Name = name;
    b.Ret(v);

    var text = LlvmEmitter.Emit(fn);
    Assert.Multiple(() => {
      Assert.That(text, Does.Contain($"  {expected} = add i32 %a, 1\n"));
      Assert.That(text, Does.Contain($"  ret i32 {expected}\n"));
    });
  }

  [Test]
  public void Emit_GivenAnEmptyName_ThenTheValueIsNumberedLikeAnUnnamedOne() {
    var fn = new IrFunction("empty", IrType.I32, [new IrArgument(IrType.I32, 0, "a")]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var v = b.Add(fn.Parameters[0], IrBuilder.ConstI32(1));
    v.Name = "";
    b.Ret(v);

    Assert.That(LlvmEmitter.Emit(fn), Does.Contain("  %0 = add i32 %a, 1\n  ret i32 %0\n"));
  }

  [Test]
  public void Emit_GivenABlockAddressOfASpacedLabel_ThenTheAddressNamesTheQuotedLabel() {
    var fn = new IrFunction("handler", IrType.Ptr, []);
    var entry = fn.CreateBlock("entry");
    var target = fn.CreateBlock("on error.handler");
    new IrBuilder(entry).Ret(new IrBlockAddress(target));
    new IrBuilder(target).Ret(new IrNullPtr());

    Assert.That(LlvmEmitter.Emit(fn), Does.Contain("ret ptr blockaddress(@handler, %\"on error.handler\")"));
  }

  [Test]
  public void Emit_GivenABlockAddressIntoAnotherFunction_ThenTheLabelIsTheOneThatFunctionDefines() {
    var owner = new IrFunction("owner", IrType.Void, []);
    var first = owner.CreateBlock("on error");
    var second = owner.CreateBlock("on error");
    new IrBuilder(first).Br(second);
    new IrBuilder(second).Ret();
    var taker = new IrFunction("taker", IrType.Ptr, []);
    new IrBuilder(taker.CreateBlock("on error")).Ret(new IrBlockAddress(second));

    Assert.Multiple(() => {
      Assert.That(LlvmEmitter.Emit(owner), Does.Contain("\n\"on error.1\":\n"));
      Assert.That(LlvmEmitter.Emit(taker), Does.Contain("ret ptr blockaddress(@owner, %\"on error.1\")"));
    });
  }

  [Test]
  public void Emit_GivenABranchToABlockOfNoFunction_ThenRaisesABackendInvariant() {
    var fn = new IrFunction("orphan", IrType.Void, []);
    new IrBuilder(fn.CreateBlock("entry")).Br(new IrBasicBlock("nowhere"));

    Assert.That(() => LlvmEmitter.Emit(fn), Throws.InstanceOf<PowerBasic.Compiler.Backend.BackendInvariantException>());
  }

  [Test]
  public void LlvmAs_AcceptsRepeatedNamesAndSpacedLabels() {
    RequireTool("llvm-as");
    var module = new IrModule("names");
    module.AddFunction(TwoValuesNamedI());
    module.AddFunction(SpacedLabelFunction());
    var ll = LlvmEmitter.Emit(module, "x86_64-unknown-linux-gnu");

    var (code, err) = Run("llvm-as", "-o /dev/null -", ll);

    Assert.That(code, Is.EqualTo(0), $"llvm-as rejected the module:\n{err}\n--- IR ---\n{ll}");
  }

  /// <summary>
  /// The two corpus programs the LLVM emitter audit found rejected: <c>DIFF36.BAS</c> (two values
  /// named <c>%i</c>) and <c>NARROW.BAS</c> (an unquoted <c>division by zero.ok7</c> label).
  /// </summary>
  [TestCase("diff/DIFF36.BAS")]
  [TestCase("diff/NARROW.BAS")]
  public void LlvmAs_AcceptsTheCorpusProgramOnceEmitted(string program) {
    RequireTool("llvm-as");
    var (emitted, ll, why) = EmitCorpusProgram(Path.Combine(_corpus, program));
    Assert.That(emitted, Is.True, why);

    var (code, err) = Run("llvm-as", "-o /dev/null -", ll);

    Assert.That(code, Is.EqualTo(0), $"llvm-as rejected {program}:\n{err}");
  }

  /// <summary>
  /// Every corpus program <c>pbc --emit-llvm</c> renders has to be a module LLVM reads: a count of
  /// exit codes alone is how two naming defects stayed in 31 of the corpus modules.
  /// </summary>
  [Test, Category("Slow")]
  public void LlvmAs_AcceptsEveryCorpusModuleTheEmitterRenders() {
    RequireTool("llvm-as");
    Assume.That(Directory.Exists(_corpus), "no tests/*.BAS corpus present");

    var rejected = new List<string>();
    var rendered = 0;
    foreach (var file in Directory.EnumerateFiles(_corpus, "*.BAS", SearchOption.AllDirectories)
               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
      var (emitted, ll, _) = EmitCorpusProgram(file);
      if (!emitted)
        continue;
      ++rendered;
      var (code, err) = Run("llvm-as", "-o /dev/null -", ll);
      if (code != 0)
        rejected.Add($"{Path.GetRelativePath(_corpus, file).Replace('\\', '/')}: {err.Split('\n')[0]}");
    }

    TestContext.Out.WriteLine($"modules rendered: {rendered}, rejected by llvm-as: {rejected.Count}");
    Assert.Multiple(() => {
      Assert.That(rendered, Is.GreaterThan(50), "the emitter renders almost no corpus program");
      Assert.That(rejected, Is.Empty, "llvm-as rejected:\n  " + string.Join("\n  ", rejected));
    });
  }

  private static readonly string _corpus = Path.GetFullPath(
    Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "tests"));

  private static (bool Emitted, string Module, string Why) EmitCorpusProgram(string path) {
    var stdout = new StringWriter();
    var stderr = new StringWriter();
    var code = PowerBasic.Compiler.Cli.Driver.Run(["--emit-llvm", path], stdout, stderr);
    return (code == 0, stdout.ToString(), stderr.ToString());
  }

  // ---- process helpers -----------------------------------------------------

  private static void RequireTool(string tool) {
    try {
      using var p = Process.Start(new ProcessStartInfo(tool, "--version") {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
      });
      p!.WaitForExit();
    } catch {
      Assert.Ignore($"{tool} not available in this environment");
    }
  }

  private static (int Code, string Err) Run(string tool, string args, string stdin) {
    using var p = Process.Start(new ProcessStartInfo(tool, args) {
      RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
    })!;
    p.StandardInput.Write(stdin);
    p.StandardInput.Close();
    var err = p.StandardError.ReadToEnd();
    p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, err);
  }
}
