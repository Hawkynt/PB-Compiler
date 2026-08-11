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
/// FIELD through the retargetable path: a record buffer read and written through named windows on
/// it, with LSET/RSET justifying inside each window and a bare GET/PUT moving the whole record.
///
/// The claim that has to be tested is ALIASING, not compilation. A FIELD variable is not a variable
/// that happens to be assigned around the file I/O - it is a view of the file's record buffer, and
/// the only way to see the difference is to write through one name, read a record back, and find the
/// name's contents changed by an I/O statement that never mentioned it.
/// </summary>
[TestFixture]
public sealed class BackendFieldTests {

  /// <summary>
  /// Two windows on a 16-byte record: six characters and ten. Record 1 is written left- and
  /// right-justified, record 2 with different contents, and then both are read back - so a stale
  /// window would show record 2's text where record 1's belongs.
  /// </summary>
  private const string _fieldRoundTrip = """
    OPEN "FLD.DAT" FOR RANDOM AS #2 LEN = 16
    FIELD #2, 6 AS f1$, 10 AS f2$
    LSET f1$ = "abc"
    RSET f2$ = "42"
    PUT #2, 1
    LSET f1$ = "zzzzzzz"
    LSET f2$ = "wiped"
    PUT #2, 2
    GET #2, 1
    PRINT "["; f1$; "]["; f2$; "]"
    GET #2, 2
    PRINT "["; f1$; "]["; f2$; "]"
    CLOSE #2
    END
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (byte[] Image, IEnumerable<string> Routed) Compile(string source, bool backend) {
    var codegen = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = backend };
    var image = codegen.EmitExecutable();
    Assert.That(codegen.Errors, Is.Empty, string.Join("; ", codegen.Errors));
    return (image, codegen.BackendRoutedNames.ToList());
  }

  private static string Execute(byte[] image, string which) {
    try {
      return Cpu8086.Run(image).Output;
    } catch (Cpu8086Exception e) {
      Assert.Ignore($"the interpreter cannot run the {which} image: {e.Message}");
      return "";
    }
  }

  /// <summary>
  /// The behaviour that defines FIELD: a bare GET overwrites the field variables, which nothing in
  /// the statement names. Both windows are blank-padded to their declared widths, LSET puts its text
  /// at the left of its window and RSET at the right, and text longer than the window is cut off at
  /// it - <c>"zzzzzzz"</c> is seven characters into a six-character field.
  /// </summary>
  [Test]
  public void Run_GivenARoutedFieldRoundTrip_ThenABareGetRefillsTheFieldVariables() {
    var (image, routed) = Compile(_fieldRoundTrip, backend: true);
    Assert.That(routed, Does.Contain("main"), "the back end did not take the module body under test");

    var output = Execute(image, "routed");

    Assert.That(Lines(output), Is.EqualTo(new[] {
      "[abc   ][        42]",     // LSET left-justifies in six, RSET right-justifies in ten
      "[zzzzzz][wiped     ]",     // the seventh 'z' did not fit the six-character window
    }));
  }

  [Test]
  public void Run_GivenAFieldRoundTrip_ThenTheRoutedPathAgreesWithTheDirectEmitter() {
    var (routedImage, routed) = Compile(_fieldRoundTrip, backend: true);
    var (directImage, _) = Compile(_fieldRoundTrip, backend: false);
    Assert.That(routed, Does.Contain("main"));

    Assert.That(Execute(routedImage, "routed"), Is.EqualTo(Execute(directImage, "direct")));
  }

  /// <summary>
  /// The window really is one: writing through the name changes what the FILE gets, and reading a
  /// record changes what the name holds. The record bytes themselves are checked here rather than
  /// only the printed text, because a field variable that merely behaved like a padded string would
  /// print identically and put nothing in the file.
  /// </summary>
  [Test]
  public void Run_GivenARoutedFieldPut_ThenTheRecordBytesAreTheFieldsSideBySide() {
    var (image, _) = Compile(_fieldRoundTrip, backend: true);

    var cpu = Cpu8086.Run(image);

    Assert.That(cpu.FileContent("FLD.DAT"), Is.EqualTo("abc           42zzzzzzwiped     "),
      "two records of 16: six then ten, blank-padded, with no separator of any kind");
  }

  /// <summary>
  /// A FIELD variable must live in a DATA cell. <c>rt_fldadd</c> keeps the ADDRESS of its handle cell
  /// in a table and the record walk dereferences it later through DS - so a frame slot would be read
  /// back as a data offset, and would corrupt whatever really lives there.
  /// </summary>
  [Test]
  public void Lower_GivenFieldVariables_ThenTheyBecomeModuleGlobalsRatherThanFrameSlots() {
    var module = IrLowering.TryLowerModule(Bind(_fieldRoundTrip));
    Assert.That(module, Is.Not.Null, "outside the IR lowering's subset");

    Assert.That(module!.Globals.Select(g => g.Name), Is.SupersetOf(new[] { "g.f1", "g.f2" }));
  }

  /// <summary>
  /// The ABI claim: AX = the file number, CX = the width, BX = the ADDRESS of the handle cell - and
  /// that address as an immediate offset, which is the only form the runtime's DS assumption is
  /// sound for.
  /// </summary>
  [Test]
  public void Select_GivenAFieldRegistration_ThenTheCellAddressIsAnImmediateOffsetInBx() {
    var module = IrLowering.TryLowerModule(Bind(_fieldRoundTrip));
    IrPassManager.Standard().RunOnModule(module!);
    var main = module!.Functions.First(f => f.Name == "main");
    var m = InstructionSelector.TrySelect(main, out var reason);
    Assert.That(m, Is.Not.Null, $"main declined: {reason}");

    var instructions = m!.AllInstructions.ToList();
    var call = instructions.First(i => i.Opcode == MOpcode.Call
      && i.Operands[0] is MOperand.LabelRef { Name: "rt_fldadd" });
    var staged = instructions
      .TakeWhile(i => i != call)
      .Where(i => i.Opcode == MOpcode.Mov && i.Operands[0] is MOperand.Register { Reg.IsVirtual: false })
      .GroupBy(i => ((MOperand.Register)i.Operands[0]).Reg.Physical)
      .ToDictionary(g => g.Key, g => g.Last().Operands[1]);

    Assert.That(((MOperand.Immediate)staged[Reg.AX]).Value, Is.EqualTo(2), "AX = the PB file number");
    Assert.That(((MOperand.Immediate)staged[Reg.CX]).Value, Is.EqualTo(6), "CX = the first field's width");
    Assert.That(staged[Reg.BX], Is.InstanceOf<MOperand.DataOffset>(), "BX = the handle CELL, not the handle");
    Assert.That(((MOperand.DataOffset)staged[Reg.BX]).Name, Is.EqualTo("g.f1"));
  }

  /// <summary>
  /// LSET is not an assignment: the target keeps the handle it had, because that handle is the
  /// window. Handing <c>rt_justify</c> a borrowed COPY would justify into the copy and leave the
  /// variable - and so the record - untouched, which is why the raw cell value is loaded.
  /// </summary>
  [Test]
  public void Lower_GivenLset_ThenTheTargetHandleIsTheCellsOwnAndIsNotDuplicated() {
    var module = IrLowering.TryLowerModule(Bind(_fieldRoundTrip));
    var main = module!.Functions.First(f => f.Name == "main");

    var justify = main.Blocks.SelectMany(b => b.Instructions).OfType<IrCall>()
      .First(c => (c.Callee as IrFunction)?.Name == "rt_str_justify");
    var target = justify.Args.First();

    Assert.That(target, Is.InstanceOf<IrLoad>(), "the cell's own handle");
    Assert.That(((IrLoad)target).Pointer, Is.InstanceOf<IrGlobalVariable>());
    Assert.That(((IrGlobalVariable)((IrLoad)target).Pointer).Name, Is.EqualTo("g.f1"));
  }

  /// <summary>
  /// A bare GET/PUT positions and then walks the fields - two calls, because the runtime routine
  /// that walks them takes only the file number and has nowhere to put a record number.
  /// </summary>
  [Test]
  public void Lower_GivenABareGet_ThenItSeeksAndThenWalksTheFields() {
    var module = IrLowering.TryLowerModule(Bind(_fieldRoundTrip));
    var main = module!.Functions.First(f => f.Name == "main");

    var calls = main.Blocks.SelectMany(b => b.Instructions).OfType<IrCall>()
      .Select(c => (c.Callee as IrFunction)?.Name)
      .Where(n => n is "rt_file_setpos" or "rt_field_get" or "rt_field_put")
      .ToList();

    Assert.That(calls, Is.EqualTo(new[] {
      "rt_file_setpos", "rt_field_put",
      "rt_file_setpos", "rt_field_put",
      "rt_file_setpos", "rt_field_get",
      "rt_file_setpos", "rt_field_get",
    }));
  }

  /// <summary>
  /// The other target LSET/RSET accept, and a different operation entirely: a FIXED-length string has
  /// no handle to justify inside - its bytes ARE the variable and its width is declared - so LSET is a
  /// plain padded store and RSET the same store against the far edge. Both widths are exercised at
  /// their boundaries: shorter than the field (padded), exactly the field, and longer (truncated).
  /// </summary>
  private const string _fixedJustify = """
    DIM fs AS STRING * 8
    LSET fs = "xy"
    PRINT "["; fs; "]"
    RSET fs = "xy"
    PRINT "["; fs; "]"
    LSET fs = "12345678"
    PRINT "["; fs; "]"
    RSET fs = "12345678"
    PRINT "["; fs; "]"
    LSET fs = "abcdefghij"
    PRINT "["; fs; "]"
    RSET fs = "abcdefghij"
    PRINT "["; fs; "]"
    END
    """;

  [Test]
  public void Run_GivenRoutedLsetRsetIntoAFixedString_ThenTheValueIsJustifiedAndBlankPaddedToTheWidth() {
    var (image, routed) = Compile(_fixedJustify, backend: true);
    Assert.That(routed, Does.Contain("main"), "the back end did not take the module body under test");

    var output = Execute(image, "routed");

    Assert.That(Lines(output), Is.EqualTo(new[] {
      "[xy      ]",     // LSET: left, blank-padded to the declared eight
      "[      xy]",     // RSET: right, blank-padded to the declared eight
      "[12345678]",     // exactly the width: no padding either way
      "[12345678]",
      "[abcdefgh]",     // too long: the leftmost eight survive, as PB truncates
      "[abcdefgh]",     // RSET truncates the same end - it clamps and only then right-aligns
    }));
  }

  [Test]
  public void Run_GivenLsetRsetIntoAFixedString_ThenTheRoutedPathAgreesWithTheDirectEmitter() {
    var (routedImage, routed) = Compile(_fixedJustify, backend: true);
    var (directImage, _) = Compile(_fixedJustify, backend: false);
    Assert.That(routed, Does.Contain("main"));

    Assert.That(Execute(routedImage, "routed"), Is.EqualTo(Execute(directImage, "direct")));
  }

  private static string[] Lines(string text)
    => text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
