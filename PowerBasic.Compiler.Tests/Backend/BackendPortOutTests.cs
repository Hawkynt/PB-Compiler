using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>OUT port, value</c> on the routed path.
///
/// <para>
/// The direct emitter writes it inline as <c>OUT DX, AL</c>. The IR had no name for it, so every
/// body containing one declined - 46 of them over the SVGA corpus, all graphics code setting a VGA
/// register, and each decline takes the whole module body with it.
/// </para>
/// <para>
/// It is a runtime CALL here rather than an inline instruction because the same declaration reaches
/// <c>--emit-c</c> and <c>--emit-llvm</c>, where a port write is whatever that target says it is.
/// <c>rt_outp</c> lives in its own runtime section, so a program that never drives a port pays
/// nothing for its existence.
/// </para>
/// <para>
/// Observable at all only because the interpreter now records port traffic. There is no hardware
/// behind these ports and modelling one would prove nothing; what a test can ask is which byte
/// reached which port, which is exactly the claim <c>OUT</c> makes.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendPortOutTests {

  private const string _source = """
    DIM p AS INTEGER, v AS INTEGER
    p = &H3C8
    v = 42
    OUT p, v
    OUT &H3C9, 7
    PRINT "done"
    """;

  private static (Cpu8086 Cpu, IEnumerable<string> Routed) Run(bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image), generator.BackendRoutedNames.ToList());
  }

  /// <summary>
  /// A variable port and a literal one, because they take different paths through the selector: the
  /// literal can become an immediate, the variable cannot. Both bytes must arrive, at their own
  /// ports, in source order - an OUT that reached the right port with the wrong byte, or the right
  /// byte with the port and value swapped, would pass a test that only counted them.
  /// </summary>
  [TestCase(false)]
  [TestCase(true)]
  public void Run_GivenOut_ThenTheBytesReachTheirPorts(bool optimize) {
    var (cpu, _) = Run(optimize);

    Assert.Multiple(() => {
      Assert.That(cpu.PortWrites, Is.EqualTo(new[] { ((ushort)0x3C8, (byte)42), ((ushort)0x3C9, (byte)7) }));
      Assert.That(cpu.Output.Trim(), Is.EqualTo("done"), "execution must continue past the OUT");
    });
  }

  /// <summary>
  /// The premise. Before the IR named the operation this body declined, and the port traffic above
  /// would then be the direct emitter's - correct, and proving nothing about this back end.
  /// </summary>
  [Test]
  public void Route_GivenOut_ThenTheModuleBodyIsTakenByTheBackEnd() {
    var (_, routed) = Run(optimize: false);

    Assert.That(routed, Does.Contain("main"), "a body containing OUT must route now");
  }
}
