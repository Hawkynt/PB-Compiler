using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Syntax;

[TestFixture]
public sealed class ParserRuntimeTargetMetaTests {
  [TestCase("$CPU SSE2")]
  [TestCase("$CPU SSE4.1")]
  [TestCase("$CPU AVX-512")]
  [TestCase("$CPU 80586 SSE2")]
  [TestCase("$ISA SSE4.2 ERROR")]
  [TestCase("$ISA SSE4.1 = ERROR")]
  [TestCase("$ISA SSE4.2, EMULATE")]
  [TestCase("$ISA PMINUD EMULATE")]
  [TestCase("$ISA DEFAULT AUTO")]
  [TestCase("$FPU NATIVE")]
  [TestCase("$X87 ERROR")]
  [TestCase("$FLOAT NPX")]
  [TestCase("$FLOAT EMULATE")]
  [TestCase("$FLOAT PROCEDURE")]
  public void Parse_GivenRuntimeTargetMetastatement_ThenAcceptsIt(string source) {
    Assert.DoesNotThrow(() => Parser.Parse(Lexer.Tokenize(source + "\n", "runtime-meta.bas", Dialect.Pb36),
      "runtime-meta.bas", Dialect.Pb36));
  }

  [TestCase("$CPU UNKNOWNCPU")]
  [TestCase("$CPU 8086 UNKNOWNFEATURE")]
  [TestCase("$ISA SSE2")]
  [TestCase("$ISA SSE2 MAYBE")]
  [TestCase("$FPU")]
  [TestCase("$X87 SOMETIMES")]
  [TestCase("$FLOAT SOMETIMES")]
  public void Parse_GivenMalformedRuntimeTargetMetastatement_ThenRejectsIt(string source) {
    Assert.Throws<ParserException>(() => Parser.Parse(Lexer.Tokenize(source + "\n", "runtime-meta.bas", Dialect.Pb36),
      "runtime-meta.bas", Dialect.Pb36));
  }
}
