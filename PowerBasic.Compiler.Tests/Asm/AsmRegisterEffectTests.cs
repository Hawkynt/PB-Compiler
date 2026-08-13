using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

/// <summary>
/// Reading one inline-assembly statement's register effect out of its text
/// (<see cref="TextAssembler.Analyze"/>).
///
/// The claims are approximated in different directions on purpose - reads and definitions upwards,
/// kills downwards - because only one of the three is safe to over-state. The cases below are one per
/// equivalence class of that asymmetry, plus the boundary that matters most: a statement the table
/// does not model must say so rather than answer "touches nothing".
/// </summary>
[TestFixture]
public sealed class AsmRegisterEffectTests {

  /// <summary>
  /// Answers a named identifier as code and every other as storage - the two kinds a PB inline-asm
  /// name has, and the same distinction the selector's own resolver makes.
  /// </summary>
  private sealed class Cells(params string[] code) : IAsmSymbolResolver {

    private readonly Assembler _labels = new();

    public bool TryResolve(string name, out AsmSymbol symbol) {
      symbol = code.Contains(name, StringComparer.OrdinalIgnoreCase)
        ? AsmSymbol.OfLabel(this._labels.Lbl(name))
        : AsmSymbol.OfMemory(Mem.Word(Reg.BP, 0));
      return true;
    }
  }

  private static AsmRegisterEffect Effect(string line, params string[] code)
    => TextAssembler.Analyze(line, new Cells(code));

  [Test]
  public void Analyze_GivenAnImmediateLoad_WhenRead_ThenItDefinesAndReadsNothing() {
    var effect = Effect("MOV CX, 5");

    Assert.Multiple(() => {
      Assert.That(effect.IsOpaque, Is.False);
      Assert.That(effect.Reads, Is.Empty);
      Assert.That(effect.Defines, Is.EquivalentTo(new[] { Reg.CX }));
      Assert.That(effect.Kills, Is.EquivalentTo(new[] { Reg.CX }), "a whole word is overwritten");
      Assert.That(effect.WritesFlags, Is.False, "MOV is the one arithmetic-shaped instruction that does not");
    });
  }

  [Test]
  public void Analyze_GivenADecrement_WhenRead_ThenItReadsWritesAndSetsFlags() {
    var effect = Effect("DEC CX");

    Assert.Multiple(() => {
      Assert.That(effect.Reads, Is.EquivalentTo(new[] { Reg.CX }));
      Assert.That(effect.Defines, Is.EquivalentTo(new[] { Reg.CX }));
      Assert.That(effect.WritesFlags, Is.True);
      Assert.That(effect.ReadsFlags, Is.False);
    });
  }

  [Test]
  public void Analyze_GivenAConditionalJump_WhenRead_ThenItOnlyConsumesTheFlags() {
    var effect = Effect("JNZ AddLoop", "AddLoop");

    Assert.Multiple(() => {
      Assert.That(effect.ReadsFlags, Is.True);
      Assert.That(effect.Reads, Is.Empty, "a label is not a register");
      Assert.That(effect.Defines, Is.Empty);
    });
  }

  /// <summary>The address registers of a memory operand are read wherever the operand sits.</summary>
  [Test]
  public void Analyze_GivenASegmentedIndexedLoad_WhenRead_ThenTheBaseIsARead() {
    var effect = Effect("MOV AL, ES:[BX]");

    Assert.Multiple(() => {
      Assert.That(effect.Reads, Is.EquivalentTo(new[] { Reg.BX }), "ES is not a register this allocates");
      Assert.That(effect.Defines, Is.EquivalentTo(new[] { Reg.AX }), "AL contends for AX");
      Assert.That(effect.Kills, Is.Empty, "...but only half of it, so AH's producer keeps its claim");
    });
  }

  /// <summary>A write THROUGH a register still reads the register - the destination is memory.</summary>
  [Test]
  public void Analyze_GivenAStoreToAVariable_WhenRead_ThenTheSourceRegisterIsARead() {
    var effect = Effect("MOV n, AX");

    Assert.Multiple(() => {
      Assert.That(effect.Reads, Does.Contain(Reg.AX));
      Assert.That(effect.Defines, Is.Empty, "the destination is a cell, not a register");
    });
  }

  /// <summary>
  /// The one-operand multiply writes AX at every width and DX only at sixteen bits, so DX is defined
  /// without being killed - over-stating a kill would end an earlier statement's claim on it.
  /// </summary>
  [Test]
  public void Analyze_GivenAOneOperandMultiply_WhenRead_ThenDxIsDefinedButNotKilled() {
    var effect = Effect("MUL BX");

    Assert.Multiple(() => {
      Assert.That(effect.Reads, Is.SupersetOf(new[] { Reg.AX, Reg.BX }));
      Assert.That(effect.Defines, Is.SupersetOf(new[] { Reg.AX, Reg.DX }));
      Assert.That(effect.Kills, Is.EquivalentTo(new[] { Reg.AX }));
    });
  }

  /// <summary>A REP prefix counts CX down, which is a read and a write the mnemonic alone does not show.</summary>
  [Test]
  public void Analyze_GivenARepeatedStringMove_WhenRead_ThenTheCounterIsReadAndWritten() {
    var effect = Effect("REP MOVSW");

    Assert.Multiple(() => {
      Assert.That(effect.Reads, Is.SupersetOf(new[] { Reg.CX, Reg.SI, Reg.DI }));
      Assert.That(effect.Defines, Is.SupersetOf(new[] { Reg.CX, Reg.SI, Reg.DI }));
    });
  }

  /// <summary>
  /// What an <c>INT</c> reads is a property of the handler and the function code in AH, not of the
  /// instruction. Naming a plausible set would be the guess the whole model exists to avoid, so it
  /// reads and writes everything - which keeps a producer before it and a consumer after it both
  /// protected, and is exactly what "not understood" has to mean.
  /// </summary>
  [Test]
  public void Analyze_GivenAnInterrupt_WhenRead_ThenItIsOpaqueBothWays() {
    var effect = Effect("INT &H10");

    Assert.Multiple(() => {
      Assert.That(effect.IsOpaque, Is.True);
      Assert.That(effect.Reads, Is.EquivalentTo(AsmRegisterEffect.GeneralRegisters));
      Assert.That(effect.Defines, Is.EquivalentTo(AsmRegisterEffect.GeneralRegisters));
    });
  }

  [Test]
  public void Analyze_GivenACall_WhenRead_ThenItIsOpaque()
    => Assert.That(Effect("CALL GetStrLoc", "GetStrLoc").IsOpaque, Is.True, "whatever the callee does");

  /// <summary>A mnemonic the table does not model, and a line that does not parse, mean the same thing.</summary>
  [TestCase("PUSHA")]
  [TestCase("FLD QWORD PTR [BX]")]
  [TestCase("MOV AX, ,")]
  [TestCase("")]
  public void Analyze_GivenSomethingUnmodelled_WhenRead_ThenItIsOpaqueRatherThanEmpty(string line)
    => Assert.That(Effect(line).IsOpaque, Is.True);

  /// <summary>
  /// BP and SP are tracked even though nothing is allocated to them, because a statement writing one
  /// is a statement the routed frame cannot survive - the selector reads exactly this to decline.
  /// </summary>
  [Test]
  public void Analyze_GivenAWriteToTheFramePointer_WhenRead_ThenItIsReportedAsADefinition()
    => Assert.That(Effect("MOV BP, AX").Defines, Does.Contain(Reg.BP));
}
