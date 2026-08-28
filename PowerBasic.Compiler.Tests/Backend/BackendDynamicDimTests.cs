using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>DIM a(1 TO n)</c> with a bound that is not a compile-time constant, and no <c>REDIM</c> anywhere
/// - the array is dynamic and its ONLY allocation point is the declaration.
///
/// <para>
/// The IR lowering treated every <c>DIM</c> as a declaration that emits nothing, on the grounds that
/// storage is allocated "lazily on first use". For a static array that is true - the storage is laid
/// out at compile time - and for a dynamic one there was no lazy allocation and no later one either:
/// the descriptor's data cell stayed null and <c>a(1) = 7</c> stored through it. The x86-16 selector
/// declined the shape (<c>gep: non-register base</c>, since the null base folds to an immediate) so no
/// DOS program ever ran it, but <c>--emit-c</c> and <c>--emit-llvm</c> have no fallback and emitted the
/// null store.
/// </para>
///
/// <para>
/// The bound comes out of a FILE. Written as a literal the array is STATIC, which is a different
/// lowering entirely and allocates nothing on purpose; written into a variable it folds back to the
/// literal and the array is static again. A file is also the only stdin the test CPU has.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendDynamicDimTests {

  private const string _dimWithARuntimeBound = """
    OPEN "OUT.TXT" FOR OUTPUT AS #1
    PRINT #1, "4"
    CLOSE #1
    DIM n AS INTEGER
    OPEN "OUT.TXT" FOR INPUT AS #1
    INPUT #1, n
    CLOSE #1
    DIM a%(1 TO n)
    a%(1) = 7
    a%(2) = 11
    PRINT a%(1); a%(2)
    END
    """;

  /// <summary>The same array reached through a computed index, so nothing folds the access away.</summary>
  private const string _dimThenComputedIndex = """
    OPEN "OUT.TXT" FOR OUTPUT AS #1
    PRINT #1, "4"
    CLOSE #1
    DIM n AS INTEGER
    DIM i AS INTEGER
    OPEN "OUT.TXT" FOR INPUT AS #1
    INPUT #1, n
    CLOSE #1
    DIM a%(1 TO n)
    FOR i = 1 TO n
      a%(i) = i * 3
    NEXT i
    PRINT a%(1); a%(n)
    END
    """;

  /// <summary>
  /// The boundary on the other side: <c>DIM b%()</c> names an array and deliberately does NOT size
  /// it - the <c>REDIM</c> behind it is what allocates. Its bound list is EMPTY rather than absent,
  /// so a declaration-allocates rule that reads "has bounds" instead of "has bounds to allocate
  /// from" sizes it to nothing and takes the whole module's lowering down with a rank mismatch.
  /// </summary>
  private const string _dimWithoutBoundsThenRedim = """
    OPEN "OUT.TXT" FOR OUTPUT AS #1
    PRINT #1, "4"
    CLOSE #1
    DIM n AS INTEGER
    OPEN "OUT.TXT" FOR INPUT AS #1
    INPUT #1, n
    CLOSE #1
    DIM b%()
    REDIM b%(1 TO n)
    b%(1) = 5
    b%(2) = 6
    PRINT b%(1); b%(2)
    END
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static string Values(string output)
    => string.Join(" ", output.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

  [TestCase(_dimWithARuntimeBound, "7 11")]
  [TestCase(_dimThenComputedIndex, "3 12")]
  [TestCase(_dimWithoutBoundsThenRedim, "5 6")]
  public void Execute_GivenADimWithARuntimeBoundAndNoRedim_WhenRouted_ThenMainRoutesAndAgrees(
      string source, string expected) {
    foreach (var optimize in new[] { false, true }) {
      var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
      var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
      var directOutput = Cpu8086.Run(direct.EmitExecutable()).Output;
      var routedOutput = Cpu8086.Run(routed.EmitExecutable()).Output;

      Assert.Multiple(() => {
        Assert.That(direct.Errors, Is.Empty, "direct: " + string.Join("; ", direct.Errors));
        Assert.That(routed.Errors, Is.Empty, "routed: " + string.Join("; ", routed.Errors));
        Assert.That(routed.BackendRoutedNames, Does.Contain("main"),
          $"the module body did not route (optimize={optimize})");
        Assert.That(Values(routedOutput), Is.EqualTo(Values(directOutput)), $"optimize={optimize}");
        Assert.That(Values(routedOutput), Does.EndWith(expected), $"optimize={optimize}");
      });
    }
  }

  /// <summary>
  /// The middle-end half, which is the half the routed decline was hiding. The allocation call has to
  /// be there, and no element address may be computed off a null base - which is exactly what
  /// <c>getelementptr i8, ptr null, i32 2</c> was.
  /// </summary>
  [Test]
  public void Lower_GivenADimWithARuntimeBound_ThenItAllocatesRatherThanIndexingNull() {
    var module = IrLowering.TryLowerModule(Bind(_dimWithARuntimeBound))!;
    IrPassManager.Standard().RunOnModule(module);
    var llvm = LlvmEmitter.Emit(module);

    Assert.Multiple(() => {
      Assert.That(IrVerifier.Verify(module), Is.Empty);
      Assert.That(llvm, Does.Contain("@rt_arr_alloc(i32"));
      Assert.That(llvm, Does.Not.Contain("ptr null"));
      Assert.That(CEmitter.TryEmit(module, out var refused), Is.Not.Null, "C declined: " + refused);
    });
  }
}
