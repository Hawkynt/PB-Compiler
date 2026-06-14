using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// The textual IR printer: deterministic, LLVM-like rendering used for inspection
/// and snapshot tests of the lowering and middle-end.
/// </summary>
[TestFixture]
public sealed class IrPrinterTests {

  [Test]
  public void Print_GivenNamedAddFunction_RendersLlvmLike() {
    var a = new IrArgument(IrType.I32, 0, "a");
    var b = new IrArgument(IrType.I32, 1, "b");
    var fn = new IrFunction("add", IrType.I32, [a, b]);
    var entry = fn.CreateBlock("entry");
    var builder = new IrBuilder(entry);
    var sum = builder.Add(a, b);
    sum.Name = "r";
    builder.Ret(sum);

    var text = IrPrinter.Print(fn);

    Assert.That(text, Is.EqualTo(
      "define i32 @add(i32 %a, i32 %b) {\n" +
      "entry:\n" +
      "  %r = add i32 %a, %b\n" +
      "  ret i32 %r\n" +
      "}\n"));
  }

  [Test]
  public void Print_GivenBranchesAndPhi_NumbersUnnamedValuesDeterministically() {
    var n = new IrArgument(IrType.I32, 0, "n");
    var fn = new IrFunction("f", IrType.I32, [n]);
    var entry = fn.CreateBlock("entry");
    var pos = fn.CreateBlock("pos");
    var neg = fn.CreateBlock("neg");
    var done = fn.CreateBlock("done");

    var be = new IrBuilder(entry);
    var cond = be.Cmp(IrCmpPred.Sgt, n, IrBuilder.ConstI32(0));   // unnamed -> %0
    be.CondBr(cond, pos, neg);
    new IrBuilder(pos).Br(done);
    new IrBuilder(neg).Br(done);

    var bd = new IrBuilder(done);
    var phi = bd.Phi(IrType.I32);                                  // unnamed -> %1
    phi.AddIncoming(IrBuilder.ConstI32(1), pos);
    phi.AddIncoming(IrBuilder.ConstI32(-1), neg);
    bd.Ret(phi);

    var text = IrPrinter.Print(fn);

    Assert.That(text, Is.EqualTo(
      "define i32 @f(i32 %n) {\n" +
      "entry:\n" +
      "  %0 = icmp sgt i32 %n, 0\n" +
      "  br i1 %0, label %pos, label %neg\n" +
      "pos:\n" +
      "  br label %done\n" +
      "neg:\n" +
      "  br label %done\n" +
      "done:\n" +
      "  %1 = phi i32 [ 1, %pos ], [ -1, %neg ]\n" +
      "  ret i32 %1\n" +
      "}\n"));
  }

  [Test]
  public void Print_GivenMemoryAndCast_RendersLoadStoreAllocaCast() {
    var fn = new IrFunction("mem", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    var slot = b.Alloca(IrType.I16);
    slot.Name = "p";
    b.Store(IrBuilder.ConstInt(IrType.I16, 7), slot);
    var loaded = b.Load(IrType.I16, slot);
    loaded.Name = "v";
    var wide = b.SExt(loaded, IrType.I32);
    wide.Name = "w";
    b.Ret();

    var text = IrPrinter.Print(fn);

    Assert.That(text, Is.EqualTo(
      "define void @mem() {\n" +
      "entry:\n" +
      "  %p = alloca i16\n" +
      "  store i16 7, ptr %p\n" +
      "  %v = load i16, ptr %p\n" +
      "  %w = sext i16 %v to i32\n" +
      "  ret void\n" +
      "}\n"));
  }

  [Test]
  public void Print_GivenDeclarationAndGlobal_RendersHeaderForms() {
    var module = new IrModule("TEST");
    module.AddGlobal(new IrGlobalVariable("counter", IrType.I32));
    module.AddFunction(new IrFunction("ext", IrType.I32, [new IrArgument(IrType.I32, 0, "x")]));

    var text = IrPrinter.Print(module);

    Assert.That(text, Is.EqualTo(
      "@counter = global i32 zeroinitializer\n" +
      "\n" +
      "declare i32 @ext(i32 %x)\n"));
  }
}
