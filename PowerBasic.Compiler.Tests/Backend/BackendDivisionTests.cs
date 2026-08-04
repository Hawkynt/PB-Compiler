using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Signed 16-bit division and remainder on the x86-16 back end. <c>IDIV</c> is the first selected
/// instruction that is <b>fixed to physical registers</b>: it divides <c>DX:AX</c> and answers with the
/// quotient in AX and the remainder in DX. Nothing else in the machine IR pins a register that the
/// operands do not name, so the two mechanisms that have to carry it are exercised here -
/// <see cref="MInstr.Clobbers"/> keeping the allocator's values out of AX/DX, and the scheduler
/// treating a clobber as a write so the divide cannot drift past the MOV that reads its result out.
///
/// Only a non-zero compile-time constant divisor is selected. PowerBASIC raises Error 11 on a zero
/// divisor; a constant that is not zero cannot trap, which is exactly the case where the direct
/// emitter also drops the guard (O0220). A runtime divisor keeps declining until the runtime-label
/// bridge exists to raise the error.
/// </summary>
[TestFixture]
public sealed class BackendDivisionTests {

  private static IrFunction Divide(IrBinaryOp op, long divisor) {
    var fn = new IrFunction("D", IrType.I16, [new IrArgument(IrType.I16, 0)]);
    var entry = fn.CreateBlock("entry");
    var value = entry.Append(new IrBinary(op, fn.Parameters[0], new IrConstantInt(IrType.I16, divisor)));
    entry.Append(new IrRet(value));
    return fn;
  }

  [Test]
  public void Select_GivenSignedDivideByAConstant_ThenEmitsTheCwdIdivSequence() {
    var m = InstructionSelector.TrySelect(Divide(IrBinaryOp.SDiv, 10), out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    var opcodes = m!.AllInstructions.Select(i => i.Opcode).ToList();
    Assert.That(opcodes, Does.Contain(MOpcode.Cwd), "the dividend is sign-extended into DX:AX");
    Assert.That(opcodes, Does.Contain(MOpcode.Idiv));
    Assert.That(opcodes.IndexOf(MOpcode.Cwd), Is.LessThan(opcodes.IndexOf(MOpcode.Idiv)),
      "CWD builds the dividend the IDIV consumes");
  }

  [Test]
  public void Select_GivenSignedDivide_ThenTheDivisorIsARegisterAndTheDivideClobbersDxAx() {
    var m = InstructionSelector.TrySelect(Divide(IrBinaryOp.SDiv, 10), out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    var idiv = m!.AllInstructions.First(i => i.Opcode == MOpcode.Idiv);
    Assert.That(idiv.Operands[0], Is.InstanceOf<MOperand.Register>(), "IDIV has no immediate form");
    Assert.That(idiv.Clobbers, Is.EquivalentTo(new[] { Reg.AX, Reg.DX }),
      "the quotient and remainder overwrite the pair, so nothing live may sit there");
  }

  [Test]
  public void Select_GivenRemainder_ThenTakesTheResultFromDxRatherThanAx() {
    var quotient = InstructionSelector.TrySelect(Divide(IrBinaryOp.SDiv, 10));
    var remainder = InstructionSelector.TrySelect(Divide(IrBinaryOp.SRem, 10));

    Assert.That(quotient, Is.Not.Null);
    Assert.That(remainder, Is.Not.Null);
    Assert.That(ResultRegister(remainder!), Is.EqualTo(Reg.DX), "MOD reads the remainder out of DX");
    Assert.That(ResultRegister(quotient!), Is.EqualTo(Reg.AX), "the quotient comes back in AX");
  }

  // the physical register the MOV right after the IDIV copies the result out of
  private static Reg ResultRegister(MFunction m) {
    var instrs = m.AllInstructions.ToList();
    var after = instrs.SkipWhile(i => i.Opcode != MOpcode.Idiv).Skip(1);
    return after.Select(i => i.Operands.Count > 1 ? i.Operands[1] : null)
      .OfType<MOperand.Register>()
      .First(r => !r.Reg.IsVirtual)
      .Reg.Physical;
  }

  [TestCase(0L, TestName = "a zero divisor is Error 11, not a divide")]
  [TestCase(-1L, TestName = "MININT / -1 overflows IDIV (Error 6)")]
  public void Select_GivenADivisorThatWouldTrap_ThenDeclines(long divisor) {
    Assert.That(InstructionSelector.TrySelect(Divide(IrBinaryOp.SDiv, divisor)), Is.Null);
  }

  [Test]
  public void Schedule_GivenTheDivideSequence_ThenKeepsItInOrder() {
    var m = InstructionSelector.TrySelect(Divide(IrBinaryOp.SDiv, 10));
    Assert.That(m, Is.Not.Null);

    MachineScheduler.Schedule(m!);

    // the whole point of declaring the clobbers: the list scheduler sees writes to AX/DX where the
    // operands name none, so it cannot hoist the result-reading MOV above the divide
    var opcodes = m!.AllInstructions.Select(i => i.Opcode).ToList();
    var cwd = opcodes.IndexOf(MOpcode.Cwd);
    var idiv = opcodes.IndexOf(MOpcode.Idiv);
    Assert.That(cwd, Is.LessThan(idiv));
    var readsResult = m.AllInstructions
      .Select((instr, at) => (instr, at))
      .Where(p => p.instr.Opcode == MOpcode.Mov && p.instr.Operands[1] is MOperand.Register { Reg.IsVirtual: false })
      .Select(p => p.at);
    Assert.That(readsResult, Is.All.GreaterThan(idiv), "the result is read after the divide produced it");
  }

  [Test]
  public void Emit_GivenAProgramWithAConstantDivide_ThenTheBackEndCompilesIt() {
    const string source = """
      FUNCTION Tenth%(BYVAL v%)
        Tenth% = v% \ 10
      END FUNCTION

      PRINT Tenth%(250)
      """;

    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var routed = new CodeGenerator(model) { Optimize = false, UseExperimentalBackend = true };

    var image = routed.EmitExecutable();

    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(image, Is.Not.Empty);
    Assert.That(routed.BackendRoutedNames, Does.Contain("Tenth"), "the back end did not take the dividing function");
  }
}
