using System.Diagnostics;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit.Omf;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Cross-compiler OMF interop (docs/LINKER.md): prove that <em>our</em> object
/// reader + linker integrate a genuine foreign C object produced by four
/// different vintage DOS C compilers, each of which emits a subtly different
/// flavour of Intel OMF:
///
///   bcc31  Borland C++ 3.1   - THEADR + COMENT(TC86) + DGROUP, cdecl "_name"
///   tc20   Turbo C 2.0       - THEADR + COMENT(TC86), cdecl "_name"
///   wc10   Watcom C/C++ 10.0 - THEADR + COMENT(WAT 0x9B); cdecl forced via -ecc
///   msc6   Microsoft C 6.0   - THEADR + COMENT, cdecl "_name"
///
/// Each genuine compiler (decrypted on demand from tools/&lt;slot&gt;-toolchain.tar.enc)
/// compiles the very same leaf C routine <c>int addone(int x){ return x+1; }</c>
/// to an .OBJ under DOSBox. We read that object with <see cref="OmfReader"/>,
/// lower it with <see cref="OmfToPbu"/>, and link it behind a BASIC main that
/// <c>PRINT #</c>s <c>addone(41)</c>. The program runs under DOSBox and must
/// write <c>42</c> to RESULT.TXT - proving our linker consumed that compiler's
/// OMF correctly and honoured cdecl (leading-underscore name, stack args,
/// caller cleanup).
///
/// Skips (Assume) when the toolchain key, the compiler, DOSBox or openssl is
/// unavailable, so CI without the decryption key still passes.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class CInteropTests {

  /// <summary>The leaf routine every compiler builds: a cdecl-by-default int identity+1.</summary>
  private const string DefaultLeaf = "int addone(int x){ return x + 1; }\n";

  /// <summary>A staged foreign C toolchain and how to drive it under DOSBox (slot mounted as C:, scratch as D:, cwd=D:).</summary>
  public sealed record ForeignCc(string Slot, string Display, string[] Env, string CompileCmd, string ObjName, string? CSource = null) {
    public override string ToString() => this.Display;
  }

  // Borland's BCC is DPMI-hosted - the DPMI loader files live beside it in BIN, so PATH must reach them.
  private static readonly ForeignCc Bcc31 = new("bcc31", "Borland C++ 3.1",
    ["set PATH=C:\\BIN"], "C:\\BIN\\BCC.EXE -c -ms LEAF.C > CC.LOG", "LEAF.OBJ");

  private static readonly ForeignCc Tc20 = new("tc20", "Turbo C 2.0",
    [], "C:\\TCC.EXE -c -ms LEAF.C > CC.LOG", "LEAF.OBJ");

  // Watcom's default is its register "watcall" convention (trailing-underscore names),
  // so the source spells __cdecl explicitly to get the leading-underscore "_addone" and
  // stack args the BASIC ALIAS expects (10.0a predates OpenWatcom's -ecc switch). -s drops
  // the implicit __STK stack-probe call (an EXTDEF we have no runtime for). wcc emits the
  // 16-bit .OBJ directly (no -c); it is a 32-bit image needing W32RUN.EXE on PATH.
  private static readonly ForeignCc Wc10 = new("wc10", "Watcom C/C++ 10.0a",
    ["set WATCOM=C:\\", "set INCLUDE=C:\\H", "set PATH=C:\\BINW"],
    "wcc -ms -0 -s LEAF.C > CC.LOG", "LEAF.OBJ",
    CSource: "int __cdecl addone(int x){ return x + 1; }\n");

  // CL spawns its C1/C2/C3 passes off PATH and reads INCLUDE; /AS = small model, /c = no link.
  // (We stage MS C 6.0, not 7.0: 7.0's CL.EXE is a DOSX32 image needing a 32-bit DPMI host
  // that DOSBox does not provide, so it cannot run under the harness. 6.0 is pure real mode.)
  // /Gs disables the implicit __aNchkstk stack-probe call MS C plants in every prologue
  // (a referenced external we have no runtime for) - the MS counterpart of Watcom's -s.
  private static readonly ForeignCc Msc6 = new("msc6", "Microsoft C 6.0",
    ["set PATH=C:\\BIN", "set INCLUDE=C:\\INCLUDE", "set LIB=C:\\LIB"],
    "CL /c /AS /Gs LEAF.C > CC.LOG", "LEAF.OBJ");

  private static IEnumerable<TestCaseData> Compilers() {
    foreach (var cc in new[] { Bcc31, Tc20, Wc10, Msc6 })
      yield return new TestCaseData(cc).SetName($"Link_ForeignC_{cc.Slot}");
  }

  [TestCaseSource(nameof(Compilers))]
  public void Link_GivenLeafObjectFromForeignCCompiler_WhenLinkedByOurs_ThenCallReturns42(ForeignCc cc) {
    // --- given: the genuine compiler and DOSBox are available -----------------
    var slot = EnsureToolchain(cc.Slot);
    Assume.That(slot, Is.Not.Null, $"{cc.Display}: toolchain unavailable (no key/openssl/.enc) - skipped");
    Assume.That(DosBoxRunner.Executable, Is.Not.Null, "DOSBox not found - skipped");

    // --- when: that compiler builds the leaf object, we read+lower+link it -----
    var obj = CompileLeaf(slot!, cc);
    var unit = OmfToPbu.Convert(OmfReader.ReadObject(obj));

    const string source = """
      DECLARE FUNCTION addone CDECL ALIAS "_addone" (BYVAL x AS INTEGER) AS INTEGER
      OPEN "RESULT.TXT" FOR OUTPUT AS #1
      PRINT #1, addone(41)
      CLOSE #1
      END
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35), Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable([unit], []);
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));

    var (_, files) = DosBoxRunner.RunWithFiles(exe, ["RESULT.TXT"]);

    // --- then: the linked foreign object computed addone(41) = 42 -------------
    Assert.That(files.ContainsKey("RESULT.TXT"), Is.True, $"{cc.Display}: linked program wrote no RESULT.TXT");
    Assert.That(files["RESULT.TXT"].Trim(), Is.EqualTo("42"),
      $"{cc.Display}: linking its C addone(41) should print 42 but got [{files["RESULT.TXT"].Replace("\r", "\\r").Replace("\n", "\\n")}]");
  }

  // ---- linking + trimming a real C runtime library --------------------------

  // strlen of a non-const global (not a literal) so the compiler must emit a real call to
  // the runtime _strlen rather than folding it; the prototype avoids needing <string.h>.
  private const string StrlenSrc = """
    unsigned strlen(const char *);
    char buf[6] = "hello";
    int slen(void){ return (int)strlen(buf); }

    """;

  /// <summary>A foreign object that needs the C runtime, plus the small-model lib that satisfies it.</summary>
  public sealed record CRuntimeCase(ForeignCc Cc, string LibRel, string Symbol) {
    public override string ToString() => this.Cc.Display;
  }

  private static IEnumerable<TestCaseData> CRuntimeLibs() {
    // reuse each compiler's env, but build the strlen-calling object instead of the leaf
    ForeignCc With(ForeignCc baseCc, string cmd) => baseCc with { CompileCmd = cmd, CSource = StrlenSrc };
    yield return new TestCaseData(new CRuntimeCase(
      With(Bcc31, "C:\\BIN\\BCC.EXE -c -ms LEAF.C > CC.LOG"), "LIB\\CS.LIB", "_strlen")).SetName("Link_CRuntimeTrim_bcc31");
    yield return new TestCaseData(new CRuntimeCase(
      With(Tc20, "C:\\TCC.EXE -c -ms LEAF.C > CC.LOG"), "LIB\\CS.LIB", "_strlen")).SetName("Link_CRuntimeTrim_tc20");
    yield return new TestCaseData(new CRuntimeCase(
      With(Msc6, "CL /c /AS /Gs LEAF.C > CC.LOG"), "LIB\\SLIBCR.LIB", "_strlen")).SetName("Link_CRuntimeTrim_msc6");
  }

  [TestCaseSource(nameof(CRuntimeLibs))]
  public void Link_GivenObjectNeedingCRuntime_WhenLibLinkedLazily_ThenSymbolPulledTrimmedAndRuns(CRuntimeCase c) {
    // --- given: the compiler, its small-model C runtime lib, and DOSBox -------
    var slot = EnsureToolchain(c.Cc.Slot);
    Assume.That(slot, Is.Not.Null, $"{c.Cc.Display}: toolchain unavailable - skipped");
    Assume.That(DosBoxRunner.Executable, Is.Not.Null, "DOSBox not found - skipped");
    var libPath = Path.Combine(slot!, c.LibRel.Replace('\\', Path.DirectorySeparatorChar));
    Assume.That(File.Exists(libPath), Is.True, $"{c.Cc.Display}: {c.LibRel} not staged - skipped");

    // --- when: the object references _strlen; we link the real lib lazily -----
    var obj = CompileLeaf(slot!, c.Cc);
    var unit = OmfToPbu.Convert(OmfReader.ReadObject(obj));
    Assert.That(unit.Imports.Any(i => i.Name == c.Symbol), Is.True,
      $"{c.Cc.Display}: object did not import {c.Symbol} - imports [{string.Join(", ", unit.Imports.Select(i => i.Name))}]");

    var lib = new OmfLibrary(File.ReadAllBytes(libPath));
    Assume.That(lib.Defines(c.Symbol), Is.True, $"{c.Cc.Display}: {c.LibRel} has no {c.Symbol} - skipped");

    const string source = """
      DECLARE FUNCTION slen CDECL ALIAS "_slen" () AS INTEGER
      OPEN "RESULT.TXT" FOR OUTPUT AS #1
      PRINT #1, slen()
      CLOSE #1
      END
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35), Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable([unit], [], [lib]);
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));

    // --- then: only a handful of the lib's many members were pulled (trimmed) --
    Assert.That(lib.MemberCount, Is.GreaterThan(20), "sanity: a real C runtime holds many members");
    Assert.That(lib.ProvidedCount, Is.LessThanOrEqualTo(3),
      $"{c.Cc.Display}: expected selective extraction but pulled {lib.ProvidedCount} of {lib.MemberCount} members");

    // --- and: the linked program ran the genuine library strlen ---------------
    var (_, files) = DosBoxRunner.RunWithFiles(exe, ["RESULT.TXT"]);
    Assert.That(files.ContainsKey("RESULT.TXT"), Is.True, $"{c.Cc.Display}: linked program wrote no RESULT.TXT");
    Assert.That(files["RESULT.TXT"].Trim(), Is.EqualTo("5"),
      $"{c.Cc.Display}: library strlen(\"hello\") should be 5 but got [{files["RESULT.TXT"].Replace("\r", "\\r").Replace("\n", "\\n")}]");
  }

  // Library-level selective extraction over all four real runtimes (incl. Watcom, whose
  // register-convention CRT we can't *call* via cdecl but can still parse + trim). No DOSBox.
  private static IEnumerable<TestCaseData> CRuntimeLibFiles() {
    yield return new TestCaseData("bcc31", "LIB\\CS.LIB").SetName("LibTrim_bcc31");
    yield return new TestCaseData("tc20", "LIB\\CS.LIB").SetName("LibTrim_tc20");
    yield return new TestCaseData("msc6", "LIB\\SLIBCR.LIB").SetName("LibTrim_msc6");
    yield return new TestCaseData("wc10", "lib286\\dos\\CLIBS.LIB").SetName("LibTrim_wc10");
  }

  [TestCaseSource(nameof(CRuntimeLibFiles))]
  public void Library_GivenRealCRuntime_WhenSymbolExtracted_ThenOnlyItsMemberIsLowered(string slot, string libRel) {
    var dir = EnsureToolchain(slot);
    Assume.That(dir, Is.Not.Null, $"{slot}: toolchain unavailable - skipped");
    var libPath = Path.Combine(dir!, libRel.Replace('\\', Path.DirectorySeparatorChar));
    Assume.That(File.Exists(libPath), Is.True, $"{slot}: {libRel} not staged - skipped");

    var lib = new OmfLibrary(File.ReadAllBytes(libPath));
    Assert.That(lib.MemberCount, Is.GreaterThan(20), $"{slot}: a real C runtime holds many members");

    // strlen exists in every C runtime; the public name varies by the lib's convention
    var symbol = new[] { "_strlen", "strlen_", "strlen" }.FirstOrDefault(lib.Defines);
    Assert.That(symbol, Is.Not.Null, $"{slot}: lib advertises no strlen variant");

    var pulled = lib.Provide(symbol!);
    Assert.That(pulled, Is.Not.Null, $"{slot}: extracting {symbol} returned no member");
    Assert.That(pulled!.Code.Length, Is.GreaterThan(0), $"{slot}: extracted strlen member should carry code");
    Assert.That(lib.ProvidedCount, Is.EqualTo(1),
      $"{slot}: extracting one symbol should lower exactly one of {lib.MemberCount} members, not {lib.ProvidedCount}");
  }

  // ---- our .LIB dictionary hash matches every vintage toolchain's librarian -----

  // For each genuine C runtime library, our OMF library hash must locate EVERY public via the
  // dictionary search the foreign linker uses - proving the hash OmfLibraryWriter emits is the
  // same one MS LIB / Borland TLIB / Turbo C / Watcom WLIB wrote, so those linkers resolve PB
  // symbols from a .LIB we produce. No DOSBox needed: a pure static cross-check of the hash
  // against the real librarian's bucket placement. (The end-to-end link is the LinkOracleTests
  // .LIB oracle, which additionally needs DOSBox.)
  private static IEnumerable<TestCaseData> RealLibraries() {
    yield return new TestCaseData("bcc31", "LIB\\CS.LIB").SetName("LibHashMatches_bcc31_CS");
    yield return new TestCaseData("tc20", "LIB\\CS.LIB").SetName("LibHashMatches_tc20_CS");
    yield return new TestCaseData("msc6", "LIB\\SLIBCR.LIB").SetName("LibHashMatches_msc6_SLIBCR");
    yield return new TestCaseData("wc10", "lib286\\dos\\CLIBC.LIB").SetName("LibHashMatches_wc10_CLIBC");
  }

  [TestCaseSource(nameof(RealLibraries))]
  public void Library_GivenGenuineRuntimeLib_WhenSearchedWithOurHash_ThenEverySymbolIsLocated(string slot, string libRel) {
    var dir = EnsureToolchain(slot);
    Assume.That(dir, Is.Not.Null, $"{slot}: toolchain unavailable - skipped");
    var libPath = Path.Combine(dir!, libRel.Replace('\\', Path.DirectorySeparatorChar));
    Assume.That(File.Exists(libPath), Is.True, $"{slot}: {libRel} not staged - skipped");

    var lib = File.ReadAllBytes(libPath);
    var (located, total) = LocateAllByOurHash(lib);
    Assert.That(total, Is.GreaterThan(50), $"{slot}: sanity - a real CRT holds many publics");
    Assert.That(located, Is.EqualTo(total),
      $"{slot} {libRel}: our omflib_hash located {located}/{total} - it must match the librarian's placement for every symbol");
  }

  // The genuine OMF library hash (MS/Intel/Watcom omflib_hash) + dictionary search, run against a
  // real .LIB: parse its blocks, then for every stored public confirm our hash's probe finds it.
  private static (int Located, int Total) LocateAllByOurHash(byte[] lib) {
    if (lib.Length < 9 || lib[0] != 0xF0)
      return (0, 0);
    var dictOff = lib[3] | (lib[4] << 8) | (lib[5] << 16) | (lib[6] << 24);
    var blocks = lib[7] | (lib[8] << 8);
    var names = new List<byte[]>();
    for (var b = 0; b < blocks; ++b) {
      var bbase = dictOff + b * 512;
      if (bbase + 512 > lib.Length) break;
      for (var bk = 0; bk < 37; ++bk) {
        var slot = lib[bbase + bk];
        if (slot == 0) continue;
        var e = bbase + slot * 2;
        var ln = lib[e];
        names.Add(lib[(e + 1)..(e + 1 + ln)]);
      }
    }
    var found = names.Count(nm => DictSearch(lib, dictOff, blocks, nm));
    return (found, names.Count);
  }

  private static bool DictSearch(byte[] lib, int dictOff, int blocks, byte[] sym) {
    var (block, blockd, bucket, bucketd) = OmfHash(sym, blocks);
    for (var i = 0; i < blocks; ++i) {
      var bbase = dictOff + block * 512;
      var bk = bucket;
      for (var j = 0; j < 37; ++j) {
        var slot = lib[bbase + bk];
        if (slot == 0) {
          if (lib[bbase + 37] == 0) return false;   // empty bucket, page not full -> absent
          break;
        }
        var e = bbase + slot * 2;
        var ln = lib[e];
        if (ln == sym.Length && lib.AsSpan(e + 1, ln).SequenceEqual(sym)) return true;
        bk += bucketd; if (bk >= 37) bk -= 37;
      }
      block += blockd; if (block >= blocks) block -= blocks;
    }
    return false;
  }

  private static ushort Rotl(ushort a, int b) => (ushort)((a << b) | (a >> (16 - b)));
  private static ushort Rotr(ushort a, int b) => (ushort)((a << (16 - b)) | (a >> b));

  private static (int Block, int BlockDelta, int Bucket, int BucketDelta) OmfHash(byte[] name, int numBlocks) {
    var count = name.Length;
    int l = 0, r = count;
    ushort block = (ushort)(count | 0x20), blockd = 0, bucket = 0, bucketd = (ushort)(count | 0x20);
    for (; ; ) {
      var curr = name[--r] | 0x20;
      blockd = (ushort)(curr ^ Rotl(blockd, 2));
      bucket = (ushort)(curr ^ Rotr(bucket, 2));
      if (--count == 0) break;
      curr = name[l++] | 0x20;
      block = (ushort)(curr ^ Rotl(block, 2));
      bucketd = (ushort)(curr ^ Rotr(bucketd, 2));
    }
    var bkd = bucketd % 37; if (bkd == 0) bkd = 1;
    var bld = blockd % numBlocks; if (bld == 0) bld = 1;
    return (block % numBlocks, bld, bucket % 37, bkd);
  }

  // ---- calling real objects through each calling convention -----------------

  /// <summary>A foreign object exporting sub2(a,b)=a-b under a convention, and how BASIC declares it.</summary>
  public sealed record ConvCase(ForeignCc Cc, string Convention, string Alias) {
    public override string ToString() => $"{this.Cc.Display} {this.Convention}";
  }

  private static IEnumerable<TestCaseData> ConventionObjects() {
    // Watcom's default register convention (watcall): a=AX, b=DX, name sub2_
    yield return new TestCaseData(new ConvCase(
      Wc10 with { CSource = "int sub2(int a,int b){ return a-b; }\n", CompileCmd = "wcc -ms -0 -s LEAF.C > CC.LOG" },
      "WATCALL", "sub2_")).SetName("Call_Watcall_wc10");
    // Microsoft/Borland fastcall: a=AX, b=DX, name @sub2
    yield return new TestCaseData(new ConvCase(
      Bcc31 with { CSource = "int __fastcall sub2(int a,int b){ return a-b; }\n", CompileCmd = "C:\\BIN\\BCC.EXE -c -ms LEAF.C > CC.LOG" },
      "FASTCALL", "@sub2")).SetName("Call_Fastcall_bcc31");
    yield return new TestCaseData(new ConvCase(
      Msc6 with { CSource = "int _fastcall sub2(int a,int b){ return a-b; }\n", CompileCmd = "CL /c /AS /Gs LEAF.C > CC.LOG" },
      "FASTCALL", "@sub2")).SetName("Call_Fastcall_msc6");
    // pascal: stack left-to-right, callee-clean, name SUB2 (uppercased)
    yield return new TestCaseData(new ConvCase(
      Bcc31 with { CSource = "int pascal sub2(int a,int b){ return a-b; }\n", CompileCmd = "C:\\BIN\\BCC.EXE -c -ms LEAF.C > CC.LOG" },
      "PASCAL", "SUB2")).SetName("Call_Pascal_bcc31");
  }

  [TestCaseSource(nameof(ConventionObjects))]
  public void Link_GivenForeignObjectWithCallingConvention_WhenCalled_ThenReturns13(ConvCase c) {
    var slot = EnsureToolchain(c.Cc.Slot);
    Assume.That(slot, Is.Not.Null, $"{c.Cc.Display}: toolchain unavailable - skipped");
    Assume.That(DosBoxRunner.Executable, Is.Not.Null, "DOSBox not found - skipped");

    var obj = CompileLeaf(slot!, c.Cc);
    var unit = OmfToPbu.Convert(OmfReader.ReadObject(obj));
    Assert.That(unit.Exports.Any(e => e.Name == c.Alias), Is.True,
      $"{c.Cc.Display}: object did not export {c.Alias} - exports [{string.Join(", ", unit.Exports.Select(e => e.Name))}]");

    var source = $"""
      DECLARE FUNCTION sub2 {c.Convention} ALIAS "{c.Alias}" (BYVAL a AS INTEGER, BYVAL b AS INTEGER) AS INTEGER
      OPEN "RESULT.TXT" FOR OUTPUT AS #1
      PRINT #1, sub2(20, 7)
      CLOSE #1
      END
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35), Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable([unit], []);
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));

    var (_, files) = DosBoxRunner.RunWithFiles(exe, ["RESULT.TXT"]);
    Assert.That(files.ContainsKey("RESULT.TXT"), Is.True, $"{c.Cc.Display}: linked program wrote no RESULT.TXT");
    Assert.That(files["RESULT.TXT"].Trim(), Is.EqualTo("13"),
      $"{c.Cc.Display} {c.Convention}: sub2(20,7) should be 13 but got [{files["RESULT.TXT"].Replace("\r", "\\r").Replace("\n", "\\n")}]");
  }

  // ---- linking + calling a C++ function by its mangled name -----------------

  // Compiled as C++ (BCC -P), so the public is name-mangled. A free function still uses
  // the cdecl argument convention - only the NAME carries the signature - so the BASIC
  // side declares it CDECL and ALIASes the exact Borland mangled symbol @square$qi.
  private const string CppSquareSrc = "int square(int x){ return x * x; }\n";

  [Test]
  public void Link_GivenCppFunctionCompiledAsCpp_WhenCalledByMangledName_ThenReturns25() {
    // --- given: Borland C++ 3.1 (it mangles C++ names) and DOSBox are available
    var slot = EnsureToolchain(Bcc31.Slot);
    Assume.That(slot, Is.Not.Null, $"{Bcc31.Display}: toolchain unavailable - skipped");
    Assume.That(DosBoxRunner.Executable, Is.Not.Null, "DOSBox not found - skipped");

    // --- when: compile square() AS C++ (so the public is mangled), read+lower it
    var cpp = Bcc31 with { CSource = CppSquareSrc, CompileCmd = "C:\\BIN\\BCC.EXE -c -ms -P LEAF.C > CC.LOG" };
    var unit = OmfToPbu.Convert(OmfReader.ReadObject(CompileLeaf(slot!, cpp)));

    // the mangled public must be present exactly as Borland decorates a free int square(int)
    const string mangled = "@square$qi";
    Assert.That(unit.Exports.Any(e => e.Name == mangled), Is.True,
      $"{Bcc31.Display}: object did not export {mangled} - exports [{string.Join(", ", unit.Exports.Select(e => e.Name))}]");
    // and our demangler reads that symbol back to a legible signature
    Assert.That(Demangle.Parse(mangled).Pretty, Is.EqualTo("square(int)"));

    // --- and: a BASIC main declares it CDECL ALIAS to that mangled symbol -------
    var source = $"""
      DECLARE FUNCTION square CDECL ALIAS "{mangled}" (BYVAL x AS INTEGER) AS INTEGER
      OPEN "RESULT.TXT" FOR OUTPUT AS #1
      PRINT #1, square(5)
      CLOSE #1
      END
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35), Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable([unit], []);
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));

    var (_, files) = DosBoxRunner.RunWithFiles(exe, ["RESULT.TXT"]);

    // --- then: linking the C++ square(5) by its mangled name printed 25 ---------
    Assert.That(files.ContainsKey("RESULT.TXT"), Is.True, $"{Bcc31.Display}: linked program wrote no RESULT.TXT");
    Assert.That(files["RESULT.TXT"].Trim(), Is.EqualTo("25"),
      $"{Bcc31.Display}: linking C++ square(5) via {mangled} should print 25 but got [{files["RESULT.TXT"].Replace("\r", "\\r").Replace("\n", "\\n")}]");
  }

  // ---- linking an object that uses FAR data (compact/large-model fixups) -----

  // A small-model object with an explicitly FAR global: reading its value forces the
  // compiler to emit a Base16 (segment of g) + Offset16 (offset of g) FIXUPP pair - the
  // far-reference shape compact/large-model objects use. Our linker hosts the whole program
  // in one combined segment, so that segment is just the program's load segment (an MZ
  // relocation) and the offset is g's place in the image - getg() must still read 100.
  private const string FarGlobalSrc = "int far g = 100;\nint getg(void){ return g; }\n";

  [Test]
  public void Link_GivenObjectUsingFarData_WhenLinkedByOurs_ThenFarReadReturns100() {
    // --- given: Borland C++ 3.1 and DOSBox are available ----------------------
    var slot = EnsureToolchain(Bcc31.Slot);
    Assume.That(slot, Is.Not.Null, $"{Bcc31.Display}: toolchain unavailable - skipped");
    Assume.That(DosBoxRunner.Executable, Is.Not.Null, "DOSBox not found - skipped");

    // --- when: build the far-global object; it must really carry a far fixup ---
    var cc = Bcc31 with { CSource = FarGlobalSrc, CompileCmd = "C:\\BIN\\BCC.EXE -c -ms LEAF.C > CC.LOG" };
    var module = OmfReader.ReadObject(CompileLeaf(slot!, cc));
    Assert.That(module.Fixups.Any(f => f.Location is OmfLocation.Base16 or OmfLocation.Pointer32), Is.True,
      $"{Bcc31.Display}: expected a far fixup from 'int far g' - locations [{string.Join(", ", module.Fixups.Select(f => f.Location))}]");
    var unit = OmfToPbu.Convert(module);

    const string source = """
      DECLARE FUNCTION getg CDECL ALIAS "_getg" () AS INTEGER
      OPEN "RESULT.TXT" FOR OUTPUT AS #1
      PRINT #1, getg()
      CLOSE #1
      END
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35), Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable([unit], []);
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));

    var (_, files) = DosBoxRunner.RunWithFiles(exe, ["RESULT.TXT"]);

    // --- then: the linked far read produced 100 -------------------------------
    Assert.That(files.ContainsKey("RESULT.TXT"), Is.True, $"{Bcc31.Display}: linked program wrote no RESULT.TXT");
    Assert.That(files["RESULT.TXT"].Trim(), Is.EqualTo("100"),
      $"{Bcc31.Display}: a far read of g should print 100 but got [{files["RESULT.TXT"].Replace("\r", "\\r").Replace("\n", "\\n")}]");
  }

  /// <summary>Compiles the leaf C routine with <paramref name="cc"/> under DOSBox and returns the .OBJ bytes.</summary>
  private static byte[] CompileLeaf(string slot, ForeignCc cc) {
    var work = Path.Combine(Path.GetTempPath(), "pbc-cc-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      File.WriteAllText(Path.Combine(work, "LEAF.C"), cc.CSource ?? DefaultLeaf);
      var conf = Path.Combine(work, "dosbox.conf");
      // slot mounted C: (compiler), scratch mounted D: and made cwd so the .OBJ,
      // CC.LOG and the DONE.TXT sentinel land in the host-visible work dir. core=auto
      // /cycles=max keeps the DPMI/DOS-extended compilers (Borland, Watcom) happy.
      File.WriteAllText(conf, $"""
        [sdl]
        window_position = 9000,9000
        [cpu]
        core=auto
        cycles=max
        [autoexec]
        mount c "{slot}"
        mount d "{work}"
        {string.Join("\n", cc.Env)}
        d:
        {cc.CompileCmd}
        echo ok > DONE.TXT
        exit
        """);

      var psi = new ProcessStartInfo(DosBoxRunner.Executable!, $"-conf \"{conf}\"") { UseShellExecute = false };
      using var process = Process.Start(psi)!;
      var sentinel = Path.Combine(work, "DONE.TXT");
      var deadline = Environment.TickCount64 + 120000;
      while (!File.Exists(sentinel) && !process.HasExited && Environment.TickCount64 < deadline)
        Thread.Sleep(50);
      var finished = File.Exists(sentinel) || process.HasExited;
      if (!process.HasExited) {
        if (finished) Thread.Sleep(200);
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
      }
      if (!finished)
        Assert.Fail($"{cc.Display}: compile timed out under DOSBox");

      var objPath = Path.Combine(work, cc.ObjName);
      if (!File.Exists(objPath)) {
        var log = Path.Combine(work, "CC.LOG");
        var diag = File.Exists(log) ? File.ReadAllText(log) : "(no CC.LOG)";
        Assert.Fail($"{cc.Display}: compiler produced no {cc.ObjName}. Output:\n{diag}");
      }
      return File.ReadAllBytes(objPath);
    } finally {
      try { Directory.Delete(work, recursive: true); } catch (IOException) { /* best effort */ }
    }
  }

  // ---- toolchain plumbing (mirrors LinkOracleTests' decrypt-on-demand) -------

  private static string RepoRoot()
    => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  /// <summary>
  /// Returns the populated tools/&lt;slot&gt; directory, decrypting
  /// tools/&lt;slot&gt;-toolchain.tar.enc into it on demand. Key from PB_TOOLCHAIN_KEY
  /// or a walked-up pbkey file. Returns null (=&gt; skip) when it cannot be produced.
  /// </summary>
  private static string? EnsureToolchain(string slot) {
    var dir = Path.Combine(RepoRoot(), "tools", slot);
    if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any())
      return dir;

    var enc = Path.Combine(RepoRoot(), "tools", slot + "-toolchain.tar.enc");
    if (!File.Exists(enc))
      return null;
    var key = ToolchainKey();
    if (key is null)
      return null;

    Directory.CreateDirectory(dir);
    try {
      var ossl = new ProcessStartInfo("openssl",
          $"enc -d -aes-256-cbc -pbkdf2 -in \"{enc}\" -pass env:PB_ORACLE_KEY") {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
      };
      ossl.Environment["PB_ORACLE_KEY"] = key;
      using var op = Process.Start(ossl);
      if (op is null)
        return null;
      var tar = new ProcessStartInfo("tar", $"xz -C \"{dir}\"") {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true,
      };
      using var tp = Process.Start(tar);
      if (tp is null) { op.Kill(true); return null; }
      op.StandardOutput.BaseStream.CopyTo(tp.StandardInput.BaseStream);
      tp.StandardInput.Close();
      op.WaitForExit(30000);
      tp.WaitForExit(30000);
    } catch (Exception) {
      return null;
    }
    return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any() ? dir : null;
  }

  private static string? ToolchainKey() {
    var env = Environment.GetEnvironmentVariable("PB_TOOLCHAIN_KEY");
    if (!string.IsNullOrWhiteSpace(env))
      return env.Trim();
    var dir = new DirectoryInfo(RepoRoot());
    for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent) {
      var p = Path.Combine(dir.FullName, "pbkey");
      if (File.Exists(p))
        return File.ReadAllText(p).Trim();
    }
    return null;
  }
}
