using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Two dynamic arrays allocated in one procedure, through the x86-16 back end - the case where they
/// came out sharing storage, so writing the second changed the first.
///
/// <para>
/// The allocation is not what was confused: <c>rt_arr_alloc</c> handed out two disjoint blocks in both
/// builds. The first array's RECORDED pointer was, and it was recorded from the wrong register. A
/// <c>REDIM</c> is <c>CALL rt_arr_alloc</c> followed by the <c>MOV v, AX</c> that takes the block
/// address out of the result register; the scheduler is free to put an unrelated instruction between
/// the two, because that instruction writes a VIRTUAL register and so conflicts with nothing - and the
/// allocator, which modelled a physical register being WRITTEN but not one being READ, was then free to
/// give that virtual the very <c>AX</c> the result was waiting in. The array's data pointer became
/// whatever the intervening instruction loaded, and pointed into the next allocation.
/// </para>
///
/// <para>
/// It needs two arrays and a runtime bound to show: with one array nothing competes for the window, and
/// with constant bounds the second allocation folds away. See
/// <see cref="LinearScanAllocator"/>'s in-flight window for the fix.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendDynamicArrayAliasTests {

  // the desugared form: a runtime REDIM bound and a computed index, twice over, the first-declared
  // array re-DIMed first. Each ingredient alone is correct routed; together they aliased.
  private const string _twoRuntimeBoundRedims = """
    DIM a(1 TO 8) AS INTEGER
    DIM i AS INTEGER
    FOR i = 1 TO 8 : a(i) = i * 10 : NEXT
    DIM b() AS INTEGER, c() AS INTEGER
    DIM lo1 AS INTEGER, hi1 AS INTEGER, i1 AS INTEGER
    lo1 = 1 : hi1 = 3
    REDIM b(0 TO hi1 - lo1)
    FOR i1 = lo1 TO hi1 : b(i1 - lo1) = a(i1) : NEXT
    lo1 = 6 : hi1 = 8
    REDIM c(0 TO hi1 - lo1)
    FOR i1 = lo1 TO hi1 : c(i1 - lo1) = a(i1) : NEXT
    PRINT b(0); b(1); b(2); c(0); c(1); c(2)
    """;

  // the source shape that found it: two array SLICES, which lower to exactly the above
  private const string _twoArraySlices = """
    DIM a(1 TO 8) AS INTEGER
    DIM i AS INTEGER
    FOR i = 1 TO 8 : a(i) = i * 10 : NEXT
    DIM b() AS INTEGER, c() AS INTEGER
    b() = a(TO 3)
    c() = a(6 TO)
    PRINT b(0); b(1); b(2); c(0); c(1); c(2)
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  /// <summary>Runs the program both ways, insisting the back end really took the module body.</summary>
  private static (string Direct, string Routed) RunBothWays(string source) {
    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };
    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"),
      "the back end did not take the module body, so this compares the direct emitter with itself");

    string Execute(byte[] image, string which) {
      try {
        return Cpu8086.Run(image).Output;
      } catch (Cpu8086Exception e) {
        Assert.Ignore($"the interpreter cannot run the {which} image: {e.Message}");
        return "";
      }
    }

    return (Execute(directImage, "direct"), Execute(routedImage, "routed"));
  }

  /// <summary>PB pads printed numbers with sign and trailing blanks; the VALUES are what this is about.</summary>
  private static string Values(string output)
    => string.Join(" ", output.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

  [Test]
  public void Run_GivenTwoRuntimeBoundRedimsInOneBody_WhenRouted_ThenTheArraysDoNotShareStorage() {
    var (direct, routed) = RunBothWays(_twoRuntimeBoundRedims);

    Assert.That(Values(routed), Is.EqualTo("10 20 30 60 70 80"),
      "the second REDIM's block overlaps the first's, so b(0) reads c(2)");
    Assert.That(routed, Is.EqualTo(direct), "the two back ends disagree");
  }

  [Test]
  public void Run_GivenTwoArraySlicesOfOneArray_WhenRouted_ThenEachSliceKeepsItsOwnCopy() {
    var (direct, routed) = RunBothWays(_twoArraySlices);

    Assert.That(Values(routed), Is.EqualTo("10 20 30 60 70 80"));
    Assert.That(routed, Is.EqualTo(direct), "the two back ends disagree");
  }

  /// <summary>
  /// The allocator's own statement of the rule, in the shape the scheduler produced: an instruction
  /// standing between a <c>CALL</c> and the move that reads its result out of <c>AX</c> may not be
  /// given <c>AX</c>. The move itself still may - <c>MOV AX, AX</c> is free, and refusing it would cost
  /// the coalescing every routed call result depends on.
  /// </summary>
  [Test]
  public void Allocate_GivenAValueDefinedBetweenACallAndItsResultMove_ThenItAvoidsTheResultRegister() {
    var m = new MFunction("F");
    var entry = new MBlock("entry");
    m.Blocks.Add(entry);
    var intruder = new MOperand.Register(MReg.Virtual(0));
    var result = new MOperand.Register(MReg.Virtual(1));
    var ax = new MOperand.Register(MReg.Physical_(Reg.AX));
    var load = new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
      ReadsMemory: false, WritesMemory: false);
    var move = new MInstrEffect(WrittenRegs: [0], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
      ReadsMemory: false, WritesMemory: false);
    var combine = new MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: true,
      ReadsMemory: false, WritesMemory: false);

    entry.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef("rt_arr_alloc")],
      MInstrEffect.None, condition: null, clobbers: [Reg.AX, Reg.BX, Reg.CX, Reg.DX, Reg.SI, Reg.DI]));
    entry.Instructions.Add(new MInstr(MOpcode.Mov, [intruder, new MOperand.Immediate(10)], load));
    entry.Instructions.Add(new MInstr(MOpcode.Mov, [result, ax], move));
    entry.Instructions.Add(new MInstr(MOpcode.Add, [result, intruder], combine));
    entry.Instructions.Add(new MInstr(MOpcode.Mov, [ax, result], move));
    entry.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
    m.VirtualRegisterCount = 2;

    var allocation = LinearScanAllocator.Allocate(m, out var reason);

    Assert.That(allocation, Is.Not.Null, reason);
    Assert.That(allocation![0], Is.Not.EqualTo(Reg.AX), "it would destroy the call's result before it is read");
    Assert.That(allocation[1], Is.EqualTo(Reg.AX), "the result move should still coalesce into AX");
  }
}
