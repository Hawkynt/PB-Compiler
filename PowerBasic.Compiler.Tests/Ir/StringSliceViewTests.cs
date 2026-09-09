using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0297 read-only substring views in the IR middle end.</summary>
[TestFixture]
public sealed class StringSliceViewTests {

  [TestCase("LEFT$(s$, n%)", "rt_str_left")]
  [TestCase("RIGHT$(s$, n%)", "rt_str_right")]
  [TestCase("MID$(s$, i%, n%)", "rt_str_mid")]
  [TestCase("MID$(s$, i%)", "rt_str_mid2")]
  public void LenOfSlice_WhenOptimized_DoesNotMaterializeSubstring(string slice, string runtimeCall) {
    var module = LowerOptimized($"""
      DIM s$, i%, n%, result%
      LINE INPUT s$
      i% = LEN(s$)
      n% = i% + 1
      result% = LEN({slice})
      PRINT result%
      END
      """);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Not.Contain($"call ptr @{runtimeCall}"), "the slice allocation/copy must be gone");
    Assert.That(text, Does.Contain("call i32 @rt_str_len(ptr"), "the source handle is still consumed by LEN");
    Assert.That(text, Does.Contain("select i1"), "slice bounds are derived in SSA instead of by the string runtime");
  }

  [Test]
  public void LenOfMid_WhenOptimized_UsesSignedClampPredicatesForPowerBasicBounds() {
    var module = LowerOptimized("""
      DIM s$, i%, n%, result%
      LINE INPUT s$
      i% = LEN(s$)
      n% = i% - 1
      result% = LEN(MID$(s$, i%, n%))
      PRINT result%
      END
      """);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Not.Contain("call ptr @rt_str_mid"));
    Assert.That(text, Does.Contain("icmp slt i32"), "MID$ start values below one clamp to one");
    Assert.That(text, Does.Contain("icmp sle i32"), "empty suffixes and non-positive counts clamp to zero");
    Assert.That(text, Does.Contain("icmp sgt i32"), "requested length is clipped to the remaining suffix");
  }

  [Test]
  public void EqualityOfSlice_WhenOptimized_ComposesWithLengthGuardAndDoesNotMaterializeSubstring() {
    var module = LowerOptimized("""
      DIM s$, t$, i%, n%, result%
      LINE INPUT s$
      LINE INPUT t$
      i% = 2
      n% = 3
      IF MID$(s$, i%, n%) = t$ THEN result% = 1
      PRINT result%
      END
      """);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Not.Contain("call ptr @rt_str_mid"), "MID$ must remain a range, not a copied string");
    Assert.That(text, Does.Contain("call i32 @rt_str_compare_eq_view(ptr"),
      "O0298 must classify equality before O0297 fuses the view consumer");
    Assert.That(text, Does.Not.Contain("call i32 @rt_str_compare_view(ptr"),
      "equality must not fall back to the three-way view comparator");
  }

  [Test]
  public void OrderingOfSlice_WhenOptimized_UsesThreeWayViewComparison() {
    var module = LowerOptimized("""
      DIM s$, t$, i%, n%, result%
      LINE INPUT s$
      LINE INPUT t$
      i% = 2
      n% = 3
      IF MID$(s$, i%, n%) < t$ THEN result% = 1
      PRINT result%
      END
      """);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Not.Contain("call ptr @rt_str_mid"));
    Assert.That(text, Does.Contain("call i32 @rt_str_compare_view(ptr"));
    Assert.That(text, Does.Not.Contain("call i32 @rt_str_compare_eq_view(ptr"));
  }

  [Test]
  public void CompareTwoSlices_WhenOptimized_EliminatesBothSubstringCopies() {
    var module = LowerOptimized("""
      DIM s$, t$, i%, n%, result%
      LINE INPUT s$
      LINE INPUT t$
      i% = 2
      n% = 3
      IF MID$(s$, i%, n%) < RIGHT$(t$, n%) THEN result% = 1
      PRINT result%
      END
      """);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Not.Contain("call ptr @rt_str_mid"));
    Assert.That(text, Does.Not.Contain("call ptr @rt_str_right"));
    Assert.That(text, Does.Contain("call i32 @rt_str_compare_view(ptr"));
  }

  [Test]
  public void PrintSlice_WhenOptimized_BorrowsSourceWithoutSubstringOrDupAllocation() {
    var module = LowerOptimized("""
      DIM s$, n%
      LINE INPUT s$
      n% = 3
      PRINT MID$(s$, 2, n%)
      END
      """);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Not.Contain("call ptr @rt_str_mid"));
    Assert.That(text, Does.Not.Contain("call ptr @rt_str_dup"),
      "the variable borrow must be cancelled rather than replaced by another owned copy");
    Assert.That(text, Does.Contain("call void @rt_print_strview(ptr"));
    Assert.That(text, Does.Contain("call i32 @rt_str_len_borrow(ptr"));
  }

  [Test]
  public void FilePrintSlice_WhenOptimized_UsesFileViewConsumer() {
    var module = LowerOptimized("""
      DIM s$, n%
      LINE INPUT s$
      n% = 3
      OPEN "O.TXT" FOR OUTPUT AS #1
      PRINT #1, RIGHT$(s$, n%)
      CLOSE #1
      END
      """);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Not.Contain("call ptr @rt_str_right"));
    Assert.That(text, Does.Contain("call void @rt_fprint_strview(i32"));
  }

  [Test]
  public void SliceWithOpaqueArgumentCall_WhenOptimized_RemainsMaterialized() {
    var module = LowerOptimized("""
      DECLARE FUNCTION Pick%(BYVAL value%)
      DIM s$
      LINE INPUT s$
      PRINT MID$(s$, Pick%(2), 2)
      END

      FUNCTION Pick%(BYVAL value%) NOINLINE
        Pick% = value%
      END FUNCTION
      """);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("call ptr @rt_str_mid"),
      "a user call after the borrow can mutate the base, so the original copied snapshot must survive");
    Assert.That(text, Does.Not.Contain("call void @rt_print_strview(ptr"));
  }

  private static IrModule? LowerOptimized(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
    if (module is not null)
      IrPassManager.Standard().RunOnModule(module);
    return module;
  }
}
