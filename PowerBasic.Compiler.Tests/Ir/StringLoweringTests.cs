using System.Diagnostics;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Basic string-variable support: assignment, concatenation, PRINT via the runtime-handle ABI.</summary>
[TestFixture]
public sealed class StringLoweringTests {

  private static IrModule? LowerOptimized(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
    if (module is not null)
      IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  [Test]
  public void StringAssignAndPrint_UsesHandleRuntimeCalls() {
    var module = LowerOptimized("a$ = \"hi\"\nPRINT a$\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("call ptr @rt_str_const(ptr @.str0, i32 2)"));
    Assert.That(text, Does.Contain("call void @rt_print_strvar(ptr"));
  }

  [Test]
  public void StringConcatenation_LowersToRuntimeConcat() {
    var module = LowerOptimized("a$ = \"Hello, \"\nb$ = \"world!\"\nc$ = a$ & b$\nPRINT c$\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("call ptr @rt_str_concat(ptr"));
  }

  [Test]
  public void StringLength_LowersToRuntimeLen() {
    var module = LowerOptimized("a$ = \"apple\"\nn% = LEN(a$)\nPRINT n%\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("call i32 @rt_str_len(ptr"));
  }

  [Test]
  public void StringComparison_LowersToRuntimeCompare() {
    var module = LowerOptimized("a$ = \"x\"\nIF a$ = \"x\" THEN\n PRINT \"yes\"\nEND IF\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    // the equality entry rather than the three-way one: the answer is only tested against zero, so
    // Ir.Passes.StringCompareEquality routes it to the routine that decides unequal lengths without
    // reading a byte. An ordering comparison keeps rt_str_compare (SelectLoweringTests pins that).
    Assert.That(text, Does.Contain("call i32 @rt_str_compare_eq(ptr"));
    Assert.That(text, Does.Contain("icmp eq i32"));
  }

  [Test]
  public void StringFunctions_LowerToRuntimeCalls() {
    var module = LowerOptimized("a$ = \"Hello, world!\"\nb$ = LEFT$(a$, 5)\nc$ = MID$(a$, 8, 5)\nd$ = CHR$(33)\nPRINT b$ & c$ & d$\nn% = ASC(a$)\nPRINT n%\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("@rt_str_left(ptr"));
    Assert.That(text, Does.Contain("@rt_str_mid(ptr"));
    Assert.That(text, Does.Contain("@rt_str_chr(i32"));
    Assert.That(text, Does.Contain("@rt_str_asc(ptr"));
  }

  [Test]
  public void StrValStringSpace_LowerToRuntimeCalls() {
    var module = LowerOptimized("n% = 42\ns$ = STR$(n%)\nv% = VAL(\"123\")\np$ = SPACE$(3)\nr$ = STRING$(5, 42)\nPRINT s$ & p$ & r$\nPRINT v%\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("@rt_str_from_i16(i16"));
    Assert.That(text, Does.Contain("@rt_str_val(ptr"));
    Assert.That(text, Does.Contain("@rt_str_space(i32"));
    Assert.That(text, Does.Contain("@rt_str_string(i32"));
  }

  [Test]
  public void CaseAndTrimFunctions_LowerToRuntimeCalls() {
    var module = LowerOptimized("a$ = \"  Hi  \"\nb$ = UCASE$(a$)\nc$ = LCASE$(a$)\nd$ = LTRIM$(a$)\ne$ = RTRIM$(a$)\nPRINT b$ & c$ & d$ & e$\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("@rt_str_ucase(ptr"));
    Assert.That(text, Does.Contain("@rt_str_lcase(ptr"));
    Assert.That(text, Does.Contain("@rt_str_ltrim(ptr"));
    Assert.That(text, Does.Contain("@rt_str_rtrim(ptr"));
  }

  [Test]
  public void Instr_LowersToRuntimeSearch() {
    var module = LowerOptimized("a$ = \"hello world\"\np% = INSTR(a$, \"o\")\nq% = INSTR(5, a$, \"o\")\nPRINT p% + q%\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("@rt_str_instr(ptr"));
    Assert.That(text, Does.Contain("@rt_str_instr_start(i32"));
  }

  [Test]
  public void BinaryRecordConversions_LowerToRuntimeCalls() {
    var module = LowerOptimized("n% = 42\nl& = 100000\nd# = 3.14\nr$ = MKI$(n%) & MKL$(l&) & MKD$(d#)\nx% = CVI(MKI$(n%))\ny& = CVL(MKL$(l&))\nz# = CVD(MKD$(d#))\nPRINT r$\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("@rt_str_mki(i16"));
    Assert.That(text, Does.Contain("@rt_str_mkl(i32"));
    Assert.That(text, Does.Contain("@rt_str_mkd(double") .Or.Contain("@rt_str_mkd(f64"));
    Assert.That(text, Does.Contain("@rt_str_cvi(ptr"));
    Assert.That(text, Does.Contain("@rt_str_cvl(ptr"));
    Assert.That(text, Does.Contain("@rt_str_cvd(ptr"));
  }

  [Test]
  public void FixedLengthString_AllocatesInlineBufferAndConvertsAtTheBoundary() {
    var module = LowerOptimized("DIM s AS STRING * 8\ns = \"hi\"\nt$ = s & \"!\"\nPRINT t$\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("alloca i8, i32 8"));          // the inline fixed buffer
    Assert.That(text, Does.Contain("@rt_str_to_fixed(ptr"));      // assignment pads/truncates into it
    Assert.That(text, Does.Contain("@rt_str_from_fixed(ptr"));    // reading it yields a handle
  }

  [Test]
  public void MidStatement_ReplacesSubstringInPlace() {
    var module = LowerOptimized("a$ = \"hello\"\nMID$(a$, 2, 3) = \"ELL\"\nMID$(a$, 1) = \"H\"\nPRINT a$\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("@rt_str_mid_assign(ptr"));   // replace into the buffer, store the new handle back
  }

  [Test]
  public void HexAndOct_LowerToRuntimeFormatters() {
    var module = LowerOptimized("n% = 255\nPRINT HEX$(n%)\nPRINT OCT$(n%)\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("@rt_str_hex(i32"));
    Assert.That(text, Does.Contain("@rt_str_oct(i32"));
  }

  [Test]
  public void IdenticalStringLiterals_AreInternedToOneGlobal() {
    var module = LowerOptimized("a$ = \"apple\"\nIF a$ = \"apple\" THEN PRINT \"apple\"\nEND");

    Assert.That(module, Is.Not.Null);
    var constants = System.Text.RegularExpressions.Regex.Matches(LlvmEmitter.Emit(module!), "private constant").Count;
    Assert.That(constants, Is.EqualTo(1));   // the three "apple" literals share one global
  }

  [Test]
  public void StringProgram_CompilesToNativeViaLlc() {
    var module = LowerOptimized("a$ = \"Hello, \"\nb$ = \"world!\"\nPRINT a$ & b$\nEND");
    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);

    try {
      using var probe = Process.Start(new ProcessStartInfo("llc", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
      probe!.WaitForExit();
    } catch {
      Assert.Ignore("llc not available");
    }

    var ll = LlvmEmitter.Emit(module!, "x86_64-unknown-linux-gnu");
    using var p = Process.Start(new ProcessStartInfo("llc", "-filetype=obj -o /dev/null -") { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
    p.StandardInput.Write(ll);
    p.StandardInput.Close();
    var err = p.StandardError.ReadToEnd();
    p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    Assert.That(p.ExitCode, Is.EqualTo(0), $"llc rejected the string module:\n{err}\n--- IR ---\n{ll}");
  }
}
