using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// The LLVM-style typed SSA IR data model: types, use-lists, operand rewiring and
/// CFG navigation. These pin the in-memory contract the middle-end and backends
/// build on, independent of any lowering from the bound AST.
/// </summary>
[TestFixture]
public sealed class IrModelTests {

  #region types

  [Test]
  public void IrType_GivenSameShape_ThenValueEqualAndCanonical() {
    Assert.That(IrType.Integer(32), Is.EqualTo(IrType.I32));
    Assert.That(IrType.Integer(32), Is.SameAs(IrType.I32));      // common widths are interned
    Assert.That(IrType.I16, Is.Not.EqualTo(IrType.I32));
    Assert.That(IrType.Floating(64), Is.SameAs(IrType.F64));
  }

  [Test]
  public void IrType_ToString_RendersLikeLlvm() {
    Assert.That(IrType.Void.ToString(), Is.EqualTo("void"));
    Assert.That(IrType.I1.ToString(), Is.EqualTo("i1"));
    Assert.That(IrType.I32.ToString(), Is.EqualTo("i32"));
    Assert.That(IrType.F64.ToString(), Is.EqualTo("f64"));
    Assert.That(IrType.Ptr.ToString(), Is.EqualTo("ptr"));
  }

  [Test]
  public void IrType_Predicates_ClassifyCorrectly() {
    Assert.That(IrType.I1.IsBool, Is.True);
    Assert.That(IrType.I32.IsBool, Is.False);
    Assert.That(IrType.I32.IsInteger, Is.True);
    Assert.That(IrType.F32.IsFloat, Is.True);
    Assert.That(IrType.Ptr.IsPointer, Is.True);
  }

  #endregion

  #region use-lists

  [Test]
  public void Binary_WhenCreated_RegistersAsUserOfBothOperands() {
    var a = new IrConstantInt(IrType.I32, 1);
    var b = new IrConstantInt(IrType.I32, 2);

    var add = new IrBinary(IrBinaryOp.Add, a, b);

    Assert.That(add.Type, Is.EqualTo(IrType.I32));
    Assert.That(a.Users, Does.Contain(add));
    Assert.That(b.Users, Does.Contain(add));
    Assert.That(add.Lhs, Is.SameAs(a));
    Assert.That(add.Rhs, Is.SameAs(b));
  }

  [Test]
  public void SetOperand_MovesTheUseFromOldToNew() {
    var a = new IrConstantInt(IrType.I32, 1);
    var b = new IrConstantInt(IrType.I32, 2);
    var c = new IrConstantInt(IrType.I32, 3);
    var add = new IrBinary(IrBinaryOp.Add, a, b);

    add.SetOperand(1, c);

    Assert.That(add.Rhs, Is.SameAs(c));
    Assert.That(b.HasNoUsers, Is.True);
    Assert.That(c.Users, Does.Contain(add));
  }

  [Test]
  public void ReplaceAllUsesWith_RewritesEveryUserAndUpdatesUseLists() {
    var a = new IrConstantInt(IrType.I32, 1);
    var b = new IrConstantInt(IrType.I32, 2);
    var replacement = new IrConstantInt(IrType.I32, 99);
    var add = new IrBinary(IrBinaryOp.Add, a, b);
    var mul = new IrBinary(IrBinaryOp.Mul, a, a);   // a used twice in one user

    a.ReplaceAllUsesWith(replacement);

    Assert.That(add.Lhs, Is.SameAs(replacement));
    Assert.That(mul.Lhs, Is.SameAs(replacement));
    Assert.That(mul.Rhs, Is.SameAs(replacement));
    Assert.That(a.HasNoUsers, Is.True);
    // a use is per operand slot (LLVM-style): add.lhs + mul.lhs + mul.rhs = 3
    Assert.That(replacement.Users.Count, Is.EqualTo(3));
  }

  [Test]
  public void EraseFromParent_RemovesInstructionAndDropsItsUses() {
    var block = new IrBasicBlock("entry");
    var a = new IrConstantInt(IrType.I32, 1);
    var b = new IrConstantInt(IrType.I32, 2);
    var add = block.Append(new IrBinary(IrBinaryOp.Add, a, b));

    add.EraseFromParent();

    Assert.That(block.Instructions, Does.Not.Contain(add));
    Assert.That(a.HasNoUsers, Is.True);
    Assert.That(b.HasNoUsers, Is.True);
    Assert.That(add.Parent, Is.Null);
  }

  #endregion

  #region cfg navigation

  [Test]
  public void Block_TerminatorAndSuccessors_FollowTheBranch() {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var then = fn.CreateBlock("then");
    var done = fn.CreateBlock("done");
    var b = new IrBuilder(entry);
    b.CondBr(IrBuilder.ConstBool(true), then, done);

    Assert.That(entry.Terminator, Is.InstanceOf<IrCondBr>());
    Assert.That(entry.Successors, Is.EquivalentTo(new[] { then, done }));
    Assert.That(then.Terminator, Is.Null);                    // not yet closed
  }

  [Test]
  public void Block_Predecessors_AreDerivedFromSiblingTerminators() {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");
    new IrBuilder(entry).Br(body);
    new IrBuilder(body).CondBr(IrBuilder.ConstBool(true), body, exit);   // self-loop + exit

    Assert.That(body.Predecessors, Is.EquivalentTo(new[] { entry, body }));
    Assert.That(exit.Predecessors, Is.EquivalentTo(new[] { body }));
    Assert.That(entry.Predecessors, Is.Empty);
  }

  [Test]
  public void Phi_IncomingFrom_ReturnsTheValueForThePredecessor() {
    var fn = new IrFunction("f", IrType.I32);
    var a = fn.CreateBlock("a");
    var bb = fn.CreateBlock("b");
    var merge = fn.CreateBlock("merge");
    var phi = new IrPhi(IrType.I32);
    merge.AppendPhi(phi);
    var one = new IrConstantInt(IrType.I32, 1);
    var two = new IrConstantInt(IrType.I32, 2);
    phi.AddIncoming(one, a);
    phi.AddIncoming(two, bb);

    Assert.That(phi.IncomingFrom(a), Is.SameAs(one));
    Assert.That(phi.IncomingFrom(bb), Is.SameAs(two));
    Assert.That(phi.IncomingFrom(merge), Is.Null);
    Assert.That(merge.Phis, Does.Contain(phi));
  }

  #endregion

  #region function assembly

  [Test]
  public void Builder_AssemblesAnAddFunction() {
    // i32 @add(i32 %a, i32 %b) { entry: %r = add %a, %b ; ret %r }
    var a = new IrArgument(IrType.I32, 0, "a");
    var b = new IrArgument(IrType.I32, 1, "b");
    var fn = new IrFunction("add", IrType.I32, [a, b]);
    var entry = fn.CreateBlock("entry");
    var builder = new IrBuilder(entry);

    var sum = builder.Add(a, b);
    builder.Ret(sum);

    Assert.That(fn.IsDeclaration, Is.False);
    Assert.That(fn.Entry, Is.SameAs(entry));
    Assert.That(fn.Parameters.Select(p => p.Index), Is.EqualTo(new[] { 0, 1 }));
    Assert.That(a.Parent, Is.SameAs(fn));
    Assert.That(entry.Instructions.Count, Is.EqualTo(2));
    Assert.That(entry.Terminator, Is.InstanceOf<IrRet>());
    Assert.That(((IrRet)entry.Terminator!).Value, Is.SameAs(sum));
    Assert.That(sum.Users, Does.Contain(entry.Terminator));
  }

  [Test]
  public void Function_WithNoBlocks_IsADeclaration() {
    var fn = new IrFunction("extern", IrType.Void);
    Assert.That(fn.IsDeclaration, Is.True);
    Assert.That(fn.Entry, Is.Null);
    Assert.That(fn.Type, Is.EqualTo(IrType.Ptr));      // a function is an address-typed global value
  }

  [Test]
  public void Module_AddAndFindFunction() {
    var module = new IrModule("TEST");
    var fn = module.AddFunction(new IrFunction("main", IrType.Void));
    Assert.That(module.Functions, Does.Contain(fn));
    Assert.That(module.FindFunction("main"), Is.SameAs(fn));
    Assert.That(module.FindFunction("nope"), Is.Null);
  }

  #endregion
}
