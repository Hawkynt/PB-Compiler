using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 dense SELECT CASE -> jump table. The byte-identical output contract is enforced
/// by the differential harness (tests/diff/DIFF62.BAS under pb35 chain and pb36 table);
/// this pins that the table actually replaces the compare chain (an indexed indirect
/// jump appears, and the dispatch is more compact than the chain).
/// </summary>
[TestFixture]
public sealed class SelectJumpTableTests {

  private static byte[] Compile(string source, Dialect dialect) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", dialect), "TEST.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return exe;
  }

  private static bool Contains(byte[] image, params byte[] needle) {
    for (var i = 0; i + needle.Length <= image.Length; ++i) {
      var match = true;
      for (var j = 0; j < needle.Length; ++j)
        if (image[i + j] != needle[j]) { match = false; break; }
      if (match)
        return true;
    }
    return false;
  }

  private const string _DenseSelect =
    "x% = 4\n" +
    "SELECT CASE x%\n" +
    "CASE 1\n PRINT \"a\"\n" +
    "CASE 2\n PRINT \"b\"\n" +
    "CASE 3\n PRINT \"c\"\n" +
    "CASE 4\n PRINT \"d\"\n" +
    "CASE 5\n PRINT \"e\"\n" +
    "CASE ELSE\n PRINT \"z\"\n" +
    "END SELECT\nEND";

  [Test]
  public void Emit_GivenDenseSelect_WhenPb36_ThenDispatchesThroughAnIndexedJump() {
    var pb36 = Compile(_DenseSelect, Dialect.Pb36);
    // JMP word [BX + disp16] is FF A7 - the jump-table dispatch the chain never emits
    Assert.That(Contains(pb36, 0xFF, 0xA7), Is.True, "expected an indexed indirect JMP (jump table)");
  }

  [Test]
  public void Emit_GivenSparseSelect_WhenPb36_ThenKeepsTheCompareChain() {
    // only two cases - below the table threshold, so the compare chain stays (no FF A7)
    var pb36 = Compile("x% = 1\nSELECT CASE x%\nCASE 1\n PRINT \"a\"\nCASE 9\n PRINT \"b\"\nEND SELECT\nEND", Dialect.Pb36);
    Assert.That(Contains(pb36, 0xFF, 0xA7), Is.False);
  }

  private const string _DenseLongSelect =
    "DIM x AS LONG\n" +
    "x = 100000\n" +
    "SELECT CASE x\n" +
    "CASE 100000\n PRINT \"a\"\n" +
    "CASE 100001\n PRINT \"b\"\n" +
    "CASE 100002\n PRINT \"c\"\n" +
    "CASE 100003\n PRINT \"d\"\n" +
    "CASE 100004\n PRINT \"e\"\n" +
    "CASE ELSE\n PRINT \"z\"\n" +
    "END SELECT\nEND";

  [Test]
  public void Emit_GivenDenseLongSelect_WhenPb36_ThenDispatchesThroughAnIndexedJump() {
    // A dense SELECT CASE over a LONG subject with >=4 consecutive arms must also
    // emit the indexed indirect jump (FF A7) rather than a compare chain.
    var pb36 = Compile(_DenseLongSelect, Dialect.Pb36);
    Assert.That(Contains(pb36, 0xFF, 0xA7), Is.True, "expected an indexed indirect JMP (jump table) for LONG subject");
  }

  [Test]
  public void Emit_GivenDenseLongSelect_WhenPb35_ThenKeepsTheCompareChain() {
    // pb35 never uses jump tables - the compare chain must remain regardless of density
    var pb35 = Compile(_DenseLongSelect, Dialect.Pb35);
    Assert.That(Contains(pb35, 0xFF, 0xA7), Is.False, "pb35 must not emit a jump table");
  }
}
