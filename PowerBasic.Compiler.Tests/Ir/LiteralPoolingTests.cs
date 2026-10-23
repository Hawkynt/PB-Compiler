using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class LiteralPoolingTests {

  private const string _SOURCE = """
    PRINT "Hallo World"
    PRINT "Test"
    PRINT "Hallo"
    PRINT "World"
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35),
      "T.BAS", Dialect.Pb35), Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty);
    return model;
  }

  private static IrModule Compile(string source, IrBackendTarget target, bool optimize) {
    var compiled = IrBackendModule.TryCompile(Bind(source), new() {
      Target = target,
      Optimize = optimize,
    }, out var why);
    Assert.That(compiled, Is.Not.Null, why);
    Assert.That(IrVerifier.Verify(compiled!.Module), Is.Empty);
    return compiled.Module;
  }

  private static string Run(string source, bool optimize) => Run(source, optimize, false);

  private static string Run(string source, bool optimize, bool com) {
    var generator = new CodeGenerator(Bind(source)) { Optimize = optimize };
    var image = com ? generator.EmitCom() : generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty);
    Assert.That(generator.BackendRoutedNames, Does.Contain("main"));
    return Cpu8086.Run(image).Output;
  }

  private static (IrModule Module, IrGlobalVariable Host, IrGlobalVariable Slice, IrBasicBlock Entry)
      CreateReads(byte[] bytes, byte[] slice) {
    var module = new IrModule("pool");
    var host = module.AddStringConstant(bytes);
    var small = module.AddStringConstant(slice);
    var print = module.AddFunction(new IrFunction("rt_print_str", IrType.Void,
      [new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I32, 1)]));
    var entry = module.AddFunction(new IrFunction("main", IrType.Void)).AddBlock(new IrBasicBlock("entry"));
    entry.Append(new IrCall(IrType.Void, print, [host, new IrConstantInt(IrType.I32, bytes.Length)]));
    entry.Append(new IrCall(IrType.Void, print, [small, new IrConstantInt(IrType.I32, slice.Length)]));
    entry.Append(new IrRet());
    return (module, host, small, entry);
  }

  [TestCase(IrBackendTarget.C)]
  [TestCase(IrBackendTarget.Llvm)]
  [TestCase(IrBackendTarget.PowerBasic35)]
  [TestCase(IrBackendTarget.X86_16)]
  [TestCase(IrBackendTarget.X86_32)]
  [TestCase(IrBackendTarget.X64)]
  [TestCase(IrBackendTarget.Mos6502)]
  public void Compile_GivenContainedLiterals_WhenOptimized_ThenOnlyHostBytesRemain(IrBackendTarget target) {
    var module = Compile(_SOURCE, target, true);
    Assert.That(module.Globals.Where(g => g.Bytes is not null).Sum(g => g.Bytes!.Length), Is.EqualTo(15));
    Assert.That(module.Functions.SelectMany(f => f.AllInstructions).OfType<IrGep>()
      .Select(g => ((IrConstantInt)g.ByteOffset).Value), Does.Contain(6));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Compile_GivenContainedLiterals_WhenRenderedAsBasic_ThenRoundTripPreservesText(bool optimize) {
    var rendered = IrBasicWriter.Write(Compile(_SOURCE, IrBackendTarget.PowerBasic35, optimize));
    Assert.That(Run(rendered, optimize), Is.EqualTo("Hallo World\r\nTest\r\nHallo\r\nWorld\r\n"));
  }

  [Test]
  public void Compile_GivenContainedLiterals_WhenOptimizationDisabled_ThenSeparateBytesRemain() {
    var module = Compile(_SOURCE, IrBackendTarget.C, false);
    Assert.That(module.Globals.Where(g => g.Bytes is not null).Sum(g => g.Bytes!.Length), Is.EqualTo(25));
  }

  [TestCase(false, false)]
  [TestCase(true, false)]
  [TestCase(false, true)]
  [TestCase(true, true)]
  public void Run_GivenContainedLiterals_WhenNative_ThenPrintsEveryOriginalSlice(bool optimize, bool com) =>
    Assert.That(Run(_SOURCE, optimize, com), Is.EqualTo("Hallo World\r\nTest\r\nHallo\r\nWorld\r\n"));

  [Test]
  public void Run_GivenOpaqueAssembly_ThenDoesNotChangeLiteralLayout() {
    var (module, _, _, entry) = CreateReads([1, 2, 3], [2, 3]);
    entry.Parent!.HasInlineAsm = true;
    Assert.That(LiteralPooling.Run(module), Is.Zero);
  }

  [TestCase("rt_str_const")]
  [TestCase("rt_fprint_str")]
  [TestCase("rt_str_append_lit")]
  public void Run_GivenAReadOnlyStringConsumer_ThenPreservesItsExplicitSliceLength(string name) {
    var (module, host, _, entry) = CreateReads([65, 66, 67, 68], [67, 68]);
    var print = entry.Instructions.OfType<IrCall>().Last();
    var pointer = print.GetOperand(1);
    var length = new IrConstantInt(IrType.I32, 1);
    IrValue[] arguments = name switch {
      "rt_fprint_str" => [new IrConstantInt(IrType.I16, 1), pointer, length],
      "rt_str_append_lit" => [new IrNullPtr(), pointer, length],
      _ => [pointer, length],
    };
    var resultType = name == "rt_fprint_str" ? IrType.Void : IrType.Ptr;
    var callee = module.AddFunction(new IrFunction(name, resultType,
      arguments.Select((arg, index) => new IrArgument(arg.Type, index))));
    var read = entry.InsertBefore(new IrCall(resultType, callee, arguments), print);
    if (resultType == IrType.Ptr) {
      var printHandle = module.AddFunction(new IrFunction("rt_print_strvar", IrType.Void,
        [new IrArgument(IrType.Ptr, 0)]));
      entry.InsertBefore(new IrCall(IrType.Void, printHandle, [read]), print);
    }
    print.EraseFromParent();
    Assert.That(LiteralPooling.Run(module), Is.EqualTo(1));
    Assert.That(module.Globals, Is.EqualTo(new[] { host }));
    var rendered = IrBasicWriter.Write(module);
    Assert.That(rendered, Does.Contain("\"C\"").And.Not.Contain("\"CD\""));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(read.Args.Last(), Is.SameAs(length));
  }

  [Test]
  public void Run_GivenBinaryLiteralBytes_ThenPoolsWithoutTextEncodingOrTerminators() {
    var (module, host, slice, entry) = CreateReads([65, 0, 255, 66], [0, 255]);
    Assert.That(LiteralPooling.Run(module), Is.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(module.Globals, Is.EqualTo(new[] { host }));
      Assert.That(((IrGep)entry.Instructions.OfType<IrCall>().Last().GetOperand(1)).ByteOffset,
        Is.InstanceOf<IrConstantInt>());
      Assert.That(IrVerifier.Verify(module), Is.Empty);
      Assert.That(LiteralPooling.Run(module), Is.Zero, "a second run must not rewrite existing slices");
    });
    var restored = module.AddStringConstant([0, 255]);
    Assert.That(restored, Is.Not.SameAs(slice), "interning must not return the removed global");
    Assert.That(module.Globals, Does.Contain(restored));
  }

  [TestCase("comparison")]
  [TestCase("store")]
  [TestCase("escape")]
  [TestCase("defined-callee")]
  [TestCase("oversized")]
  [TestCase("negative")]
  [TestCase("dynamic")]
  public void Run_GivenAnUnprovedLiteralUse_ThenKeepsDistinctStorage(string use) {
    var (module, host, slice, entry) = CreateReads([1, 2, 3], [2, 3]);
    var print = entry.Instructions.OfType<IrCall>().Last();
    switch (use) {
      case "comparison": entry.InsertBefore(new IrCmp(IrCmpPred.Eq, host, slice), print); break;
      case "store": entry.InsertBefore(new IrStore(new IrConstantInt(IrType.I8, 9), host), print); break;
      case "escape":
        var unknown = module.AddFunction(new IrFunction("unknown", IrType.Void, [new IrArgument(IrType.Ptr, 0)]));
        entry.InsertBefore(new IrCall(IrType.Void, unknown, [slice]), print);
        break;
      case "defined-callee":
        ((IrFunction)print.Callee).AddBlock(new IrBasicBlock("entry")).Append(new IrRet());
        break;
      case "oversized": print.SetOperand(2, new IrConstantInt(IrType.I32, 3)); break;
      case "negative": print.SetOperand(2, new IrConstantInt(IrType.I32, -1)); break;
      case "dynamic":
        var count = module.AddGlobal(new IrGlobalVariable("count", IrType.I32));
        print.SetOperand(2, entry.InsertBefore(new IrLoad(IrType.I32, count), print));
        break;
    }
    Assert.That(LiteralPooling.Run(module), Is.Zero);
    Assert.That(module.Globals, Does.Contain(host).And.Contain(slice));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void Run_GivenAnEmptyOrMerelyOverlappingLiteral_ThenDoesNotPoolIt() {
    var (module, _, _, _) = CreateReads([1, 2, 3], [3, 4]);
    module.AddStringConstant([]);
    Assert.That(LiteralPooling.Run(module), Is.Zero);
  }

  [Test]
  public void Run_GivenNull_ThenRejectsThePublicArgument() =>
    Assert.Throws<ArgumentNullException>(() => LiteralPooling.Run(null!));
}
