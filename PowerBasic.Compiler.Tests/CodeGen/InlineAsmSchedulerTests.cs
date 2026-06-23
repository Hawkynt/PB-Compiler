using PowerBasic.Compiler.CodeGen;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 inline-asm scheduler: reorders a run of single-instruction <c>!</c> lines to group memory and
/// ALU operations while preserving every register/flags/memory dependency, leaving the block verbatim
/// whenever a line is not confidently modelled. Reordering is output-preserving (a valid topological
/// order of the dependency partial order).
/// </summary>
[TestFixture]
public sealed class InlineAsmSchedulerTests {

  private static IReadOnlyList<string> Run(params string[] lines) {
    var order = InlineAsmScheduler.Schedule(lines);
    return order is null ? lines : order.Select(i => lines[i]).ToList();
  }

  [Test]
  public void Schedule_GivenTwoIndependentChains_ThenGroupsLoadsThenAlu() {
    // mov/add/mov/add of independent (a->AX, b->BX) chains regroups to: both loads, then both adds
    var result = Run("MOV AX, a", "ADD AX, 5", "MOV BX, b", "ADD BX, 7", "MOV r1, AX", "MOV r2, BX");
    Assert.That(result, Is.EqualTo(new[] { "MOV AX, a", "MOV BX, b", "ADD AX, 5", "ADD BX, 7", "MOV r1, AX", "MOV r2, BX" }));
  }

  [Test]
  public void Schedule_GivenDependentChain_ThenUnchanged() {
    // a strict producer->consumer chain through AX has only one valid order
    var result = Run("MOV AX, a", "ADD AX, 5", "SHL AX, 1");
    Assert.That(result, Is.EqualTo(new[] { "MOV AX, a", "ADD AX, 5", "SHL AX, 1" }));
  }

  [Test]
  public void Schedule_GivenUnknownMnemonic_ThenLeavesBlockVerbatim() {
    // a CALL is not modelled -> the whole block is left in source order (no reordering)
    var result = Run("MOV AX, a", "CALL Foo", "MOV BX, b", "MOV r, AX");
    Assert.That(result, Is.EqualTo(new[] { "MOV AX, a", "CALL Foo", "MOV BX, b", "MOV r, AX" }));
  }

  [Test]
  public void Schedule_GivenFlagDependency_ThenKeepsCmpBeforeFlagUser() {
    // a flags writer (CMP) must stay before code that the schedule cannot move past it; ADC reads carry
    var result = Run("MOV AX, a", "ADD AX, BX", "ADC DX, 0");
    // ADD sets flags, ADC reads them -> ADC stays after ADD; MOV has no flags -> may move but is independent of the chain start
    Assert.That(result[2], Is.EqualTo("ADC DX, 0"), "the carry consumer stays last");
  }

  [Test]
  public void Schedule_GivenAliasedMemory_ThenPreservesOrderThroughIndexedAccess() {
    // an indexed [BX] store aliases everything, so a later indexed load cannot hop before it
    var result = Run("MOV [BX], AX", "MOV CX, dd", "MOV DX, [BX]");
    Assert.That(result, Is.EqualTo(new[] { "MOV [BX], AX", "MOV CX, dd", "MOV DX, [BX]" }).Or.EqualTo(
      new[] { "MOV [BX], AX", "MOV DX, [BX]", "MOV CX, dd" }), "the [BX] store stays before the [BX] load");
  }

  [Test]
  public void Schedule_GivenWriteThenReadSameRegister_ThenOrderPreserved() {
    var result = Run("MOV AX, 1", "MOV BX, AX", "MOV CX, 2");
    Assert.That(result.ToList().IndexOf("MOV AX, 1"), Is.LessThan(result.ToList().IndexOf("MOV BX, AX")));
  }
}
