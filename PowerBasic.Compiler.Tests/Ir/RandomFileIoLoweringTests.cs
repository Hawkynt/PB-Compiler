using System.Diagnostics;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// Random / binary record I/O: OPEN ... FOR RANDOM/BINARY carries a record length, and
/// GET/PUT of a fixed-size scalar variable read/write that many bytes at the record
/// through the file runtime (the FIELD-buffer form is declined).
/// </summary>
[TestFixture]
public sealed class RandomFileIoLoweringTests {

  private static IrModule? LowerModule(string source, bool optimize = true) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
    if (module is not null && optimize)
      IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  [Test]
  public void RandomPutGet_RoundTripsAScalarRecord() {
    var module = LowerModule("OPEN \"d.dat\" FOR RANDOM AS #1 LEN = 2\nx% = 42\nPUT #1, 1, x%\nGET #1, 2, y%\nCLOSE #1\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("@rt_file_put(i32"));
    Assert.That(text, Does.Contain("@rt_file_get(i32"));
  }

  [Test]
  public void Open_CarriesRecordLength() {
    var module = LowerModule("OPEN \"d.dat\" FOR RANDOM AS #1 LEN = 4\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var main = module!.Functions.First(f => f.Name == "main");
    var open = main.AllInstructions.OfType<IrCall>().First(c => c.Callee is IrFunction { Name: "rt_file_open" });
    var openArgs = open.Args.ToList();
    Assert.That(openArgs.Count, Is.EqualTo(4));                                   // fileno, name, mode, reclen
    Assert.That(openArgs[3].Type, Is.EqualTo(IrType.I32));                        // the record length is the 4th argument
  }

  [Test]
  public void BinaryGet_UsesByteCountOfTheTargetType() {
    var module = LowerModule("OPEN \"d.dat\" FOR BINARY AS #1\nGET #1, 1, n&\nCLOSE #1\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var main = module!.Functions.First(f => f.Name == "main");
    var get = main.AllInstructions.OfType<IrCall>().First(c => c.Callee is IrFunction { Name: "rt_file_get" });
    Assert.That(get.Args.ToList()[3], Is.InstanceOf<IrConstantInt>().And.Property("Value").EqualTo(4L));   // LONG = 4 bytes
  }

  [Test]
  public void FieldBasedGetPut_Declines() {
    var module = LowerModule("OPEN \"d.dat\" FOR RANDOM AS #1 LEN = 2\nGET #1, 1\nEND", optimize: false);

    Assert.That(module, Is.Null);   // GET with no variable is the FIELD-buffer form, not modeled
  }

  [Test]
  public void Pipeline_RandomFileProgram_IsAcceptedByLlvm() {
    var module = LowerModule("OPEN \"d.dat\" FOR RANDOM AS #1 LEN = 2\nFOR i% = 1 TO 5\n  PUT #1, i%, i%\nNEXT i%\nGET #1, 3, r%\nCLOSE #1\nEND");
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
    Assert.That(p.ExitCode, Is.EqualTo(0), $"llvm-as rejected the random-file module:\n{err}\n--- IR ---\n{ll}");
  }
}
