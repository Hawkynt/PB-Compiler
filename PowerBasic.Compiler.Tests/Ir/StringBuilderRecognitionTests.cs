using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class StringBuilderRecognitionTests {

  [Test]
  public void Run_GivenLoopCarriedVariableAndLiteralConcatChain_ThenItBecomesBorrowingAppendsBeforeO0024() {
    var (module, function, body, text, _) = BuildBuilder(extraAccumulatorRead: false);

    var changed = StringConcatChain.Run(module);

    var calls = body.Instructions.OfType<IrCall>().ToList();
    var names = calls.Select(call => ((IrFunction)call.Callee).Name).ToList();
    Assert.Multiple(() => {
      Assert.That(changed, Is.EqualTo(1));
      Assert.That(names, Is.EqualTo(new[] { "rt_str_append_var", "rt_str_append_lit" }));
      Assert.That(names, Does.Not.Contain("rt_str_concat_n"),
        "O0024 must not rebuild the growing prefix once per iteration");
      Assert.That(names, Does.Not.Contain("rt_str_dup"),
        "borrowing suffixes must not allocate blocks above the accumulator");
      Assert.That(names, Does.Not.Contain("rt_str_free"),
        "the first append consumes the old accumulator in place of the assignment free");
      Assert.That(text.IncomingFrom(body), Is.SameAs(calls[^1]));
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenAnotherAccumulatorReadInsideTheLoop_ThenTheBuilderIsNotMutatedEarly() {
    var (module, function, _, _, root) = BuildBuilder(extraAccumulatorRead: true);

    var changed = StringBuilderRecognition.Run(module);

    Assert.Multiple(() => {
      Assert.That(changed, Is.Zero);
      Assert.That(root.Parent, Is.Not.Null,
        "an in-loop reader can observe the old value, so the concat chain must remain intact");
      Assert.That(function.AllInstructions.OfType<IrCall>()
        .Select(call => ((IrFunction)call.Callee).Name), Does.Not.Contain("rt_str_append_var"));
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  private static (IrModule Module, IrFunction Function, IrBasicBlock Body, IrPhi Text, IrCall Root)
      BuildBuilder(bool extraAccumulatorRead) {
    var module = new IrModule("test");
    var concat = module.AddFunction(new IrFunction("rt_str_concat", IrType.Ptr, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.Ptr, 1),
    ]));
    var duplicate = module.AddFunction(new IrFunction("rt_str_dup", IrType.Ptr,
      [new IrArgument(IrType.Ptr, 0)]));
    var free = module.AddFunction(new IrFunction("rt_str_free", IrType.Void,
      [new IrArgument(IrType.Ptr, 0)]));
    var constant = module.AddFunction(new IrFunction("rt_str_const", IrType.Ptr, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I32, 1),
    ]));
    var length = module.AddFunction(new IrFunction("rt_str_len", IrType.I32,
      [new IrArgument(IrType.Ptr, 0)]));
    var comma = module.AddStringConstant([(byte)',']);

    var keepGoing = new IrArgument(IrType.I1, 0, "keepGoing");
    var part = new IrArgument(IrType.Ptr, 1, "part");
    var function = module.AddFunction(new IrFunction("f", IrType.Ptr, [keepGoing, part]));
    var preheader = function.CreateBlock("preheader");
    var header = function.CreateBlock("header");
    var body = function.CreateBlock("body");
    var exit = function.CreateBlock("exit");
    new IrBuilder(preheader).Br(header);

    var bh = new IrBuilder(header);
    var text = bh.Phi(IrType.Ptr);
    bh.CondBr(keepGoing, body, exit);

    var bb = new IrBuilder(body);
    var accumulatorBorrow = bb.Call(IrType.Ptr, duplicate, text);
    if (extraAccumulatorRead) {
      var observed = bb.Call(IrType.Ptr, duplicate, text);
      bb.Call(IrType.I32, length, observed);
    }
    var partBorrow = bb.Call(IrType.Ptr, duplicate, part);
    var first = bb.Call(IrType.Ptr, concat, accumulatorBorrow, partBorrow);
    var literal = bb.Call(IrType.Ptr, constant, comma, IrBuilder.ConstI32(1));
    var root = bb.Call(IrType.Ptr, concat, first, literal);
    bb.Call(IrType.Void, free, text);
    bb.Br(header);

    text.AddIncoming(new IrNullPtr(), preheader);
    text.AddIncoming(root, body);
    new IrBuilder(exit).Ret(text);
    return (module, function, body, text, root);
  }
}
