using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// A back-end-compiled function reading a module-level variable. The back end lays out no data of its
/// own - the whole-program codegen does - so a global access resolves at emission to exactly the
/// <c>Mem</c> the direct emitter uses for that symbol. Both paths then address the same storage,
/// which is what lets a routed function and directly-emitted code share state at all.
///
/// The soundness question this had to answer first is whether that cell can be <b>stale</b>. It
/// cannot, for two independent reasons: a global a procedure can see is <c>SHARED</c>, and
/// <c>SsaForm.IsTrackableShape</c> excludes SHARED variables from SSA tracking - so no store to one is
/// ever elided and no read of one is ever folded to a constant; and register residency, which could
/// otherwise hold a value in SI/DI while the cell went stale, requires an SI/DI-clean region, which a
/// call is not.
/// </summary>
[TestFixture]
public sealed class BackendGlobalAccessTests {

  private const string _sharedGlobalProgram = """
    DIM g AS SHARED INTEGER

    FUNCTION AddG%(BYVAL v%)
      AddG% = v% + g
    END FUNCTION

    g = 40
    PRINT AddG%(2)
    """;

  private const string _sharedArrayAndStaticsProgram = """
    DIM tally(3) AS SHARED INTEGER

    FUNCTION Touch%(BYVAL index%)
      tally(index%) = tally(index%) + 10
      Touch% = tally(index%)
    END FUNCTION

    FUNCTION First%()
      STATIC count AS INTEGER
      count = count + 1
      First% = count
    END FUNCTION

    FUNCTION Second%()
      STATIC count AS INTEGER
      count = count + 10
      Second% = count
    END FUNCTION

    tally(1) = 2
    PRINT Touch%(1); tally(1)
    PRINT First%; First%; Second%; First%; Second%
    """;

  private const string _globalArrayAcrossCallProgram = """
    DIM values(3) AS SHARED INTEGER

    FUNCTION Supply%(BYVAL value%)
      Supply% = value% + 40
    END FUNCTION

    SUB Store(BYVAL index%)
      values(index%) = Supply%(index%)
    END SUB

    Store 2
    PRINT values(2)
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static IrModule Optimized(SemanticModel model) {
    var module = IrLowering.TryLowerModule(model);
    Assert.That(module, Is.Not.Null, "outside the IR lowering's subset");
    IrPassManager.Standard().RunOnModule(module!);
    foreach (var f in module!.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  [Test]
  public void Select_GivenFunctionReadingASharedGlobal_ThenAddressesItAsANamedDataCell() {
    var module = Optimized(Bind(_sharedGlobalProgram));
    var fn = module.Functions.First(f => f.Name.Equals("AddG", StringComparison.OrdinalIgnoreCase));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, $"AddG declined: {reason}");
    var cells = m!.AllInstructions
      .SelectMany(i => i.Operands)
      .OfType<MOperand.DataCell>()
      .ToList();
    Assert.That(cells, Is.Not.Empty, "the global is read through a data cell, not a register-held address");
    Assert.That(cells[0].Name, Does.StartWith("g."), "a module variable keeps the IR's g.<name> spelling");
  }

  [Test]
  public void Select_GivenSharedArrayWithRuntimeIndex_ThenStartsAtTheDirectEmittersNamedCell() {
    var module = Optimized(Bind(_sharedArrayAndStaticsProgram));
    var fn = module.Functions.First(f => f.Name.Equals("Touch", StringComparison.OrdinalIgnoreCase));

    var machine = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(machine, Is.Not.Null, $"Touch declined: {reason}");
    Assert.That(machine!.AllInstructions.SelectMany(i => i.Operands).OfType<MOperand.DataOffset>()
      .Select(offset => offset.Name), Does.Contain("g.tally"),
      "the computed address must start at the data cell the direct emitter owns");
  }

  [Test]
  public void Select_GivenSameNamedStaticsInTwoFunctions_ThenTheirCellsRemainDistinct() {
    var module = Optimized(Bind(_sharedArrayAndStaticsProgram));
    var first = module.Functions.First(f => f.Name.Equals("First", StringComparison.OrdinalIgnoreCase));
    var second = module.Functions.First(f => f.Name.Equals("Second", StringComparison.OrdinalIgnoreCase));

    var firstMachine = InstructionSelector.TrySelect(first, out var firstReason);
    var secondMachine = InstructionSelector.TrySelect(second, out var secondReason);

    Assert.That(firstMachine, Is.Not.Null, $"First declined: {firstReason}");
    Assert.That(secondMachine, Is.Not.Null, $"Second declined: {secondReason}");
    Assert.That(DataCells(firstMachine!), Does.Contain("static.First.count"));
    Assert.That(DataCells(secondMachine!), Does.Contain("static.Second.count"));
    Assert.That(DataCells(firstMachine!), Is.Not.EquivalentTo(DataCells(secondMachine!)),
      "same-named STATIC locals in different procedures must not alias");

    static IReadOnlyList<string> DataCells(MFunction fn) => fn.AllInstructions
      .SelectMany(i => i.Operands)
      .OfType<MOperand.DataCell>()
      .Select(cell => cell.Name)
      .Distinct()
      .ToList();
  }

  [Test]
  public void Allocate_GivenGlobalArrayAddressLiveAcrossCall_ThenPreservesItAtTheStore() {
    var module = Optimized(Bind(_globalArrayAcrossCallProgram));
    var fn = module.Functions.First(f => f.Name.Equals("Store", StringComparison.OrdinalIgnoreCase));
    var machine = InstructionSelector.TrySelect(fn, out var selectReason);
    Assert.That(machine, Is.Not.Null, $"Store declined: {selectReason}");
    MachineScheduler.Schedule(machine!);

    var allocation = LinearScanAllocator.Allocate(machine!, out var allocationReason);

    Assert.That(allocation, Is.Not.Null,
      $"allocation declined: {allocationReason}\n{string.Join(Environment.NewLine, machine!.AllInstructions)}");
    Assert.That(machine!.AllInstructions.SelectMany(i => i.Operands).OfType<MOperand.DataOffset>()
      .Select(offset => offset.Name), Does.Contain("g.values"));
  }

  [Test]
  public void Select_GivenSynthesizedIrGlobal_ThenAddressesTheBackEndsOwnCell() {
    // DATA/READ introduces .data and .data_cursor - IR globals with no PowerBASIC symbol behind them,
    // so there is no cell of the DIRECT emitter's to borrow. They used to decline for exactly that
    // reason. The back end now lays down its OWN pair (ir_datapool / ir_dataptr) beside the direct
    // emitter's, which is sound only because CodeGenerator.BackendOwnsData refuses to route DATA at
    // all when anything the direct emitter keeps also reads from one: the two cursors do not mean the
    // same thing - this one is a blob-relative INDEX, rt_dataptr is an ABSOLUTE pointer - so a
    // program reading through both would advance one and consult the other.
    var module = Optimized(Bind("""
      DIM n AS INTEGER
      DATA 7
      READ n
      PRINT n
      """));
    var main = module.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));

    var m = InstructionSelector.TrySelect(main, out var reason);

    Assert.That(m, Is.Not.Null, $"main declined: {reason}");
    var operands = m!.AllInstructions.SelectMany(i => i.Operands).ToList();
    Assert.That(operands.OfType<MOperand.DataCell>().Select(c => c.Name), Does.Contain(".data_cursor"),
      "the read cursor is READ and WRITTEN, so it is addressed as a named data cell");
    Assert.That(operands.OfType<MOperand.DataOffset>().Select(o => o.Name), Does.Contain(".data"),
      "the pool is INDEXED rather than loaded whole, so it is taken as an address");
  }

  [Test]
  public void Route_GivenAProcedureTheDirectEmitterKeepsAlsoReadingData_ThenNothingRoutes() {
    // Two pools are only sound while nothing uses both. Here `Grab` is never called, so the direct
    // emitter compiles it, and it READs - which would leave the module body advancing ir_dataptr
    // while `Grab` consults rt_dataptr. The whole arrangement is refused, and refused HERE: by
    // emission the only answer left would be an exception, because DataCellOf has no cell to hand
    // back and MachineEmitter raises on null.
    const string source = """
      DIM s AS STRING
      READ s
      PRINT s
      DATA one, two
      END

      SUB Grab
        DIM t AS STRING
        READ t
        PRINT t
      END SUB
      """;
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };

    var image = routed.EmitExecutable();

    Assert.Multiple(() => {
      Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
      Assert.That(routed.BackendRoutedNames, Does.Not.Contain("main"));
      Assert.That(image, Is.Not.Empty);
    });
  }

  [Test]
  public void Emit_GivenRoutedGlobalAccess_ThenTheImageAssemblesAndTheBackEndTookTheFunction() {
    var direct = new CodeGenerator(Bind(_sharedGlobalProgram)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(_sharedGlobalProgram)) { Optimize = true, UseExperimentalBackend = true };

    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routedImage, Is.Not.Empty);
    // an unresolved data reference would have thrown while the fixups resolved
    Assert.That(routed.BackendRoutedNames, Does.Contain("AddG"),
      "the back end did not take the global-reading function");
    Assert.That(directImage, Is.Not.Empty);
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenSharedArrayAndPersistentStatics_ThenBothEmittersAgreeWithoutFallback(bool optimize) {
    var direct = new CodeGenerator(Bind(_sharedArrayAndStaticsProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind(_sharedArrayAndStaticsProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = true,
    };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames,
      Is.SupersetOf(new[] { "Touch", "First", "Second", "main" }),
      "the feature under test must not pass through the direct-emitter fallback");
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
    Assert.That(directCpu.Output.Trim().Replace("\r\n", "|"), Is.EqualTo("12  12 | 1  2  10  3  20"));
  }
}
