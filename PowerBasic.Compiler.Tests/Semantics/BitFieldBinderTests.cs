using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// pb36 bit-field members: <c>Flags AS BIT * 3</c> packs sub-WORD fields into a hidden <c>$bits</c>
/// WORD; reads desugar to <c>(word >> offset) AND mask</c> and writes to a read-modify-write that
/// preserves the neighbouring fields. No new codegen — pure binder desugar over WORD storage.
/// </summary>
[TestFixture]
public sealed class BitFieldBinderTests {

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    return model;
  }

  [Test]
  public void Bind_GivenBitFieldType_WhenBound_ThenFieldsPackDenselyIntoSmallestStorage() {
    var model = Bind("TYPE R\n  Mode AS BIT * 3\n  Enabled AS BIT\n  Level AS BIT * 4\nEND TYPE\nDIM r AS R\n");
    var udt = (UdtType)model.ModuleVariables.Values.Single(v => v.Name.Equals("r", System.StringComparison.OrdinalIgnoreCase)).Type;
    Assert.Multiple(() => {
      // the public bit-field names are not real storage fields
      Assert.That(udt.FindField("Mode"), Is.Null, "a bit-field is not a storage field");
      Assert.That(udt.Fields.Count, Is.EqualTo(1), "all three bit-fields (8 bits) pack into one container");
      Assert.That(udt.Fields.Single().Type, Is.EqualTo(PbType.Byte), "8 used bits pack densely into a BYTE, not a WORD");
    });
  }

  [Test]
  public void Bind_GivenBitFieldRead_WhenBound_ThenDesugarsToShiftAndMask() {
    var model = Bind("TYPE R\n  Mode AS BIT * 3\n  Level AS BIT * 4\nEND TYPE\nDIM r AS R\nDIM x&\nx& = r.Level\n");
    var read = model.Desugared.Keys.OfType<MemberExpr>().Single(m => m.Member.Equals("Level", System.StringComparison.OrdinalIgnoreCase));
    var lowered = (BinaryExpr)model.Desugared[read];
    Assert.Multiple(() => {
      Assert.That(lowered.Op, Is.EqualTo(BinaryOp.And), "the read masks to the field width");
      Assert.That(((IntegerLiteralExpr)lowered.Right).Value, Is.EqualTo(0xF), "4-bit field masks with &HF");
      Assert.That(lowered.Left, Is.InstanceOf<BinaryExpr>(), "offset 3 shifts the storage word right first");
    });
  }

  [Test]
  public void Bind_GivenBitFieldWrite_WhenBound_ThenReadModifyWritePreservesNeighbours() {
    var model = Bind("TYPE R\n  Mode AS BIT * 3\n  Level AS BIT * 4\nEND TYPE\nDIM r AS R\nr.Mode = 5\n");
    var write = model.DesugaredStatements.Keys.OfType<AssignStmt>().Single(a => a.Target is MemberExpr { Member: "Mode" });
    var store = (AssignStmt)model.DesugaredStatements[write];
    Assert.Multiple(() => {
      Assert.That(store.Target, Is.InstanceOf<MemberExpr>(), "the write targets the hidden storage word");
      var or = (BinaryExpr)store.Value;
      Assert.That(or.Op, Is.EqualTo(BinaryOp.Or), "cleared bits OR'd with the new value");
      var cleared = (BinaryExpr)or.Left;
      Assert.That(cleared.Op, Is.EqualTo(BinaryOp.And), "the existing word is cleared at the field position");
      Assert.That(((IntegerLiteralExpr)cleared.Right).Value, Is.EqualTo(~0x7L & 0xFF), "3-bit field at offset 0 clears with ~&H7 masked to its BYTE container");
    });
  }

  [Test]
  public void Bind_GivenBitFieldWidthOutOfRange_WhenParsed_ThenRejected() {
    Assert.Throws<ParserException>(() =>
      Parser.Parse(Lexer.Tokenize("TYPE R\n  X AS BIT * 17\nEND TYPE\n", "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36));
  }

  [Test]
  public void Bind_GivenBitFieldBelowPb36_WhenParsed_ThenRejected() {
    Assert.Throws<ParserException>(() =>
      Parser.Parse(Lexer.Tokenize("TYPE R\n  X AS BIT * 3\nEND TYPE\n", "t.bas", Dialect.Pb35), "t.bas", Dialect.Pb35));
  }
}
