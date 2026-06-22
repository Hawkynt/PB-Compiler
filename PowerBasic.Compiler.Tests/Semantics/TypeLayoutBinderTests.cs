using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// pb36 TYPE layout control: <c>PACKED</c> (the byte-packed default), <c>ALIGN n</c> (each field on an
/// n-byte boundary capped at its natural alignment, total rounded to n), <c>SIZE n</c> (fixed total),
/// and per-field <c>AS T AT offset</c> (explicit placement, gaps/overlap allowed). pb36-only - genuine
/// PBC always byte-packs, so these are verified by binder layout assertions + execution, not the oracle.
/// </summary>
[TestFixture]
public sealed class TypeLayoutBinderTests {

  private static UdtType Udt(string source, string name) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    return model.Udts[name];
  }

  [Test]
  public void Define_GivenDefaultType_ThenBytePacked() {
    var udt = Udt("TYPE T\n a AS BYTE\n b AS LONG\n c AS INTEGER\nEND TYPE\nDIM x AS T\n", "T");
    Assert.Multiple(() => {
      Assert.That(udt.FindField("a")!.Offset, Is.EqualTo(0));
      Assert.That(udt.FindField("b")!.Offset, Is.EqualTo(1), "no padding before the LONG");
      Assert.That(udt.FindField("c")!.Offset, Is.EqualTo(5));
      Assert.That(udt.Size, Is.EqualTo(7));
    });
  }

  [Test]
  public void Define_GivenAlign4_ThenFieldsAndTotalAligned() {
    var udt = Udt("TYPE T ALIGN 4\n a AS BYTE\n b AS LONG\n c AS INTEGER\nEND TYPE\nDIM x AS T\n", "T");
    Assert.Multiple(() => {
      Assert.That(udt.FindField("a")!.Offset, Is.EqualTo(0));
      Assert.That(udt.FindField("b")!.Offset, Is.EqualTo(4), "the LONG aligns to a 4-byte boundary");
      Assert.That(udt.FindField("c")!.Offset, Is.EqualTo(8));
      Assert.That(udt.Size, Is.EqualTo(12), "total padded to a multiple of 4");
    });
  }

  [Test]
  public void Define_GivenAlignCapsAtNaturalAlignment_ThenByteFieldsStayContiguous() {
    // ALIGN 4 must NOT push a 1-byte field to a 4-byte boundary - alignment is capped at the field's natural size
    var udt = Udt("TYPE T ALIGN 4\n a AS BYTE\n b AS BYTE\n c AS BYTE\nEND TYPE\nDIM x AS T\n", "T");
    Assert.Multiple(() => {
      Assert.That(udt.FindField("b")!.Offset, Is.EqualTo(1));
      Assert.That(udt.FindField("c")!.Offset, Is.EqualTo(2));
      Assert.That(udt.Size, Is.EqualTo(4), "but the total still rounds up to 4");
    });
  }

  [Test]
  public void Define_GivenPacked_ThenSameAsDefault() {
    var udt = Udt("TYPE T PACKED\n a AS BYTE\n b AS LONG\nEND TYPE\nDIM x AS T\n", "T");
    Assert.Multiple(() => {
      Assert.That(udt.FindField("b")!.Offset, Is.EqualTo(1));
      Assert.That(udt.Size, Is.EqualTo(5));
    });
  }

  [Test]
  public void Define_GivenSize_ThenPaddedToTotal() {
    var udt = Udt("TYPE T SIZE 16\n a AS INTEGER\n b AS INTEGER\nEND TYPE\nDIM x AS T\n", "T");
    Assert.That(udt.Size, Is.EqualTo(16));
  }

  [Test]
  public void Define_GivenExplicitFieldOffset_ThenPlacedWithGap() {
    var udt = Udt("TYPE T\n a AS INTEGER\n b AS LONG AT 8\nEND TYPE\nDIM x AS T\n", "T");
    Assert.Multiple(() => {
      Assert.That(udt.FindField("a")!.Offset, Is.EqualTo(0));
      Assert.That(udt.FindField("b")!.Offset, Is.EqualTo(8), "placed at the explicit offset, leaving a gap");
      Assert.That(udt.Size, Is.EqualTo(12), "size spans to the end of the highest field");
    });
  }

  [Test]
  public void Define_GivenOverlappingOffsets_ThenSizeIsHighestEnd() {
    var udt = Udt("TYPE T\n whole AS LONG\n lo AS INTEGER AT 0\n hi AS INTEGER AT 2\nEND TYPE\nDIM x AS T\n", "T");
    Assert.Multiple(() => {
      Assert.That(udt.FindField("lo")!.Offset, Is.EqualTo(0), "overlaps the LONG's low word");
      Assert.That(udt.FindField("hi")!.Offset, Is.EqualTo(2));
      Assert.That(udt.Size, Is.EqualTo(4), "the overlapping fields do not grow the type");
    });
  }

  [Test]
  public void Define_GivenSizeSmallerThanNatural_ThenError() {
    var unit = Parser.Parse(Lexer.Tokenize("TYPE T SIZE 2\n a AS LONG\nEND TYPE\n", "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Success, Is.False, "SIZE below the natural size is rejected");
  }

  [Test]
  public void Parse_GivenLayoutBelowPb36_ThenRejected() {
    Assert.Throws<ParserException>(() =>
      Parser.Parse(Lexer.Tokenize("TYPE T ALIGN 4\n a AS LONG\nEND TYPE\n", "t.bas", Dialect.Pb35), "t.bas", Dialect.Pb35));
  }

  [Test]
  public void Parse_GivenBadAlignValue_ThenRejected() {
    Assert.Throws<ParserException>(() =>
      Parser.Parse(Lexer.Tokenize("TYPE T ALIGN 3\n a AS LONG\nEND TYPE\n", "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36));
  }
}
