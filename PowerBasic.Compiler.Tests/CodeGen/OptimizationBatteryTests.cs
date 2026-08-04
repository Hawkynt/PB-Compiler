using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// The optimization battery: every SUB in <c>tests/optimize/*.BAS</c> is one scenario that
/// states, in its own header comment, what the optimizer is expected to do with it - and the
/// harness checks that expectation against the bytes actually emitted for that SUB.
///
/// A scenario is written as an annotation block directly above a <c>NOINLINE</c> SUB:
/// <code>
/// ' @scenario BoundsCheckDroppedForCounterIndex
/// ' @what     a%(i%) indexed by a FOR counter proven inside the array's bounds
/// ' @expect   the Error-9 bounds check disappears entirely
/// ' @assert   absent-call rt_bounds
/// ' @status   done
/// SUB BoundsCheckDroppedForCounterIndex NOINLINE
/// </code>
///
/// Assertions (repeatable; every one must hold):
/// <list type="bullet">
///   <item><c>present &lt;pattern&gt;</c> / <c>absent &lt;pattern&gt;</c> - a named byte
///     pattern from <see cref="_patterns"/> occurs (or not) inside the SUB's code.</item>
///   <item><c>present-call &lt;label&gt;</c> / <c>absent-call &lt;label&gt;</c> - the SUB
///     contains (or not) a near call whose target is that runtime label or procedure.</item>
///   <item><c>count &lt;pattern&gt; &lt;n&gt;</c> - the pattern occurs exactly n times (says what
///     present/absent cannot: a value loaded ONCE, not recomputed).</item>
///   <item><c>bytes&lt;= &lt;n&gt;</c> - the SUB's emitted code is at most n bytes.</item>
///   <item><c>smaller-than-unoptimized</c> - the SUB shrank versus the same source with the
///     optimizer off.</item>
/// </list>
///
/// <c>@status done</c> scenarios gate the build; <c>@status roadmap</c> ones are reported but
/// never fail - that is how a not-yet-implemented idea is parked in the battery: write the
/// scenario, watch it report as roadmap, implement the optimization, flip the status.
/// </summary>
[TestFixture]
public sealed class OptimizationBatteryTests {

  private static readonly string _batteryDir =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "tests", "optimize"));

  /// <summary>
  /// Named 8086 byte patterns an expectation can name. Kept small and literal on purpose: each
  /// entry is one unambiguous encoding the optimizer either produces or avoids. Add to it when a
  /// new scenario needs a new shape.
  /// </summary>
  private static readonly Dictionary<string, byte[]> _patterns = new(StringComparer.OrdinalIgnoreCase) {
    ["imul-bx"] = [0xF7, 0xEB],          // IMUL BX   - signed 16x16 -> 32 in DX:AX
    ["mul-bx"] = [0xF7, 0xE3],           // MUL BX    - unsigned 16x16 -> 32
    ["idiv-bx"] = [0xF7, 0xFB],          // IDIV BX   - signed 32/16 divide
    ["div-bx"] = [0xF7, 0xF3],           // DIV BX    - unsigned 32/16 divide
    ["cmp-ax-bx"] = [0x39, 0xD8],        // CMP AX,BX - a 16-bit comparison
    ["cmp-dx-cx"] = [0x39, 0xCA],        // CMP DX,CX - the high-word compare of a signed 32-bit fold
    ["cmp-ax-imm"] = [0x83, 0xF8],       // CMP AX,imm8 - a comparison against a small constant
    ["sub-ax-bx"] = [0x29, 0xD8],        // SUB AX,BX
    ["sbb-dx-cx"] = [0x19, 0xCA],        // SBB DX,CX - the 2nd instruction of every 32-bit compare/subtract
    ["adc-dx-cx"] = [0x11, 0xCA],        // ADC DX,CX - the 2nd instruction of a 32-bit add
    ["rep-stosw"] = [0xF3, 0xAB],        // REP STOSW - the frame/array zero fill
    ["cwd"] = [0x99],                    // CWD       - sign-extend AX into DX:AX
    ["xor-ax-ax"] = [0x31, 0xC0],        // XOR AX,AX - the zero idiom
    ["push-ax-pop-ax"] = [0x50, 0x58],   // PUSH AX / POP AX - staging that cancels itself out
    ["mov-ax-mem-bx"] = [0x8B, 0x87],    // MOV AX,[BX+disp16] - one read of an array element
    ["mov-ax-frame"] = [0x8B, 0x46],     // MOV AX,[BP+disp8] - a read of a frame cell
    ["add-ax-mem-bx"] = [0x03, 0x87],    // ADD AX,[BX+disp16] - an array element fused into the op
    ["add-di-mem-bx"] = [0x03, 0x3F],
    ["add-di-mem-bp"] = [0x03, 0x7E],    // ADD DI,[BP+disp8] - accumulate a frame scratch into the resident register    // ADD DI,[BX] - the element accumulated straight into the resident register
    ["add-bx-2"] = [0x83, 0xC3, 0x02],   // ADD BX,2  - the element pointer stepping over 2-byte elements
    ["mov-bx-ax"] = [0x89, 0xC3],        // MOV BX,AX - an address computed into the index register
    ["mov-ax-minus1"] = [0xB8, 0xFF, 0xFF],  // MOV AX,-1 - PB's TRUE, materialized from a comparison
    ["test-ax-ax"] = [0x85, 0xC0],       // TEST AX,AX - the branch reading that materialized truth value
    ["jmp-to-next"] = [0xEB, 0x00],      // JMP +0 - a jump to the following instruction
    ["mov-mem-imm"] = [0xC7, 0x46],      // MOV WORD PTR [BP+disp8],imm16 - a constant straight into a local
    ["fild-dword"] = [0xDB, 0x06],       // FILD dword [mem] - an integer entering the x87
    ["fistp-dword"] = [0xDB, 0x1E],      // FISTP dword [mem] - and coming back out
  };

  /// <summary>One annotated scenario: a NOINLINE procedure plus what is expected of its code.</summary>
  private sealed record Scenario(string Name, string What, string Expect, string Status, IReadOnlyList<string> Asserts, int Line) {
    public bool IsRoadmap => this.Status.Equals("roadmap", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>A compiled battery file: the raw code image plus the per-procedure byte extents.</summary>
  private sealed class Compiled {
    public required byte[] Code { get; init; }
    public required CodeGenerator.ListingInfo Listing { get; init; }
    public required IReadOnlyDictionary<string, (int Start, int End)> Extents { get; init; }

    public ReadOnlySpan<byte> CodeOf(string procedure) {
      var (start, end) = this.Extents[procedure];
      return this.Code.AsSpan(start, end - start);
    }
  }

  /// <summary>Every battery file, by name - the fixture resolves it under tests/optimize/.</summary>
  public static IEnumerable<string> Batteries() {
    if (!Directory.Exists(_batteryDir))
      yield break;
    foreach (var file in Directory.EnumerateFiles(_batteryDir, "*.BAS").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
      yield return Path.GetFileName(file);
  }

  [TestCaseSource(nameof(Batteries))]
  public void Battery_GivenScenarios_WhenCompiledOptimized_ThenEachExpectationHolds(string battery) {
    var file = Path.Combine(_batteryDir, battery);
    var source = File.ReadAllText(file);
    var scenarios = ParseScenarios(source);
    Assert.That(scenarios, Is.Not.Empty, $"{Path.GetFileName(file)} declares no @scenario blocks");

    var optimized = Compile(source, file, optimize: true);
    var plain = Compile(source, file, optimize: false);

    var failures = new List<string>();
    var roadmap = new List<string>();
    foreach (var scenario in scenarios) {
      if (!optimized.Extents.ContainsKey(scenario.Name)) {
        Report(scenario, $"no procedure named '{scenario.Name}' survived to the image - is it called from the main body, and is it NOINLINE?");
        continue;
      }
      foreach (var assertion in scenario.Asserts) {
        var (ok, detail) = Evaluate(assertion, scenario.Name, optimized, plain);
        if (!ok)
          Report(scenario, $"{assertion} -> {detail}");
      }
    }

    void Report(Scenario scenario, string message) {
      var line = $"{Path.GetFileName(file)}({scenario.Line}) {scenario.Name}: {message}";
      (scenario.IsRoadmap ? roadmap : failures).Add(line);
    }

    if (roadmap.Count > 0)
      TestContext.Out.WriteLine("roadmap scenarios (reported, not gating):" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", roadmap));
    TestContext.Out.WriteLine($"{scenarios.Count} scenarios, {roadmap.Count} roadmap expectation(s) still unmet");
    Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
  }

  /// <summary>
  /// The battery must also RUN: its main body drives every scenario and prints one line each,
  /// which must match the sibling <c>.expected</c> file. An optimization that changes behaviour
  /// is a bug however good the emitted code looks. Skipped without DOSBox.
  /// </summary>
  [TestCaseSource(nameof(Batteries))]
  public void Battery_GivenScenarios_WhenRunUnderDosBox_ThenOutputMatchesGoldenBothWays(string battery) {
    var file = Path.Combine(_batteryDir, battery);
    var expectedFile = Path.ChangeExtension(file, ".expected");
    Assume.That(File.Exists(expectedFile), $"no golden output next to {Path.GetFileName(file)}");
    var source = File.ReadAllText(file);
    var expected = DosBoxRunner.Normalize(File.ReadAllText(expectedFile));

    foreach (var optimize in (bool[])[false, true]) {
      var model = Bind(source, file);
      var generator = new CodeGenerator(model) { Optimize = optimize };
      var exe = generator.EmitExecutable();
      Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
      var output = DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
      Assert.That(output, Is.EqualTo(expected), $"{Path.GetFileName(file)} with optimize={optimize}");
    }
  }

  #region annotations

  /// <summary>Reads the <c>' @key value</c> blocks; a block ends at the SUB/FUNCTION it describes.</summary>
  private static List<Scenario> ParseScenarios(string source) {
    var scenarios = new List<Scenario>();
    var lines = source.Replace("\r\n", "\n").Split('\n');
    string? name = null, what = null, expect = null, status = null;
    var asserts = new List<string>();
    var line = 0;

    for (var i = 0; i < lines.Length; ++i) {
      var text = lines[i].Trim();
      if (text.StartsWith('\'')) {
        var body = text.TrimStart('\'', ' ', '=');
        if (!body.StartsWith('@'))
          continue;
        var space = body.IndexOf(' ');
        var (key, value) = space < 0 ? (body[1..], "") : (body[1..space], body[(space + 1)..].Trim());
        switch (key.ToLowerInvariant()) {
          case "scenario": name = value; line = i + 1; break;
          case "what": what = value; break;
          case "expect": expect = value; break;
          case "status": status = value; break;
          case "assert": asserts.Add(value); break;
        }
        continue;
      }
      if (name == null || text.Length == 0)
        continue;
      // the first non-comment line closes the block; it must be the procedure it describes
      if (text.StartsWith("SUB ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("FUNCTION ", StringComparison.OrdinalIgnoreCase))
        scenarios.Add(new(name, what ?? "", expect ?? "", status ?? "done", [.. asserts], line));
      (name, what, expect, status) = (null, null, null, null);
      asserts.Clear();
    }
    return scenarios;
  }

  #endregion

  #region compilation

  private static SemanticModel Bind(string source, string path) {
    var name = Path.GetFileName(path);
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, name, Dialect.Pb36), name, Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static Compiled Compile(string source, string path, bool optimize) {
    var generator = new CodeGenerator(Bind(source, path)) { Optimize = optimize };
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    var listing = generator.DescribeImage();

    // the listing's offsets index the raw image, which the MZ header prefixes
    var code = exe.AsSpan(BitConverter.ToUInt16(exe, 8) * 16).ToArray();

    // a procedure runs to the next thing bound after it - the next procedure, the first runtime
    // label past it, or the end of the code
    var boundaries = listing.Procedures.Where(p => p.CodeOffset >= 0).Select(p => p.CodeOffset)
      .Concat(listing.RuntimeLabels.Select(l => l.Offset))
      .Append(Math.Min(listing.CodeLength, code.Length))
      .Distinct().OrderBy(o => o).ToList();
    var extents = new Dictionary<string, (int Start, int End)>(StringComparer.OrdinalIgnoreCase);
    foreach (var procedure in listing.Procedures.Where(p => p is { CodeOffset: >= 0, IsExternal: false })) {
      var end = boundaries.FirstOrDefault(o => o > procedure.CodeOffset, code.Length);
      extents[procedure.Name] = (procedure.CodeOffset, Math.Min(end, code.Length));
    }
    return new() { Code = code, Listing = listing, Extents = extents };
  }

  #endregion

  #region assertions

  private static (bool Ok, string Detail) Evaluate(string assertion, string procedure, Compiled optimized, Compiled plain) {
    var space = assertion.IndexOf(' ');
    var verb = (space < 0 ? assertion : assertion[..space]).Trim();
    var argument = space < 0 ? "" : assertion[(space + 1)..].Trim();
    var code = optimized.CodeOf(procedure);

    switch (verb.ToLowerInvariant()) {
      case "present":
      case "absent": {
        if (!_patterns.TryGetValue(argument, out var pattern))
          return (false, $"unknown byte pattern '{argument}' (known: {string.Join(", ", _patterns.Keys.Order())})");
        var found = code.IndexOf(pattern) >= 0;
        var want = verb.Equals("present", StringComparison.OrdinalIgnoreCase);
        return (found == want, found ? $"{argument} is present" : $"{argument} is absent");
      }

      case "present-call":
      case "absent-call": {
        var called = CallTargets(optimized, procedure);
        var found = called.Contains(argument);
        var want = verb.Equals("present-call", StringComparison.OrdinalIgnoreCase);
        return (found == want, found
          ? $"calls {argument}"
          : $"does not call {argument} (calls: {(called.Count == 0 ? "nothing" : string.Join(", ", called.Order()))})");
      }

      case "count": {
        // "count <pattern> <n>" - the pattern occurs exactly n times. Says what present/absent
        // cannot: that a value is loaded ONCE per iteration rather than recomputed.
        var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var want))
          return (false, $"'{argument}' is not '<pattern> <count>'");
        if (!_patterns.TryGetValue(parts[0], out var counted))
          return (false, $"unknown byte pattern '{parts[0]}' (known: {string.Join(", ", _patterns.Keys.Order())})");
        var seen = 0;
        for (var at = 0; at + counted.Length <= code.Length; ++at)
          if (code[at..].StartsWith(counted))
            ++seen;
        return (seen == want, $"{parts[0]} occurs {seen}x, expected {want}x");
      }

      case "bytes<=": {
        if (!int.TryParse(argument, out var limit))
          return (false, $"'{argument}' is not a byte count");
        return (code.Length <= limit, $"{code.Length} bytes");
      }

      case "smaller-than-unoptimized": {
        if (!plain.Extents.ContainsKey(procedure))
          return (false, "the unoptimized build has no such procedure");
        var before = plain.CodeOf(procedure).Length;
        return (code.Length < before, $"{before} -> {code.Length} bytes");
      }

      default:
        return (false, $"unknown assertion verb '{verb}'");
    }
  }

  /// <summary>
  /// The named targets of the near calls (<c>E8 rel16</c>) inside a procedure. A linear scan, so
  /// it only reports a call when the decoded target lands exactly on a bound runtime label or
  /// procedure entry - an immediate byte that happens to be 0xE8 practically never does.
  /// </summary>
  private static HashSet<string> CallTargets(Compiled battery, string procedure) {
    var byOffset = new Dictionary<int, string>();
    foreach (var label in battery.Listing.RuntimeLabels)
      byOffset.TryAdd(label.Offset, label.Name);
    foreach (var proc in battery.Listing.Procedures.Where(p => p.CodeOffset >= 0))
      byOffset.TryAdd(proc.CodeOffset, proc.Name);

    var (start, end) = battery.Extents[procedure];
    var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for (var i = start; i + 2 < end; ++i) {
      if (battery.Code[i] != 0xE8)
        continue;
      var target = i + 3 + BitConverter.ToInt16(battery.Code, i + 1);
      if (byOffset.TryGetValue(target, out var name))
        targets.Add(name);
    }
    return targets;
  }

  #endregion
}
