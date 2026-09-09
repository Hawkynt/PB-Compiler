using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Regression tests for O0287's bounded dynamic-string stack promotion.</summary>
[TestFixture]
public sealed class O0287StackPromotionTests {

  [Test]
  public void Run_GivenChrPlusLiteralPrinted_WhenBoundedAndNonEscaping_ThenTheStringHeapDisappears() {
    var module = new IrModule("test");
    var chr = Declare(module, "rt_str_chr", IrType.Ptr, IrType.I32);
    var append = Declare(module, "rt_str_append_lit", IrType.Ptr, IrType.Ptr, IrType.Ptr, IrType.I32);
    var print = Declare(module, "rt_print_strvar", IrType.Void, IrType.Ptr);
    var suffix = module.AddStringConstant([(byte)':']);
    var code = new IrArgument(IrType.I32, 0, "code");
    var function = module.AddFunction(new IrFunction("f", IrType.Void, [code]));
    var entry = function.CreateBlock("entry");
    var builder = new IrBuilder(entry);
    var first = builder.Call(IrType.Ptr, chr, code);
    var text = builder.Call(IrType.Ptr, append, first, suffix, IrBuilder.ConstI32(1));
    builder.Call(IrType.Void, print, text);
    builder.Ret();

    var changed = StringStackPromotion.Run(module);

    Assert.That(changed, Is.EqualTo(1));
    var frame = entry.Instructions.OfType<IrAlloca>().Single();
    Assert.Multiple(() => {
      Assert.That(frame.Allocated, Is.EqualTo(IrType.I8));
      Assert.That(frame.Count, Is.EqualTo(2));
      Assert.That(Callees(function), Does.Not.Contain("rt_str_chr"));
      Assert.That(Callees(function), Does.Not.Contain("rt_str_append_lit"));
      Assert.That(Callees(function), Does.Not.Contain("rt_print_strvar"));
      Assert.That(Callees(function), Does.Contain("rt_print_str"));
      Assert.That(Callees(function).Count(name => name == "llvm.memcpy.p0.p0.i32"), Is.EqualTo(2),
        "literal -> SS frame, then SS frame -> DS raw-print staging buffer");
      Assert.That(module.FindGlobal(".o0287.printbuf")?.Count, Is.EqualTo(StringStackPromotion.MaxStackBytes));
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenTheHandleHasASecondUser_WhenRun_ThenItDeclinesRatherThanChangingRepresentation() {
    var module = new IrModule("test");
    var chr = Declare(module, "rt_str_chr", IrType.Ptr, IrType.I32);
    var length = Declare(module, "rt_str_len", IrType.I32, IrType.Ptr);
    var print = Declare(module, "rt_print_strvar", IrType.Void, IrType.Ptr);
    var code = new IrArgument(IrType.I32, 0, "code");
    var function = module.AddFunction(new IrFunction("f", IrType.I32, [code]));
    var entry = function.CreateBlock("entry");
    var builder = new IrBuilder(entry);
    var text = builder.Call(IrType.Ptr, chr, code);
    var observedLength = builder.Call(IrType.I32, length, text);
    builder.Call(IrType.Void, print, text);
    builder.Ret(observedLength);

    Assert.That(StringStackPromotion.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(Callees(function), Does.Contain("rt_str_chr"));
      Assert.That(Callees(function), Does.Contain("rt_print_strvar"));
      Assert.That(entry.Instructions.OfType<IrAlloca>(), Is.Empty);
      Assert.That(module.FindGlobal(".o0287.printbuf"), Is.Null);
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenAProducerTreeLargerThanTheStackCap_WhenRun_ThenItStaysOnTheStringHeap() {
    var module = new IrModule("test");
    var constant = Declare(module, "rt_str_const", IrType.Ptr, IrType.Ptr, IrType.I32);
    var chr = Declare(module, "rt_str_chr", IrType.Ptr, IrType.I32);
    var concat = Declare(module, "rt_str_concat", IrType.Ptr, IrType.Ptr, IrType.Ptr);
    var print = Declare(module, "rt_print_strvar", IrType.Void, IrType.Ptr);
    var literal = module.AddStringConstant(Enumerable.Repeat((byte)'x', StringStackPromotion.MaxStackBytes).ToArray());
    var function = module.AddFunction(new IrFunction("f", IrType.Void));
    var entry = function.CreateBlock("entry");
    var builder = new IrBuilder(entry);
    var left = builder.Call(IrType.Ptr, constant, literal, IrBuilder.ConstI32(StringStackPromotion.MaxStackBytes));
    var right = builder.Call(IrType.Ptr, chr, IrBuilder.ConstI32('!'));
    var text = builder.Call(IrType.Ptr, concat, left, right);
    builder.Call(IrType.Void, print, text);
    builder.Ret();

    Assert.That(StringStackPromotion.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(Callees(function), Does.Contain("rt_str_concat"));
      Assert.That(entry.Instructions.OfType<IrAlloca>(), Is.Empty);
      Assert.That(module.FindGlobal(".o0287.printbuf"), Is.Null);
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenAFilePrint_WhenPromoted_ThenTheFileNumberIsPreservedOnTheRawPrint() {
    var module = new IrModule("test");
    var chr = Declare(module, "rt_str_chr", IrType.Ptr, IrType.I32);
    var print = Declare(module, "rt_fprint_strvar", IrType.Void, IrType.I32, IrType.Ptr);
    var file = new IrArgument(IrType.I32, 0, "file");
    var code = new IrArgument(IrType.I32, 1, "code");
    var function = module.AddFunction(new IrFunction("f", IrType.Void, [file, code]));
    var entry = function.CreateBlock("entry");
    var builder = new IrBuilder(entry);
    var text = builder.Call(IrType.Ptr, chr, code);
    builder.Call(IrType.Void, print, file, text);
    builder.Ret();

    Assert.That(StringStackPromotion.Run(module), Is.EqualTo(1));
    var raw = function.AllInstructions.OfType<IrCall>()
      .Single(call => call.Callee is IrFunction { Name: "rt_fprint_str" });
    Assert.Multiple(() => {
      Assert.That(raw.GetOperand(1), Is.SameAs(file));
      Assert.That(raw.GetOperand(2), Is.SameAs(module.FindGlobal(".o0287.printbuf")));
      Assert.That(((IrConstantInt)raw.GetOperand(3)).Value, Is.EqualTo(1));
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  private static IReadOnlyList<string> Callees(IrFunction function)
    => function.AllInstructions.OfType<IrCall>()
      .Select(call => (call.Callee as IrFunction)?.Name ?? string.Empty)
      .ToList();

  private static IrFunction Declare(IrModule module, string name, IrType result, params IrType[] parameters)
    => module.AddFunction(new IrFunction(name, result,
      parameters.Select((type, index) => new IrArgument(type, index))));
}
