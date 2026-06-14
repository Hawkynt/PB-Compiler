using System.Diagnostics;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// The strict LLVM text emitter. The snapshot tests pin the spelling; the toolchain
/// tests feed the emitted module to the real LLVM tools (llvm-as / llc), proving the
/// IR is valid LLVM and can be lowered to a native non-16-bit-DOS target.
/// </summary>
[TestFixture]
public sealed class LlvmEmitterTests {

  private static IrModule LowerOptimizeToModule(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var fn = IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35))!;
    IrPassManager.Standard().RunToFixpoint(fn);
    var module = new IrModule("T");
    module.AddFunction(fn);
    return module;
  }

  [Test]
  public void Emit_AddFunction_UsesLlvmSpelling() {
    var a = new IrArgument(IrType.I32, 0, "a");
    var b = new IrArgument(IrType.I32, 1, "b");
    var fn = new IrFunction("add", IrType.I32, [a, b]);
    var builder = new IrBuilder(fn.CreateBlock("entry"));
    var sum = builder.Add(a, b);
    sum.Name = "r";
    builder.Ret(sum);

    Assert.That(LlvmEmitter.Emit(fn), Is.EqualTo(
      "define i32 @add(i32 %a, i32 %b) {\n" +
      "entry:\n" +
      "  %r = add i32 %a, %b\n" +
      "  ret i32 %r\n" +
      "}\n"));
  }

  [Test]
  public void Emit_FloatTypes_UseLlvmNames() {
    var fn = new IrFunction("f", IrType.F64, [new IrArgument(IrType.F32, 0, "x")]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var wide = b.Cast(IrCastOp.FPExt, fn.Parameters[0], IrType.F64);
    b.Ret(wide);

    var text = LlvmEmitter.Emit(fn);
    Assert.That(text, Does.Contain("define double @f(float %x)"));
    Assert.That(text, Does.Contain("fpext float %x to double"));
  }

  [Test]
  public void Emit_Gep_UsesGetElementPtrI8Form() {
    var p = new IrArgument(IrType.Ptr, 0, "p");
    var fn = new IrFunction("g", IrType.Ptr, [p]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    b.Ret(b.Gep(p, IrBuilder.ConstI32(4)));

    Assert.That(LlvmEmitter.Emit(fn), Does.Contain("getelementptr i8, ptr %p, i32 4"));
  }

  [Test]
  public void LlvmAs_AcceptsTheEmittedModule() {
    RequireTool("llvm-as");
    var module = LowerOptimizeToModule(
      "s% = 0\nFOR i% = 1 TO 10\n  s% = s% + i%\nNEXT i%\nIF s% > 5 THEN\n  s% = s% - 1\nEND IF");
    var ll = LlvmEmitter.Emit(module, "x86_64-unknown-linux-gnu");

    var (code, err) = Run("llvm-as", "-o /dev/null -", ll);

    Assert.That(code, Is.EqualTo(0), $"llvm-as rejected the module:\n{err}\n--- IR ---\n{ll}");
  }

  [Test]
  public void Llc_LowersTheEmittedModuleToNativeX8664() {
    RequireTool("llc");
    var module = LowerOptimizeToModule("a% = 7\nb% = 3\nc% = a% * b% + 1");
    var ll = LlvmEmitter.Emit(module, "x86_64-unknown-linux-gnu");

    var (code, err) = Run("llc", "-filetype=asm -o /dev/null -", ll);

    Assert.That(code, Is.EqualTo(0), $"llc could not lower the module to x86-64:\n{err}\n--- IR ---\n{ll}");
  }

  // ---- process helpers -----------------------------------------------------

  private static void RequireTool(string tool) {
    try {
      using var p = Process.Start(new ProcessStartInfo(tool, "--version") {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
      });
      p!.WaitForExit();
    } catch {
      Assert.Ignore($"{tool} not available in this environment");
    }
  }

  private static (int Code, string Err) Run(string tool, string args, string stdin) {
    using var p = Process.Start(new ProcessStartInfo(tool, args) {
      RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
    })!;
    p.StandardInput.Write(stdin);
    p.StandardInput.Close();
    var err = p.StandardError.ReadToEnd();
    p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, err);
  }
}
