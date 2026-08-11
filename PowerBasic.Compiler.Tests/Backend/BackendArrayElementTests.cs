using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Addressing ONE element of an array through the x86-16 back end, at an index the machine does not
/// know until it runs.
///
/// <para>
/// The IR says this two ways. Most arrays get a byte-offset GEP - the lowering has already multiplied
/// the flattened index by the element's size, because that size is a property of the source type and
/// the same everywhere. An array of dynamic STRINGs cannot: its elements are handles, and a handle's
/// width is a property of the TARGET (two bytes here, eight on a 64-bit host), so the lowering emits a
/// typed, element-indexed GEP and leaves the multiplication to whoever knows the answer. The x86-16
/// selector is one of those, and until it scaled the index itself every module body that read or wrote
/// <c>a$(i)</c> declined.
/// </para>
///
/// <para>
/// Both forms end in the same place: <c>[base + index]</c>, which on this target has no scale factor
/// at all, so a stride of two, four or six is a shift or a multiply in front of the address - or, when
/// the index is constant, nothing whatsoever, folded into the displacement.
/// </para>
///
/// <para>
/// Every case asserts the VALUES as well as agreement with the direct emitter, because agreement alone
/// would pass on a mistake the two paths shared: they share the runtime, the descriptor and the data
/// layout, and a stride that is wrong in the same direction twice reads back consistently wrong.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendArrayElementTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  /// <summary>Runs the program both ways, insisting the back end really took the code under test.</summary>
  private static (string Direct, string Routed) RunBothWays(string source, bool optimize) {
    var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
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

  /// <summary>PB pads printed numbers with sign and trailing blanks; the VALUES are what these tests are about.</summary>
  private static string[] Lines(string output) => output
    .Replace("\r", "")
    .Split('\n')
    .Select(line => string.Join(" ", line.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
    .ToArray();

  /// <summary>Both optimization settings, for the reason the corpus differential runs both: they are different emitters.</summary>
  private static void AssertAgreeAndRead(string source, params string[] expected) {
    foreach (var optimize in new[] { true, false }) {
      var (direct, routed) = RunBothWays(source, optimize);
      Assert.That(routed, Is.EqualTo(direct), $"the two back ends disagree (optimize={optimize})");
      Assert.That(Lines(routed).Take(expected.Length), Is.EqualTo(expected).AsCollection,
        $"...and the answer both give is not the one BASIC gives (optimize={optimize})");
    }
  }

  /// <summary>
  /// The case the element-indexed GEP exists for: a STRING array, written and read back at an index
  /// the loop supplies. Every element is a two-byte handle, so element three lives six bytes in - and
  /// a selector that forgot to scale would have written all four strings over the first one.
  /// </summary>
  [Test]
  public void Run_GivenAStringArrayAtARuntimeIndex_ThenEachElementKeepsItsOwnHandle() {
    AssertAgreeAndRead("""
      DIM s(1 TO 4) AS STRING
      FOR i% = 1 TO 4
        s(i%) = CHR$(64 + i%) + "-" + CHR$(48 + i%)
      NEXT i%
      k% = 1
      k% = k% + 2
      PRINT s(k%)
      FOR i% = 1 TO 4 : PRINT s(i%); : NEXT i% : PRINT ""
      """,
      "C-3",
      "A-1B-2C-3D-4");
  }

  /// <summary>
  /// The same array read at a CONSTANT index, which is the other half of the scaling: the index is
  /// known here, so the stride is a displacement and there is no shift at all. Reading element four as
  /// a constant after writing it through a variable says the two agree about where element four is.
  /// </summary>
  [Test]
  public void Run_GivenAStringArrayAtAConstantIndex_ThenTheStrideFoldsIntoTheDisplacement() {
    AssertAgreeAndRead("""
      DIM s(0 TO 3) AS STRING
      FOR i% = 0 TO 3 : s(i%) = "x" + CHR$(48 + i%) : NEXT i%
      PRINT s(0); s(3)
      PRINT LEN(s(2))
      """,
      "x0x3",
      "2");
  }

  /// <summary>
  /// A string array whose lower bound is NEGATIVE. The flattening subtracts the lower bound before it
  /// scales, so an array based at -2 is the case that says the subtraction happens on the INDEX rather
  /// than on the byte offset - based at zero the two are indistinguishable.
  /// </summary>
  [Test]
  public void Run_GivenAStringArrayWithANegativeLowerBound_ThenTheIndexIsRebasedBeforeItIsScaled() {
    AssertAgreeAndRead("""
      DIM t(-2 TO 2) AS STRING
      FOR i% = -2 TO 2 : t(i%) = "n" + CHR$(48 + i% + 2) : NEXT i%
      j% = -4
      j% = j% + 2
      PRINT t(j%)
      FOR i% = -2 TO 2 : PRINT t(i%); : NEXT i% : PRINT ""
      """,
      "n0",
      "n0n1n2n3n4");
  }

  /// <summary>
  /// One stride of each width, all indexed at run time: a BYTE array walks by one, an INTEGER by two,
  /// a LONG by four. The three are written in one pass and read back in another, so an element that
  /// landed on top of its neighbour shows up as a wrong value rather than as a crash.
  /// </summary>
  [Test]
  public void Run_GivenEveryScalarWidthAtARuntimeIndex_ThenEachStrideAddressesItsOwnElement() {
    AssertAgreeAndRead("""
      DIM b(1 TO 4) AS BYTE
      DIM w(1 TO 4) AS INTEGER
      DIM d(1 TO 4) AS LONG
      FOR i% = 1 TO 4
        b(i%) = i% * 3
        w(i%) = i% * 1000
        d(i%) = i% * 100000
      NEXT i%
      FOR i% = 1 TO 4 : PRINT b(i%); : NEXT i% : PRINT ""
      FOR i% = 1 TO 4 : PRINT w(i%); : NEXT i% : PRINT ""
      FOR i% = 1 TO 4 : PRINT d(i%); : NEXT i% : PRINT ""
      """,
      "3 6 9 12",
      "1000 2000 3000 4000",
      "100000 200000 300000 400000");
  }

  /// <summary>
  /// A RECORD stride, which is the one that is not a power of two: six bytes per element, so the
  /// address is a multiply and not a shift. Reading a member of element three is what says the
  /// multiply happened - element three of a six-byte record starts where element four and a half of a
  /// four-byte one would.
  /// </summary>
  [Test]
  public void Run_GivenARecordStrideAtARuntimeIndex_ThenTheOffsetIsMultipliedRatherThanShifted() {
    AssertAgreeAndRead("""
      TYPE Triple
        a AS INTEGER
        b AS INTEGER
        c AS INTEGER
      END TYPE
      DIM p(1 TO 4) AS Triple
      FOR i% = 1 TO 4
        p(i%).a = i%
        p(i%).b = i% * 10
        p(i%).c = i% * 100
      NEXT i%
      k% = 1
      k% = k% + 2
      PRINT p(k%).a; p(k%).b; p(k%).c
      FOR i% = 1 TO 4 : PRINT p(i%).b; : NEXT i% : PRINT ""
      """,
      "3 30 300",
      "10 20 30 40");
  }

  /// <summary>
  /// A two-dimensional string array, so the flattening itself is exercised: the row-major index is a
  /// runtime product, and only then does the element stride multiply it again.
  /// </summary>
  [Test]
  public void Run_GivenATwoDimensionalStringArray_ThenRowMajorFlatteningSurvivesTheScaling() {
    AssertAgreeAndRead("""
      DIM g(1 TO 2, 1 TO 3) AS STRING
      FOR r% = 1 TO 2
        FOR c% = 1 TO 3
          g(r%, c%) = CHR$(64 + r%) + CHR$(48 + c%)
        NEXT c%
      NEXT r%
      PRINT g(2, 3); g(1, 1)
      FOR r% = 1 TO 2 : FOR c% = 1 TO 3 : PRINT g(r%, c%); : NEXT c% : NEXT r% : PRINT ""
      """,
      "B3A1",
      "A1A2A3B1B2B3");
  }

  /// <summary>
  /// The selector's own answer for a stride that is not a power of two, which no BASIC program reaches
  /// - a record element travels as a byte offset the lowering already multiplied, and the only typed
  /// GEPs the lowering emits are over <c>ptr</c>. Asked directly, the selector must still multiply
  /// rather than shift, because <c>[base + index]</c> on this target has no scale.
  /// </summary>
  [Test]
  public void TrySelect_GivenAnElementIndexedGepWithAnOddStride_ThenTheIndexIsMultiplied() {
    var index = new IrArgument(IrType.I16, 0);
    var fn = new IrFunction("F", IrType.I16, [index]);
    var entry = fn.CreateBlock("entry");
    var array = entry.Append(new IrAlloca(IrType.F80) { Count = 4 });
    var element = entry.Append(new IrGep(array, index, IrType.F80));      // ten bytes per element
    entry.Append(new IrRet(entry.Append(new IrLoad(IrType.I16, element))));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    var opcodes = m!.AllInstructions.Select(i => i.Opcode).ToList();
    Assert.That(opcodes, Does.Contain(MOpcode.Imul), "a ten-byte stride cannot be a shift");
    Assert.That(opcodes, Does.Not.Contain(MOpcode.Shl));
  }

  /// <summary>
  /// A pointer stride IS a power of two, so the same address costs one shift and no multiply. Two
  /// bytes is this target's near pointer - the number the IR deliberately does not carry.
  /// </summary>
  [Test]
  public void TrySelect_GivenAnElementIndexedGepOverPointers_ThenTheIndexIsShiftedByOne() {
    var index = new IrArgument(IrType.I16, 0);
    var fn = new IrFunction("F", IrType.Ptr, [index]);
    var entry = fn.CreateBlock("entry");
    var array = entry.Append(new IrAlloca(IrType.Ptr) { Count = 4 });
    var element = entry.Append(new IrGep(array, index, IrType.Ptr));
    entry.Append(new IrRet(entry.Append(new IrLoad(IrType.Ptr, element))));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    var shift = m!.AllInstructions.SingleOrDefault(i => i.Opcode == MOpcode.Shl);
    Assert.That(shift, Is.Not.Null, "a two-byte stride is one shift");
    Assert.That(((MOperand.Immediate)shift!.Operands[1]).Value, Is.EqualTo(1));
    Assert.That(m.AllInstructions.Select(i => i.Opcode), Does.Not.Contain(MOpcode.Imul));
  }

  /// <summary>
  /// A CONSTANT element index costs no instruction at all: the stride is folded into the address's
  /// displacement, which is the shape the byte-offset GEP already had and the one the warning about
  /// materializing addresses is about - six live address registers in a block is more than this
  /// machine has.
  /// </summary>
  [Test]
  public void TrySelect_GivenAConstantElementIndex_ThenTheStrideBecomesADisplacement() {
    var fn = new IrFunction("F", IrType.Ptr);
    var entry = fn.CreateBlock("entry");
    var array = entry.Append(new IrAlloca(IrType.Ptr) { Count = 8 });
    var element = entry.Append(new IrGep(array, new IrConstantInt(IrType.I32, 5), IrType.Ptr));
    entry.Append(new IrRet(entry.Append(new IrLoad(IrType.Ptr, element))));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    Assert.That(m!.AllInstructions.Select(i => i.Opcode), Does.Not.Contain(MOpcode.Shl));
    // the first LEA is the alloca's own base address; the element's is the one formed from it
    var lea = m.AllInstructions.Last(i => i.Opcode == MOpcode.Lea);
    Assert.That(((MOperand.Memory)lea.Operands[1]).Disp, Is.EqualTo(10));   // element 5 of a two-byte stride
    Assert.That(((MOperand.Memory)lea.Operands[1]).Index, Is.Null);
  }

  /// <summary>
  /// The allocator's side of the same story, and the bug the reordering above uncovered: a value must
  /// not be left in a register that an ABI-pinned move is about to write while the value still has a
  /// reader. <c>MOV AX, lo</c> followed by <c>MOV DX, hi</c> returned the low word TWICE for as long
  /// as the allocator was free to put <c>lo</c> in the very <c>DX</c> the second move reads from.
  /// </summary>
  [Test]
  public void Allocate_GivenAPairReturnedThroughDxAx_ThenNeitherHalfLivesInTheOthersRegister() {
    var m = new MFunction("F");
    var entry = new MBlock("entry");
    m.Blocks.Add(entry);
    var lo = new MOperand.Register(MReg.Virtual(0));
    var hi = new MOperand.Register(MReg.Virtual(1));
    var ax = new MOperand.Register(MReg.Physical_(Reg.AX));
    var dx = new MOperand.Register(MReg.Physical_(Reg.DX));
    var move = new MInstrEffect(WrittenRegs: [0], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
      ReadsMemory: false, WritesMemory: false);
    var load = new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
      ReadsMemory: false, WritesMemory: false);
    entry.Instructions.Add(new MInstr(MOpcode.Mov, [lo, new MOperand.Immediate(1)], load));
    entry.Instructions.Add(new MInstr(MOpcode.Mov, [hi, new MOperand.Immediate(2)], load));
    entry.Instructions.Add(new MInstr(MOpcode.Mov, [ax, lo], move));
    entry.Instructions.Add(new MInstr(MOpcode.Mov, [dx, hi], move));
    entry.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
    m.VirtualRegisterCount = 2;

    var allocation = LinearScanAllocator.Allocate(m, out var reason);

    Assert.That(allocation, Is.Not.Null, reason);
    // lo is read by the move that writes AX and dies there, so AX itself is still fair game for it;
    // hi is read AFTER that move, so it may be in neither AX nor DX
    Assert.That(allocation![1], Is.Not.EqualTo(Reg.AX));
    Assert.That(allocation[1], Is.Not.EqualTo(Reg.DX));
  }
}
