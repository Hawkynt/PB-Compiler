using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// A GEP at a CONSTANT displacement is an addressing mode, not an address. Selecting it as an
/// <c>LEA</c> into a register of its own and then dereferencing that register spends one BASE
/// register per field, where folding the displacement into the access leaves every field of an
/// object sharing the one base the object already needs.
///
/// <para>
/// This is a register-allocation fixture rather than a code-shape one. A value used as a memory base
/// is the single thing the spiller cannot relocate, so exhausting bases does not degrade - it fails,
/// as <c>allocation: no register assignment, and nothing left that can move to memory</c>. That is
/// how it was found: a routed array-parameter body reading both bounds and doing one
/// read-modify-write declined, while the IDENTICAL body over a shared dynamic array - whose
/// descriptor fields are absolute data cells and so need no base at all - allocated fine.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendGepAddressingTests {

  private static MFunction Select(string source, string procedure) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);
    var fn = module!.Functions.First(f => f.Name.Equals(procedure, StringComparison.OrdinalIgnoreCase));
    var machine = InstructionSelector.TrySelect(fn, out var reason);
    Assert.That(machine, Is.Not.Null, $"{procedure} declined: {reason}");
    return machine!;
  }

  /// <summary>The distinct registers used as a memory BASE - the ones the spiller cannot relocate.</summary>
  private static int DistinctBases(MFunction fn) => fn.AllInstructions
    .SelectMany(instruction => instruction.Operands)
    .OfType<MOperand.Memory>()
    .Where(memory => memory.Base is not null)
    .Select(memory => memory.Base!.Value)
    .Distinct()
    .Count();

  /// <summary>
  /// An array parameter's descriptor has six fields this body reads, all at constant offsets from one
  /// pointer. Before the fold each took a base of its own; the bound is deliberately generous, because
  /// the claim is "the fields share a base", not an exact count that would break on any scheduling
  /// change.
  /// </summary>
  private const string _descriptorFields = """
    SUB S(a%()) NOINLINE
      PRINT LBOUND(a%); UBOUND(a%)
      a%(2) = a%(1) * 10
    END SUB
    DIM v%(1 TO 4)
    v%(1) = 7
    S v%()
    PRINT v%(2)
    """;

  [Test]
  public void Select_GivenManyConstantOffsetsFromOnePointer_ThenTheyShareABaseRegister() {
    var machine = Select(_descriptorFields, "S");

    Assert.That(DistinctBases(machine), Is.LessThanOrEqualTo(3),
      "each constant-offset field took a base register of its own, which is what exhausts the allocator");
  }

  /// <summary>
  /// And the allocation itself, which is the property that actually matters: the fixture above could
  /// pass on a count while the function still failed to allocate.
  /// </summary>
  [Test]
  public void Allocate_GivenAnArrayParameterBodyReadingBothBounds_ThenRegistersAreFound() {
    var machine = Select(_descriptorFields, "S");
    MachineScheduler.Schedule(machine);

    var allocation = LinearScanAllocator.Allocate(machine, out var reason);

    Assert.That(allocation, Is.Not.Null, $"allocation declined: {reason}");
  }

  /// <summary>
  /// A record's members are the same shape - constant offsets from one pointer - so the fold applies
  /// to them too, and this pins that the change was not special-cased to arrays.
  /// </summary>
  private const string _recordMembers = """
    TYPE T
      a AS INTEGER
      b AS INTEGER
      c AS INTEGER
      d AS INTEGER
    END TYPE
    FUNCTION F(p AS T) AS INTEGER NOINLINE
      F = p.a + p.b + p.c + p.d
    END FUNCTION
    DIM q AS T
    q.a = 1 : q.b = 2 : q.c = 3 : q.d = 4
    PRINT F(q)
    """;

  [Test]
  public void Select_GivenSeveralRecordMembersThroughOnePointer_ThenTheyShareABaseRegister() {
    var machine = Select(_recordMembers, "F");

    Assert.That(DistinctBases(machine), Is.LessThanOrEqualTo(2),
      "four members at constant offsets from one BYREF pointer must not need four bases");
  }
}
