using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Signed 16/32-bit division and remainder on the x86-16 back end. <c>IDIV</c> is the first selected
/// instruction that is <b>fixed to physical registers</b>: it divides <c>DX:AX</c> and answers with the
/// quotient in AX and the remainder in DX. Nothing else in the machine IR pins a register that the
/// operands do not name, so the two mechanisms that have to carry it are exercised here -
/// <see cref="MInstr.Clobbers"/> keeping the allocator's values out of AX/DX, and the scheduler
/// treating a clobber as a write so the divide cannot drift past the MOV that reads its result out.
///
/// The inline 16-bit form selects only a non-zero compile-time constant divisor. The 32-bit form uses
/// the direct emitter's long-runtime helper, which accepts a runtime divisor and raises PowerBASIC
/// Error 11 on zero.
/// </summary>
[TestFixture]
public sealed class BackendDivisionTests {

  private static SemanticModel Bind(string source) {
    var syntax = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(syntax, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static IrFunction Divide(IrBinaryOp op, long divisor) {
    var fn = new IrFunction("D", IrType.I16, [new IrArgument(IrType.I16, 0)]);
    var entry = fn.CreateBlock("entry");
    var value = entry.Append(new IrBinary(op, fn.Parameters[0], new IrConstantInt(IrType.I16, divisor)));
    entry.Append(new IrRet(value));
    return fn;
  }

  private static IrFunction WideDivide(IrBinaryOp op) {
    var fn = new IrFunction("D", IrType.I32,
      [new IrArgument(IrType.I32, 0), new IrArgument(IrType.I32, 1)]);
    var entry = fn.CreateBlock("entry");
    var value = entry.Append(new IrBinary(op, fn.Parameters[0], fn.Parameters[1]));
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

  [TestCase(IrBinaryOp.SDiv, "rt_ldiv")]
  [TestCase(IrBinaryOp.SRem, "rt_lmod")]
  public void Select_GivenSigned32BitDivideOrRemainder_ThenCallsTheMatchingRuntimeHelper(
      IrBinaryOp op, string label) {
    var m = InstructionSelector.TrySelect(WideDivide(op), out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    var call = m!.AllInstructions.Single(i => i.Opcode == MOpcode.Call);
    Assert.That(call.Operands, Is.EqualTo(new[] { new MOperand.LabelRef(label) }));
    Assert.That(call.Clobbers, Is.EquivalentTo(new[] { Reg.AX, Reg.BX, Reg.CX, Reg.DX, Reg.SI, Reg.DI }),
      "the helper owns the caller-saved file while DX:AX and CX:BX carry its arguments");
  }

  [TestCase(IrBinaryOp.SDiv)]
  [TestCase(IrBinaryOp.SRem)]
  public void Allocate_GivenSigned32BitDivideOrRemainder_ThenThePinnedAbiSequenceSurvives(IrBinaryOp op) {
    var m = InstructionSelector.TrySelect(WideDivide(op), out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    MachineScheduler.Schedule(m!);
    Assert.That(LinearScanAllocator.Allocate(m!), Is.Not.Null,
      "the four argument registers and two result registers must not strand a live virtual");
  }

  [Test]
  public void Execute_GivenSigned32BitDivideAndRemainder_ThenRoutedAndDirectResultsMatchAtBoundaries() {
    const string source = """
      FUNCTION Quot&(BYVAL n&, BYVAL d&)
        Quot& = n& \ d&
      END FUNCTION

      FUNCTION Remain&(BYVAL n&, BYVAL d&)
        Remain& = n& MOD d&
      END FUNCTION

      a& = 100000007
      b& = -7
      PRINT Quot&(a&, b&); Remain&(a&, b&)
      PRINT Quot&(-a&, 7); Remain&(-a&, 7)
      m& = -2147483647 - 1
      PRINT Quot&(m&, -1); Remain&(m&, -1)
      PRINT Quot&(m&, 3); Remain&(m&, 3)
      """;
    var direct = new CodeGenerator(Bind(source)) { Optimize = false, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = false, UseExperimentalBackend = true };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("Quot"));
    Assert.That(routed.BackendRoutedNames, Does.Contain("Remain"));
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
    Assert.That(routedCpu.Output.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
      Is.EqualTo(new[] {
        "-14285715", "2", "-14285715", "-2", "-2147483648", "0", "-715827882", "-2",
      }), "division truncates toward zero, remainder follows the dividend, and MINLONG / -1 wraps");
  }

  [Test]
  public void Execute_GivenAZeroRuntimeDivisor_ThenTheRoutedPathRaisesPowerBasicError11() {
    const string source = """
      ON ERROR GOTO trapped
      n& = 7
      d& = 0
      q& = n& \ d&
      PRINT "missed"
      END
      trapped:
      PRINT ERR
      END
      """;
    var direct = new CodeGenerator(Bind(source)) { Optimize = false, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = false, UseExperimentalBackend = true };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"), "the error path must not be a fallback");
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
    Assert.That(routedCpu.Output.Trim(), Is.EqualTo("11"));
  }

  [Test]
  public void Emit_GivenAProgramWithAConstantDivide_ThenTheBackEndCompilesIt() {
    const string source = """
      FUNCTION Tenth%(BYVAL v%)
        Tenth% = v% \ 10
      END FUNCTION

      PRINT Tenth%(250)
      """;

    var model = Bind(source);
    var routed = new CodeGenerator(model) { Optimize = false, UseExperimentalBackend = true };

    var image = routed.EmitExecutable();

    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(image, Is.Not.Empty);
    Assert.That(routed.BackendRoutedNames, Does.Contain("Tenth"), "the back end did not take the dividing function");
  }
}
