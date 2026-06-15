using System.Diagnostics;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// User-defined TYPE records: a UDT variable is a packed byte buffer, and member access
/// reads/writes the field's scalar type at its byte offset via a byte GEP.
/// </summary>
[TestFixture]
public sealed class UdtLoweringTests {

  private static IrModule? LowerModule(string source, bool optimize = true) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
    if (module is not null && optimize)
      IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  private const string Point = "TYPE Point\n  X AS INTEGER\n  Y AS INTEGER\nEND TYPE\n";

  [Test]
  public void UdtVariable_IsAPackedBufferWithFieldOffsets() {
    var module = LowerModule(Point + "DIM p AS Point\np.X = 3\np.Y = 4\ns% = p.X + p.Y\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("alloca i8, i32 4"));        // two INTEGERs = 4 bytes
    Assert.That(text, Does.Contain("getelementptr i8"));        // the Y field at offset 2
  }

  [Test]
  public void UdtFieldStore_WritesTheMappedScalarType() {
    var module = LowerModule(Point + "DIM p AS Point\np.X = 1234\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("store i16 1234"));
  }

  [Test]
  public void UdtFieldRead_VerifiesAfterOptimization() {
    var module = LowerModule(Point + "DIM p AS Point\np.X = 10\np.Y = 20\nx% = p.X * p.Y\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
  }

  [Test]
  public void MixedWidthFields_OffsetByDeclaredSizes() {
    var module = LowerModule("TYPE Rec\n  A AS INTEGER\n  B AS LONG\n  C AS INTEGER\nEND TYPE\nDIM r AS Rec\nr.A = 1\nr.B = 2\nr.C = 3\ny% = r.A + r.C\nz& = r.B\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("alloca i8, i32 8"));   // 2 + 4 + 2
  }

  [Test]
  public void WholeUdtAssignment_CopiesTheRecord() {
    var module = LowerModule(Point + "DIM a AS Point\nDIM b AS Point\na.X = 5\na.Y = 6\nb = a\ns% = b.X + b.Y\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("@llvm.memcpy.p0.p0.i32(ptr"));
  }

  [Test]
  public void WholeUdtComparison_UsesMemCompare() {
    var module = LowerModule(Point + "DIM a AS Point\nDIM b AS Point\nIF a = b THEN PRINT \"same\"\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("@rt_mem_compare(ptr"));
    Assert.That(text, Does.Contain("icmp eq i32"));
  }

  [Test]
  public void WholeRecordGetPut_UsesRecordSize() {
    var module = LowerModule(Point + "DIM p AS Point\nOPEN \"d.dat\" FOR RANDOM AS #1 LEN = 4\nPUT #1, 1, p\nGET #1, 2, p\nCLOSE #1\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var main = module!.Functions.First(f => f.Name == "main");
    var put = main.AllInstructions.OfType<IrCall>().First(c => c.Callee is IrFunction { Name: "rt_file_put" });
    Assert.That(put.Args.ToList()[3], Is.InstanceOf<IrConstantInt>().And.Property("Value").EqualTo(4L));   // the whole 4-byte record
  }

  [Test]
  public void StaticUdtArray_IndexesThenOffsetsTheField() {
    var module = LowerModule(Point + "DIM pts(1 TO 3) AS Point\npts(1).X = 10\npts(2).Y = 20\ns% = pts(1).X + pts(2).Y\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("alloca i8, i32 12"));   // 3 records * 4 bytes
    Assert.That(text, Does.Contain("getelementptr i8"));
  }

  [Test]
  public void StaticUdtArray_VerifiesAfterOptimization() {
    var module = LowerModule(Point + "DIM pts(0 TO 4) AS Point\nFOR i% = 0 TO 4\n  pts(i%).X = i%\n  pts(i%).Y = i% * 2\nNEXT i%\nx% = pts(3).X + pts(3).Y\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
  }

  [Test]
  public void UdtByRefParameter_PassesRecordPointer() {
    var module = LowerModule("TYPE Point\n  X AS INTEGER\n  Y AS INTEGER\nEND TYPE\nSUB Bump(p AS Point)\n  p.X = p.X + 1\nEND SUB\nDIM q AS Point\nq.X = 5\nCALL Bump(q)\ns% = q.X\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var bump = module!.Functions.First(f => f.Name.Equals("Bump", System.StringComparison.OrdinalIgnoreCase));
    Assert.That(bump.IsDeclaration, Is.False);                              // the body lowered (not declined)
    Assert.That(bump.Parameters.Single().Type, Is.EqualTo(IrType.Ptr));    // the record arrives as a pointer
    Assert.That(bump.AllInstructions.OfType<IrCall>().Any(), Is.False);    // direct field access through the pointer
  }

  [Test]
  public void FixedStringField_ConvertsAtTheRecordBoundary() {
    var module = LowerModule("TYPE Person\n  Name AS STRING * 20\n  Age AS INTEGER\nEND TYPE\nDIM p AS Person\np.Name = \"Ada\"\np.Age = 36\nn$ = p.Name\nPRINT n$\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("alloca i8, i32 22"));         // 20-byte name + 2-byte age
    Assert.That(text, Does.Contain("@rt_str_to_fixed(ptr"));      // p.Name = "Ada"
    Assert.That(text, Does.Contain("@rt_str_from_fixed(ptr"));    // reading p.Name
    Assert.That(text, Does.Contain("store i16 36"));              // the scalar field still works
  }

  [Test]
  public void Pipeline_UdtProgram_IsAcceptedByLlvm() {
    var module = LowerModule(Point + "DIM p AS Point\nDIM q AS Point\np.X = 6\np.Y = 7\nq = p\nPRINT q.X * q.Y\nEND");
    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);

    try {
      using var probe = Process.Start(new ProcessStartInfo("llvm-as", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
      probe!.WaitForExit();
    } catch {
      Assert.Ignore("llvm-as not available");
    }

    var ll = LlvmEmitter.Emit(module!, "x86_64-unknown-linux-gnu");
    using var p = Process.Start(new ProcessStartInfo("llvm-as", "-o /dev/null -") { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
    p.StandardInput.Write(ll);
    p.StandardInput.Close();
    var err = p.StandardError.ReadToEnd();
    p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    Assert.That(p.ExitCode, Is.EqualTo(0), $"llvm-as rejected the UDT module:\n{err}\n--- IR ---\n{ll}");
  }
}
