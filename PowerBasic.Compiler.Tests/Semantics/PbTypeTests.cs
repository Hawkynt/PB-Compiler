using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.Tests.Semantics;

[TestFixture]
public sealed class PbTypeTests {

  [TestCase(ScalarKind.Byte, 1)]
  [TestCase(ScalarKind.Word, 2)]
  [TestCase(ScalarKind.Dword, 4)]
  [TestCase(ScalarKind.Integer, 2)]
  [TestCase(ScalarKind.Long, 4)]
  [TestCase(ScalarKind.Single, 4)]
  [TestCase(ScalarKind.Double, 8)]
  [TestCase(ScalarKind.Ext, 10)]
  public void Size_GivenScalar_WhenQueried_ThenMatchesPb35TargetSize(ScalarKind kind, int expected) {
    ScalarType t = kind switch {
      ScalarKind.Byte => PbType.Byte,
      ScalarKind.Word => PbType.Word,
      ScalarKind.Dword => PbType.Dword,
      ScalarKind.Integer => PbType.Integer,
      ScalarKind.Long => PbType.Long,
      ScalarKind.Single => PbType.Single,
      ScalarKind.Double => PbType.Double,
      _ => PbType.Ext,
    };
    Assert.That(t.Size, Is.EqualTo(expected));
  }

  [Test]
  public void Size_GivenFixedString_WhenQueried_ThenInlineLength() {
    Assert.That(new FixedStringType(12).Size, Is.EqualTo(12));
  }

  [Test]
  public void Size_GivenPackedUdt_WhenQueried_ThenSumOfFieldsWithoutPadding() {
    // TYPE t: b AS BYTE: l AS LONG: w AS WORD - PB packs: 1 + 4 + 2 = 7
    var t = new UdtType("t", [
      new("b", PbType.Byte, 0),
      new("l", PbType.Long, 1),
      new("w", PbType.Word, 5),
    ], IsUnion: false);
    Assert.That(t.Size, Is.EqualTo(7));
  }

  [Test]
  public void Size_GivenUnion_WhenQueried_ThenLargestField() {
    var u = new UdtType("u", [
      new("b", PbType.Byte, 0),
      new("d", PbType.Double, 0),
    ], IsUnion: true);
    Assert.That(u.Size, Is.EqualTo(8));
  }

  [Test]
  public void Size_GivenUdtWithFieldArray_WhenQueried_ThenElementCountCounted() {
    // field(1 TO 4) AS WORD inside a TYPE occupies 8 bytes
    var t = new UdtType("t", [new("arr", PbType.Word, 0, ElementCount: 4)], IsUnion: false);
    Assert.That(t.Size, Is.EqualTo(8));
  }

  [Test]
  public void FindField_GivenMixedCase_WhenLookedUp_ThenCaseInsensitive() {
    var t = new UdtType("t", [new("CurrentMode", PbType.Word, 0)], IsUnion: false);
    Assert.That(t.FindField("currentmode"), Is.Not.Null);
  }

  [Test]
  public void Size_GivenStaticArray_WhenQueried_ThenElementTimesCount() {
    // DIM a(1 TO 3, 0 TO 4) AS LONG -> 3*5*4 bytes
    var a = new ArrayType(PbType.Long, [(1, 3), (0, 4)], Rank: 2);
    Assert.That(a.ElementCount, Is.EqualTo(15));
    Assert.That(a.Size, Is.EqualTo(60));
    Assert.That(a.IsDynamic, Is.False);
  }

  [Test]
  public void Size_GivenDynamicArray_WhenQueried_ThenDescriptorSize() {
    var a = new ArrayType(PbType.Word, null, Rank: 2);
    Assert.That(a.IsDynamic, Is.True);
    Assert.That(a.Size, Is.EqualTo(8 + 2 * 4));
  }
}
