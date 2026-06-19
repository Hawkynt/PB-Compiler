using System.Diagnostics;
using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Emit.Omf;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Differential oracle for the OMF object linker (docs/LINKER.md): validate that
/// <em>our</em> linker integrates a foreign OMF object the same way the genuine
/// Microsoft <c>LINK.EXE</c> (shipped in tools/qb45-toolchain.tar.enc) does.
///
/// The oracle is hermetic w.r.t. the object code - both sides link the very same
/// hand-built bytes (a leaf cdecl <c>_addone</c> routine) - and depends only on the
/// genuine LINK.EXE plus DOSBox. A raw image byte-diff would be meaningless (our
/// linker links a BASIC main; MS LINK links an asm main), so equivalence is proved
/// <em>behaviourally</em>: link the same object on both sides, run both programs,
/// and require them to write the identical observable result to RESULT.TXT.
///
///   reference: hand-built MAIN.OBJ (asm: call _addone(41), format the LONG as PB's
///              `PRINT #` would, write RESULT.TXT via INT 21h, exit) + ADDONE.OBJ
///              -> genuine LINK.EXE -> REF.EXE -> RESULT.TXT
///   ours:      BASIC main DECLAREing addone CDECL ALIAS "_addone", PRINT #1 the
///              same call, + the same ADDONE object linked by our compiler -> EXE
///              -> RESULT.TXT
///
/// Skips gracefully (Assume) when the toolchain key, LINK.EXE, DOSBox or openssl is
/// unavailable, so CI without the decryption key still passes.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class LinkOracleTests {

  // --- minimal OMF record builders (checksum byte 0 = "ignore", accepted everywhere) ---
  private static byte[] Record(byte type, params byte[][] parts) {
    var body = parts.SelectMany(p => p).ToArray();
    return [type, (byte)(body.Length + 1), (byte)((body.Length + 1) >> 8), .. body, 0];
  }
  private static byte[] Str(string s) { var b = Encoding.ASCII.GetBytes(s); return [(byte)b.Length, .. b]; }
  private static byte[] U16(int v) => [(byte)v, (byte)(v >> 8)];

  // leaf cdecl FUNCTION addone(BYVAL x AS LONG) AS LONG -> x + 1 in DX:AX
  private static readonly byte[] _addOne =
    [0x55, 0x8B, 0xEC, 0x8B, 0x46, 0x04, 0x8B, 0x56, 0x06, 0x05, 0x01, 0x00, 0x83, 0xD2, 0x00, 0x5D, 0xC3];

  /// <summary>The shared foreign object - identical bytes feed both linkers.</summary>
  private static byte[] AddOneObj() => [
    .. Record(0x80, Str("ADDONE")),                                       // THEADR
    .. Record(0x96, Str("_TEXT"), Str("CODE")),                           // LNAMES: 1=_TEXT 2=CODE
    .. Record(0x98, [0x28], U16(_addOne.Length), [1], [2], [0]),          // SEGDEF _TEXT/CODE
    .. Record(0x90, [0], [1], Str("_addone"), U16(0), [0]),               // PUBDEF _addone @ seg1:0
    .. Record(0xA0, [1], U16(0), _addOne),                                // LEDATA seg1:0
    .. Record(0x8A, [0]),                                                 // MODEND
  ];

  // Hand-assembled 8086 "main" (single segment, DS=CS). Calls _addone(41), divides
  // the (16-bit) result by 10 to ASCII, then writes it to RESULT.TXT formatted EXACTLY
  // like PB's `PRINT #1, <long>`: a leading sign space, the digits, a trailing space,
  // then CRLF (" 42 \r\n"). The near call's disp16 at offset 10 is left zero and
  // resolved by a self-relative FIXUPP against the EXTDEF _addone.
  private const int MainCallDispSite = 10;
  private static readonly byte[] _main = [
    0x0E, 0x1F,                   // push cs / pop ds
    0x31, 0xC0, 0x50,             // xor ax,ax ; push ax        (arg hi = 0)
    0xB8, 0x29, 0x00, 0x50,       // mov ax,41 ; push ax        (arg lo = 41)
    0xE8, 0x00, 0x00,             // call near _addone          (disp16 @ off 10 -> FIXUPP)
    0x83, 0xC4, 0x04,             // add sp,4                   (cdecl caller cleanup)
    0xBE, 0x6F, 0x00,             // mov si, buf_end (0x6F)
    0xBB, 0x0A, 0x00,             // mov bx,10
    0x31, 0xD2, 0xF7, 0xF3,       // xor dx,dx ; div bx
    0x80, 0xC2, 0x30,             // add dl,'0'
    0x4E, 0x88, 0x14,             // dec si ; mov [si],dl
    0x09, 0xC0, 0x75, 0xF2,       // or ax,ax ; jnz divloop
    0xBA, 0x5C, 0x00,             // mov dx, fname (0x5C)
    0xB4, 0x3C, 0x31, 0xC9, 0xCD, 0x21, // mov ah,3Ch ; xor cx,cx ; int 21h (create)
    0x89, 0xC3, 0x89, 0xF5,       // mov bx,ax (handle) ; mov bp,si (digit start)
    0xBA, 0x58, 0x00,             // mov dx, lead (0x58)        write " "
    0xB9, 0x01, 0x00, 0xB4, 0x40, 0xCD, 0x21,
    0x89, 0xEA,                   // mov dx,bp                  write digits
    0xB9, 0x6F, 0x00, 0x29, 0xE9, 0xB4, 0x40, 0xCD, 0x21, // mov cx,buf_end ; sub cx,bp ; write
    0xBA, 0x59, 0x00,             // mov dx, tail (0x59)        write " \r\n"
    0xB9, 0x03, 0x00, 0xB4, 0x40, 0xCD, 0x21,
    0xB4, 0x3E, 0xCD, 0x21,       // mov ah,3Eh ; int 21h       close
    0xB8, 0x00, 0x4C, 0xCD, 0x21, // mov ax,4C00h ; int 21h     exit
    // --- data (DS=CS) ---
    0x20,                         // 0x58 lead: " "
    0x20, 0x0D, 0x0A,             // 0x59 tail: " \r\n"
    0x52, 0x45, 0x53, 0x55, 0x4C, 0x54, 0x2E, 0x54, 0x58, 0x54, 0x00, // 0x5C "RESULT.TXT",0
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,                   // 0x67 buf[8]
    // 0x6F buf_end
  ];

  /// <summary>The asm "main" as a genuine-shaped OMF object referencing _addone.</summary>
  private static byte[] MainObj() {
    // self-relative FIXUPP for the near call's disp16 (LOC=1 offset, M=0 self-rel),
    // frame = SEGDEF index 1, target = EXTDEF index 1, no displacement (P=1).
    var locatHi = (byte)(0x80 | (1 << 2) | ((MainCallDispSite >> 8) & 0x3));
    var locatLo = (byte)(MainCallDispSite & 0xFF);
    const byte fixDat = (1 << 2) | 2; // F=0/frame=SEGDEF(0), T=0/targ=EXTDEF(2), P=1 (no disp)
    return [
      .. Record(0x80, Str("MAIN")),                                      // THEADR
      .. Record(0x96, Str("_TEXT"), Str("CODE")),                        // LNAMES 1=_TEXT 2=CODE
      .. Record(0x98, [0x28], U16(_main.Length), [1], [2], [0]),         // SEGDEF _TEXT/CODE
      .. Record(0x8C, Str("_addone"), [0]),                              // EXTDEF _addone (idx 1)
      .. Record(0xA0, [1], U16(0), _main),                               // LEDATA seg1:0
      .. Record(0x9C, [locatHi, locatLo, fixDat, 1, 1]),                 // FIXUPP near call
      // MODEND main + start address (frame SEGDEF idx1, target SEGDEF idx1, disp 0):
      .. Record(0x8A, [0xC0, 0x00, 1, 1], U16(0)),
    ];
  }

  private static PbuFile AddOneUnit() => OmfToPbu.Convert(OmfReader.ReadObject(AddOneObj()));

  [Test]
  public void Link_GivenSameObjectLinkedByGenuineLinkExe_ThenOursMatchesTheGenuineResult() {
    // --- given: a genuine LINK.EXE and DOSBox are available -------------------
    var link = GenuineLinkExe();
    Assume.That(link, Is.Not.Null, "genuine LINK.EXE unavailable (no toolchain key/openssl) - oracle skipped");
    Assume.That(DosBoxRunner.Executable, Is.Not.Null, "DOSBox not found - oracle skipped");

    // --- when (reference): genuine LINK.EXE links MAIN.OBJ + ADDONE.OBJ -------
    var reference = RunGenuineLink(link!, MainObj(), AddOneObj());

    // --- when (ours): our compiler links the SAME object behind a BASIC main --
    const string source = """
      DECLARE FUNCTION addone CDECL ALIAS "_addone" (BYVAL x AS LONG) AS LONG
      OPEN "RESULT.TXT" FOR OUTPUT AS #1
      PRINT #1, addone(41)
      CLOSE #1
      END
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable([AddOneUnit()], []);
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    var (_, files) = DosBoxRunner.RunWithFiles(exe, ["RESULT.TXT"]);
    Assert.That(files.ContainsKey("RESULT.TXT"), Is.True, "our program wrote no RESULT.TXT");
    var ours = files["RESULT.TXT"];

    // --- then: both linkers integrated the object identically -----------------
    // The hand-built asm formats the LONG exactly as PB's PRINT # ( " 42 \r\n" ),
    // so the observable results must be byte-for-byte identical.
    Assert.That(ours, Is.EqualTo(reference),
      $"our linker produced [{Escape(ours)}] but genuine LINK.EXE produced [{Escape(reference)}]");
    Assert.That(reference.Trim(), Is.EqualTo("42"), "sanity: addone(41) must be 42 on the reference side");
  }

  [Test]
  public void Link_GivenObjectEmittedByOurOmfWriter_ThenGenuineLinkExeConsumesItAndRuns() {
    // --- given: a genuine LINK.EXE and DOSBox are available -------------------
    var link = GenuineLinkExe();
    Assume.That(link, Is.Not.Null, "genuine LINK.EXE unavailable (no toolchain key/openssl) - oracle skipped");
    Assume.That(DosBoxRunner.Executable, Is.Not.Null, "DOSBox not found - oracle skipped");

    // --- when: re-emit the addone unit with OUR OmfWriter, then let genuine LINK.EXE
    //     link the hand-built MAIN.OBJ against it (the inverse of every other test - here
    //     a real linker must consume an object WE produced) ----------------------
    var emitted = OmfWriter.WriteObject(AddOneUnit());
    var reference = RunGenuineLink(link!, MainObj(), emitted);

    // --- then: genuine LINK.EXE accepted our object and addone(41) ran as 42 ---
    Assert.That(reference.Trim(), Is.EqualTo("42"),
      $"genuine LINK.EXE linking our emitted _addone object should print 42 but produced [{Escape(reference)}]");
  }

  private static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

  // ---- genuine toolchain plumbing -------------------------------------------

  private static string RepoRoot()
    => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  /// <summary>
  /// Returns the path to a genuine MS LINK.EXE, decrypting tools/qb45-toolchain.tar.enc
  /// into tools/qb45/ on demand. The decryption key comes from PB_TOOLCHAIN_KEY or, as a
  /// local fallback, the pbkey file beside the repo. Returns null (=> skip) if it cannot
  /// be produced - no key, no openssl, or decryption fails.
  /// </summary>
  private static string? GenuineLinkExe() {
    var slot = Path.Combine(RepoRoot(), "tools", "qb45");
    var link = Path.Combine(slot, "LINK.EXE");
    if (File.Exists(link))
      return link;

    var enc = Path.Combine(RepoRoot(), "tools", "qb45-toolchain.tar.enc");
    if (!File.Exists(enc))
      return null;
    var key = ToolchainKey();
    if (key is null)
      return null;

    Directory.CreateDirectory(slot);
    // openssl ... -in enc | tar xz -C slot   (tar reads the decrypted stream on stdin)
    try {
      var ossl = new ProcessStartInfo("openssl",
          $"enc -d -aes-256-cbc -pbkdf2 -in \"{enc}\" -pass env:PB_ORACLE_KEY") {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
      };
      ossl.Environment["PB_ORACLE_KEY"] = key;
      using var op = Process.Start(ossl);
      if (op is null)
        return null;

      var tar = new ProcessStartInfo("tar", $"xz -C \"{slot}\"") {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true,
      };
      using var tp = Process.Start(tar);
      if (tp is null) { op.Kill(true); return null; }

      op.StandardOutput.BaseStream.CopyTo(tp.StandardInput.BaseStream);
      tp.StandardInput.Close();
      op.WaitForExit(30000);
      tp.WaitForExit(30000);
    } catch (Exception) {
      // openssl/tar not on PATH, or a transient failure - treat as "unavailable".
      return null;
    }

    return File.Exists(link) ? link : null;
  }

  private static string? ToolchainKey() {
    var env = Environment.GetEnvironmentVariable("PB_TOOLCHAIN_KEY");
    if (!string.IsNullOrWhiteSpace(env))
      return env.Trim();
    // Local convenience: the key file (pbkey) sits beside the working copies root, a few
    // levels above the repo - and a couple more when running from a .git worktree. Walk
    // up until it is found (or the filesystem root is reached).
    var dir = new DirectoryInfo(RepoRoot());
    for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent) {
      var p = Path.Combine(dir.FullName, "pbkey");
      if (File.Exists(p))
        return File.ReadAllText(p).Trim();
    }
    return null;
  }

  /// <summary>
  /// Links <paramref name="mainObj"/> + <paramref name="addOneObj"/> with the genuine
  /// LINK.EXE under DOSBox, runs the resulting REF.EXE, and returns the RESULT.TXT it
  /// writes. Reuses the sentinel/anti-vanish DOSBox pattern (see DosBoxRunner / scripts).
  /// </summary>
  private static string RunGenuineLink(string linkExe, byte[] mainObj, byte[] addOneObj) {
    var dir = Path.Combine(Path.GetTempPath(), "pbc-oracle-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      File.Copy(linkExe, Path.Combine(dir, "LINK.EXE"));
      File.WriteAllBytes(Path.Combine(dir, "MAIN.OBJ"), mainObj);
      File.WriteAllBytes(Path.Combine(dir, "ADDONE.OBJ"), addOneObj);
      var conf = Path.Combine(dir, "dosbox.conf");
      File.WriteAllText(conf, $"""
        [sdl]
        window_position = 9000,9000
        [cpu]
        core=auto
        cycles=max
        [dosbox]
        ems=true
        [autoexec]
        mount c "{dir}"
        c:
        LINK MAIN.OBJ+ADDONE.OBJ,REF.EXE,,, > LINKOUT.TXT
        REF.EXE
        echo ok > DONE.TXT
        exit
        """);

      var psi = new ProcessStartInfo(DosBoxRunner.Executable!, $"-conf \"{conf}\"") { UseShellExecute = false };
      using var process = Process.Start(psi)!;
      var sentinel = Path.Combine(dir, "DONE.TXT");
      var deadline = Environment.TickCount64 + 60000;
      while (!File.Exists(sentinel) && !process.HasExited && Environment.TickCount64 < deadline)
        Thread.Sleep(50);
      var finished = File.Exists(sentinel) || process.HasExited;
      if (!process.HasExited) {
        if (finished) Thread.Sleep(200);
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
      }

      var result = Path.Combine(dir, "RESULT.TXT");
      if (!File.Exists(result)) {
        var linkout = Path.Combine(dir, "LINKOUT.TXT");
        var diag = File.Exists(linkout) ? File.ReadAllText(linkout) : "(no LINKOUT.TXT)";
        Assert.Fail("genuine LINK.EXE produced no RESULT.TXT - linker output:\n" + diag);
      }
      return File.ReadAllText(result);
    } finally {
      try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
    }
  }
}
