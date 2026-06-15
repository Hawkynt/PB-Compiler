using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.CodeGen.Ssa;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 copy propagation (Pb36CopyProp): a copy y = x redirects reads of y to x and the
/// copy is dropped. The byte-identical contract is enforced by the differential harness;
/// these tests pin that the analysis fires (and declines unsound cases).
/// </summary>
[TestFixture]
public sealed class CopyPropTests {

  private static (SemanticModel Model, SsaForm Ssa) BuildSsa(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cfg = ControlFlowGraph.TryBuild(model.MainBody);
    Assert.That(cfg, Is.Not.Null, "expected a buildable CFG");
    var ssa = SsaForm.TryBuild(model, cfg!, null);
    Assert.That(ssa, Is.Not.Null, "expected a buildable SSA");
    return (model, ssa!);
  }

  [Test]
  public void Analyze_GivenSimpleCopy_RemovesItAndRedirectsTheRead() {
    var (_, ssa) = BuildSsa("a% = 5\nb% = a%\nc% = b% + 1");
    var (reads, deadCopies) = Pb36CopyProp.Analyze(ssa);

    Assert.That(deadCopies, Has.Count.EqualTo(1));      // the b% = a% copy is dropped
    Assert.That(reads, Has.Count.EqualTo(1));           // the read of b% in (b% + 1) is redirected
    Assert.That(reads.Values, Has.All.Property("Name").EqualTo("a"));   // ... to a%
  }

  [Test]
  public void Analyze_GivenCopyChain_RedirectsToTheRoot() {
    var (_, ssa) = BuildSsa("a% = 7\nb% = a%\nc% = b%\nd% = c% + 2");
    var (reads, deadCopies) = Pb36CopyProp.Analyze(ssa);

    Assert.That(deadCopies, Has.Count.EqualTo(2));      // both b% = a% and c% = b% drop
    Assert.That(reads.Values, Has.All.Property("Name").EqualTo("a"));   // the live read resolves to the root a%
  }

  [Test]
  public void Analyze_GivenReassignedSource_DeclinesTheCopy() {
    // a% is written twice, so its cell is not stable across b%'s live range: redirecting
    // the b% read to a% would read 2, not the copied 1 - the copy must stay
    var (_, ssa) = BuildSsa("a% = 1\nb% = a%\na% = 2\nc% = b% + a%");
    var (reads, deadCopies) = Pb36CopyProp.Analyze(ssa);

    Assert.That(deadCopies, Is.Empty);
    Assert.That(reads, Is.Empty);
  }
}
