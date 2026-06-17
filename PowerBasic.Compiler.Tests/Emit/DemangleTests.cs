using PowerBasic.Compiler.Emit.Omf;

namespace PowerBasic.Compiler.Tests.Emit;

/// <summary>
/// The C++ symbol demangler (docs/LINKER.md "C++ mangled symbols"): turns a mangled
/// public back into a legible <c>name(types)</c> so an unresolved-external diagnostic
/// can tell the user what ALIAS to write. The Borland cases are <b>real</b> symbols
/// harvested from genuine BCC 3.1 output (<c>BCC -c -ms -P</c>); the MSVC and Watcom
/// cases follow each scheme's documented encoding.
/// </summary>
[TestFixture]
public sealed class DemangleTests {

  // ---- Borland / Turbo C++ (verified against real BCC 3.1 PUBDEFs) ----------

  [TestCase("@square$qi", "square(int)")]
  [TestCase("@addl$qll", "addl(long, long)")]
  [TestCase("@noargs$qv", "noargs()")]
  [TestCase("@dfun$qd", "dfun(double)")]
  [TestCase("@vptr$qpzc", "vptr(signed char *)")]
  [TestCase("@many$qzciuil", "many(signed char, int, unsigned int, long)")]
  public void Parse_GivenRealBorlandSymbol_WhenDemangled_ThenReadableSignature(string symbol, string pretty) {
    // given a genuine Borland mangled free-function public
    // when demangling it
    var d = Demangle.Parse(symbol);
    // then the scheme and readable signature are recovered
    Assert.That(d.IsMangled, Is.True, $"{symbol} should be recognised as mangled");
    Assert.That(d.Scheme, Is.EqualTo(MangleScheme.Borland));
    Assert.That(d.Pretty, Is.EqualTo(pretty));
  }

  [Test]
  public void Parse_GivenBorlandSymbol_WhenDemangled_ThenBareNameRecovered() {
    var d = Demangle.Parse("@square$qi");
    Assert.That(d.Name, Is.EqualTo("square"));
  }

  // boundary: a single 'v' is an empty parameter list, not a one-element (void) list
  [Test]
  public void Parse_GivenBorlandVoidArgs_WhenDemangled_ThenEmptyParameterList() {
    var d = Demangle.Parse("@noargs$qv");
    Assert.That(d.Pretty, Is.EqualTo("noargs()"));
  }

  // pointer prefix stacks: char ** is two 'p's
  [Test]
  public void Parse_GivenBorlandDoublePointer_WhenDemangled_ThenTwoStars() {
    var d = Demangle.Parse("@f$qppc");
    Assert.That(d.Pretty, Is.EqualTo("f(char **)"));
  }

  // ---- Microsoft Visual C++ ------------------------------------------------

  [TestCase("?square@@YAHH@Z", "square(int)")]
  [TestCase("?addtwo@@YAHHH@Z", "addtwo(int, int)")]
  [TestCase("?noargs@@YAHXZ", "noargs()")]
  [TestCase("?takeptr@@YAXPAD@Z", "takeptr(char *)")]
  public void Parse_GivenMsvcSymbol_WhenDemangled_ThenReadableSignature(string symbol, string pretty) {
    var d = Demangle.Parse(symbol);
    Assert.That(d.IsMangled, Is.True, $"{symbol} should be recognised as mangled");
    Assert.That(d.Scheme, Is.EqualTo(MangleScheme.Msvc));
    Assert.That(d.Pretty, Is.EqualTo(pretty));
  }

  // ---- Watcom C++ ----------------------------------------------------------

  [TestCase("square_$n(i)i", "square(int)")]
  [TestCase("W?fn$n(ii)i", "fn(int, int)")]
  [TestCase("g_$n(v)v", "g()")]
  public void Parse_GivenWatcomSymbol_WhenDemangled_ThenReadableSignature(string symbol, string pretty) {
    var d = Demangle.Parse(symbol);
    Assert.That(d.IsMangled, Is.True, $"{symbol} should be recognised as mangled");
    Assert.That(d.Scheme, Is.EqualTo(MangleScheme.Watcom));
    Assert.That(d.Pretty, Is.EqualTo(pretty));
  }

  // ---- extern "C" / plain (equivalence: NOT mangled) -----------------------

  [TestCase("_addone")]
  [TestCase("_strlen")]
  [TestCase("printf")]
  [TestCase("MYSUB")]
  public void Parse_GivenExternCOrPlainSymbol_WhenDemangled_ThenReportedUnmangled(string symbol) {
    // given a cdecl / extern "C" public (leading underscore) or an undecorated name
    var d = Demangle.Parse(symbol);
    // then it is reported as not mangled and echoed as-is
    Assert.That(d.IsMangled, Is.False, $"{symbol} should not be treated as C++ mangled");
    Assert.That(d.Scheme, Is.EqualTo(MangleScheme.None));
    Assert.That(d.Pretty, Is.EqualTo(symbol));
  }

  [Test]
  public void Parse_GivenLeadingUnderscore_WhenDemangled_ThenBareNameDropsUnderscore() {
    var d = Demangle.Parse("_addone");
    Assert.That(d.Name, Is.EqualTo("addone"));
  }

  // ---- exceptional / edge cases --------------------------------------------

  [Test]
  public void Parse_GivenNull_WhenDemangled_ThenUnmangledEmpty() {
    var d = Demangle.Parse(null);
    Assert.That(d.IsMangled, Is.False);
    Assert.That(d.Pretty, Is.EqualTo(""));
  }

  [Test]
  public void Parse_GivenEmptyString_WhenDemangled_ThenUnmangled() {
    var d = Demangle.Parse("");
    Assert.That(d.IsMangled, Is.False);
    Assert.That(d.Name, Is.EqualTo(""));
  }

  // a bare '@' with no name must not be mistaken for Borland
  [Test]
  public void Parse_GivenAtSignWithoutName_WhenDemangled_ThenNotBorland() {
    var d = Demangle.Parse("@$qi");
    Assert.That(d.Scheme, Is.Not.EqualTo(MangleScheme.Borland));
  }

  [Test]
  public void IsMangled_GivenBorlandSymbol_ThenTrue()
    => Assert.That(Demangle.IsMangled("@square$qi"), Is.True);

  [Test]
  public void IsMangled_GivenPlainSymbol_ThenFalse()
    => Assert.That(Demangle.IsMangled("_addone"), Is.False);
}
