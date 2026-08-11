using System.Diagnostics;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// DATA / READ / RESTORE lowering: every DATA item program-wide is packed into one
/// length-prefixed module blob; a module-global i32 cursor walks it, numeric items are
/// parsed via the string runtime and string items are stored as handles.
/// </summary>
[TestFixture]
public sealed class DataReadLoweringTests {

  private static IrModule? LowerModule(string source, bool optimize = true) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
    if (module is not null && optimize)
      IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  [Test]
  public void NumericRead_PacksBlobAndCursorAndParsesViaRuntime() {
    var module = LowerModule("READ a%\nREAD b%\nx% = a% + b%\nEND\nDATA 10, 20", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    Assert.That(module!.FindGlobal(".data"), Is.Not.Null);
    Assert.That(module.FindGlobal(".data_cursor"), Is.Not.Null);
    var text = LlvmEmitter.Emit(module);
    Assert.That(text, Does.Contain("@.data_cursor = global i32 zeroinitializer"));
    Assert.That(text, Does.Contain("call f64 @rt_str_val(ptr").Or.Contain("call double @rt_str_val(ptr"));
  }

  [Test]
  public void StringRead_StoresHandleFromBlob() {
    var module = LowerModule("READ a$\nPRINT a$\nEND\nDATA hello", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    // rt_str_from_fixed, not rt_str_const: a DATA item is n bytes at an OFFSET into the pool, where a
    // constant is a whole pooled literal named by its global. Same routine underneath, and only the
    // constant form can be reached by naming a global, which is why the two are spelled apart
    Assert.That(text, Does.Contain("call ptr @rt_str_from_fixed(ptr"));   // build a handle from the blob bytes
    Assert.That(text, Does.Contain("call void @rt_print_strvar(ptr"));
  }

  [Test]
  public void BlobLengthPrefix_EncodesEachItem() {
    var module = LowerModule("READ a$\nEND\nDATA hi", optimize: false);

    Assert.That(module, Is.Not.Null);
    var blob = module!.FindGlobal(".data");
    Assert.That(blob!.Bytes, Is.Not.Null);
    Assert.That(blob.Bytes!.Length, Is.EqualTo(4));      // 2-byte length + "hi"
    Assert.That(blob.Bytes[0], Is.EqualTo(2));
    Assert.That(blob.Bytes[1], Is.EqualTo(0));
    Assert.That(blob.Bytes[2], Is.EqualTo((byte)'h'));
    Assert.That(blob.Bytes[3], Is.EqualTo((byte)'i'));
  }

  [Test]
  public void Restore_ResetsCursorToZero() {
    var module = LowerModule("READ a%\nRESTORE\nREAD b%\nx% = a% + b%\nEND\nDATA 7", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var main = module!.Functions.First(f => f.Name == "main");
    Assert.That(main.AllInstructions.OfType<IrStore>().Any(s => s.Pointer == module.FindGlobal(".data_cursor") && s.Value is IrConstantInt { Value: 0 }), Is.True);
  }

  [Test]
  public void RestoreToLabel_RewindsToThatLabelsOffset() {
    var module = LowerModule("READ a%\nRESTORE second\nREAD b%\nx% = a% + b%\nEND\nDATA 1\nsecond:\nDATA 2", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var main = module!.Functions.First(f => f.Name == "main");
    // "DATA 1" packs as 3 bytes (len 1 + '1'), so the 'second' label offset is 3
    Assert.That(main.AllInstructions.OfType<IrStore>().Any(s => s.Pointer == module.FindGlobal(".data_cursor") && s.Value is IrConstantInt { Value: 3 }), Is.True);
  }

  [Test]
  public void Pipeline_DataProgram_IsAcceptedByLlvm() {
    var module = LowerModule("READ n%\nREAD s$\nPRINT s$\nPRINT n%\nEND\nDATA 42, answer");
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
    Assert.That(p.ExitCode, Is.EqualTo(0), $"llvm-as rejected the DATA module:\n{err}\n--- IR ---\n{ll}");
  }
}
