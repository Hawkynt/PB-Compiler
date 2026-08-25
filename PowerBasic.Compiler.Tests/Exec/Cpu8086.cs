using System.Text;

namespace PowerBasic.Compiler.Tests.Exec;

/// <summary>
/// A real-mode 8086 interpreter, enough of one to <b>run</b> the executables this compiler emits.
///
/// It exists to answer one question the rest of the test suite cannot: does the retargetable IR path
/// produce the same OBSERVABLE behaviour as the direct emitter? Byte-identity with PBC 3.50 is the
/// direct emitter's job and the IR path will never match those bytes - it is a different code
/// generator. What it must match is what the program PRINTS, and until something executes the image
/// nobody can say whether it does. Every claim about the back end has rested on matched register
/// conventions and static invariants; this turns them into a measurement
/// (<see cref="Tests.Backend.BackendDifferentialTests"/>).
///
/// The design rule that matters more than coverage: <b>it fails loudly</b>. An unimplemented opcode,
/// an unhandled DOS call, a runaway loop - all throw <see cref="Cpu8086Exception"/> naming what was
/// hit and where. An interpreter that quietly does the wrong thing would prove the opposite of what it
/// is for, so a program it cannot run is a skipped test, never a passing one. Its x87 model preserves
/// integral values exactly and approximates non-integral values with doubles; the latter still bounds
/// which floating-point fidelity claims this interpreter can make.
/// </summary>
public sealed class Cpu8086 {

  private const int _MEMORY = 1 << 20;                 // one megabyte, the real-mode address space
  private const int _MAX_EXEC_DEPTH = 32;
  private const int _EMS_PAGE_SIZE = 16 * 1024;
  private const int _EMS_TOTAL_PAGES = 256;             // four MiB, enough to expose allocation changes
  private const ushort _PSP_SEGMENT = 0x0100;
  private const ushort _LOAD_SEGMENT = 0x0110;         // DOS loads the image one PSP (16 paragraphs) up
  private const ushort _EMS_FRAME_SEGMENT = 0xE000;

  private readonly byte[] _memory = new byte[_MEMORY];

  /// <summary>The BIOS video mode last set through INT 10h AH=00h; 03h (80x25 colour text) at reset.</summary>
  private byte _videoMode = 0x03;
  private readonly StringBuilder _output = new();
  private readonly StringBuilder _printer = new();
  private readonly Dictionary<int, OpenFile> _files = [];
  private readonly Dictionary<string, MemoryFile> _byName = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, byte[]> _executables = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<ushort, byte[]> _emsHandles = [];
  private readonly EmsMapping?[] _emsMappings = new EmsMapping?[4];

  /// <summary>Directories the program has created; there is no host file system behind this.</summary>
  private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
  private int _nextHandle = 5;                          // 0..4 are the standard handles
  private ushort _nextFreeSegment = 0x2000;             // where INT 21h/48h hands out blocks
  private ushort _nextEmsHandle = 1;
  private int _execDepth;
  private byte _childExitCode;

  private sealed class MemoryFile {
    public string Name = "";
    public List<byte> Bytes = [];
  }

  /// <summary>
  /// One DOS handle onto a file. The POSITION belongs to the handle rather than to the file, which is
  /// what DOS does and what lets a program hold the same file open twice - one handle reading while
  /// another writes - without the two silently sharing a cursor.
  /// </summary>
  private sealed class OpenFile {
    public MemoryFile File = new();
    public int Position;
  }

  private readonly record struct EmsMapping(ushort Handle, ushort LogicalPage);

  // registers, in the encoding order the ModRM byte uses
  private readonly ushort[] _r = new ushort[8];         // AX CX DX BX SP BP SI DI
  private readonly ushort[] _rh = new ushort[8];        // high halves for the executable 386 subset
  private ushort _cs, _ds, _es, _ss, _ip;
  private bool _cf, _zf, _sf, _of, _pf, _af, _df;
  private bool _halted;

  private const int _AX = 0, _CX = 1, _DX = 2, _BX = 3, _SP = 4, _BP = 5, _SI = 6, _DI = 7;

  /// <summary>Everything the program wrote to stdout/stderr, in order.</summary>
  public string Output => this._output.ToString();

  /// <summary>
  /// Everything the program wrote to DOS handle 4, PRN - what LPRINT prints.
  ///
  /// <para>
  /// Kept APART from <see cref="Output"/> rather than folded into it, because the whole point of
  /// LPRINT is that the printer is a second destination: a test that could not tell the two apart
  /// would pass just as happily for a compiler that sent everything to the screen.
  /// </para>
  /// </summary>
  public string PrinterOutput => this._printer.ToString();

  /// <summary>The exit code the program terminated with.</summary>
  public int ExitCode { get; private set; }

  /// <summary>
  /// The byte at a real-mode <c>segment:offset</c> after the run - what a test asks when the question
  /// is where a store LANDED rather than what the program printed. The address space is flat here, so
  /// video memory at B800 and A000 is ordinary memory and reads back what was written to it.
  /// </summary>
  public byte MemoryAt(ushort segment, int offset) => this.ReadByte(Linear(segment, (ushort)offset));

  /// <summary>Whether the program created (and did not remove) the named directory.</summary>
  public bool DirectoryExists(string name) => this._directories.Contains(name);

  /// <summary>The contents of a file the program created, or null if it made no such file.</summary>
  public string? FileContent(string name)
    => this._byName.TryGetValue(name, out var file) ? Encoding.ASCII.GetString([.. file.Bytes]) : null;

  /// <summary>The raw bytes of a file the program created, or null if it made no such file.</summary>
  public byte[]? FileBytes(string name)
    => this._byName.TryGetValue(name, out var file) ? [.. file.Bytes] : null;

  /// <summary>
  /// Every file name the disk holds after the run. A comparison that names the files it will look at
  /// can only see a difference in one of them; this is what lets a caller compare the whole disk, so
  /// a program that writes under a name nobody thought of is still observed.
  /// </summary>
  public IEnumerable<string> FileNames => this._byName.Keys;

  /// <summary>Loads an MZ executable and runs it to termination (or until <paramref name="maxSteps"/> instructions).</summary>
  public static Cpu8086 Run(byte[] exe, int maxSteps = 20_000_000) {
    var cpu = new Cpu8086();
    cpu._executables["T.EXE"] = exe;                    // the test harness runs each image under this DOS name
    cpu.Load(exe);
    cpu.Execute(maxSteps);
    return cpu;
  }

  /// <summary>
  /// As <see cref="Run(byte[],int)"/>, but with files already on the (in-memory) disk, and reporting
  /// rather than throwing what stopped the program - the machine comes back either way.
  ///
  /// <para>
  /// This overload deliberately registers no executable target. A CHAIN therefore stops at its EXEC,
  /// letting a test inspect the COMMON handoff and feed it to a separately started image. The simpler
  /// overload registers its image as <c>T.EXE</c> and follows a self-CHAIN end to end. Loudness is
  /// preserved in both forms: <paramref name="fault"/> names an unavailable target or other boundary,
  /// and a caller that ignores it is asserting on a program that did not finish.
  /// </para>
  /// </summary>
  public static Cpu8086 Run(byte[] exe, IReadOnlyDictionary<string, byte[]> disk,
      out Cpu8086Exception? fault, int maxSteps = 20_000_000) {
    var cpu = new Cpu8086();
    foreach (var (name, bytes) in disk)
      cpu._byName[name] = new MemoryFile { Name = name, Bytes = [.. bytes] };
    cpu.Load(exe);
    fault = null;
    try {
      cpu.Execute(maxSteps);
    } catch (Cpu8086Exception e) {
      fault = e;
    }
    return cpu;
  }

  /// <summary>
  /// The environment a loaded program sees, as DOS lays it out: NAME=VALUE strings, each
  /// NUL-terminated, the lot closed by a second NUL. The segment holding it goes in the PSP word at
  /// 2Ch, which is where <c>rt_environ</c> looks for it.
  ///
  /// Without this the PSP was blank and every program read an environment segment of zero, so
  /// ENVIRON$ - which ships - could not be executed here at all and had no test.
  /// </summary>
  private static readonly (string Name, string Value)[] _environment = [
    ("PATH", "C:\\DOS"), ("COMSPEC", "C:\\COMMAND.COM"), ("PROMPT", "$P$G"),
  ];

  private const ushort _ENVIRONMENT_SEGMENT = 0x00F0;

  /// <summary>Paragraphs DOS allocated for the environment - the bound ENVIRON has to respect.</summary>
  private const ushort _ENVIRONMENT_PARAGRAPHS = 32;

  private void InstallEnvironment() {
    var at = _ENVIRONMENT_SEGMENT * 16;
    foreach (var (name, value) in _environment) {
      foreach (var c in $"{name}={value}")
        this._memory[at++] = (byte)c;
      this._memory[at++] = 0;
    }
    this._memory[at] = 0;                                        // the block's own terminator
    this.WriteWord(_PSP_SEGMENT * 16 + 0x2C, _ENVIRONMENT_SEGMENT);

    // the memory-control block DOS puts one paragraph below any allocation: 'M', owner, size in
    // paragraphs. ENVIRON reads the size from here to know how much room it has to write into.
    var mcb = (_ENVIRONMENT_SEGMENT - 1) * 16;
    this._memory[mcb] = (byte)'M';
    this.WriteWord(mcb + 1, _PSP_SEGMENT);
    this.WriteWord(mcb + 3, _ENVIRONMENT_PARAGRAPHS);
  }

  private void Load(byte[] exe) {
    this.InstallEnvironment();
    if (exe.Length < 0x1C || exe[0] != 'M' || exe[1] != 'Z') {
      // a COM-style image: no header, no relocations, everything in one segment at offset 0x100
      Array.Copy(exe, 0, this._memory, _PSP_SEGMENT * 16 + 0x100, exe.Length);
      this._cs = this._ds = this._es = this._ss = _PSP_SEGMENT;
      this._ip = 0x100;
      this._r[_SP] = 0xFFFE;
      return;
    }
    var headerParagraphs = Word(exe, 0x08);
    var headerSize = headerParagraphs * 16;
    var relocations = Word(exe, 0x06);
    var relocationTable = Word(exe, 0x18);
    var pages = Word(exe, 0x04);
    var lastPage = Word(exe, 0x02);
    var imageSize = pages * 512 - headerSize - (lastPage == 0 ? 0 : 512 - lastPage);

    var loadAddress = _LOAD_SEGMENT * 16;
    Array.Copy(exe, headerSize, this._memory, loadAddress, Math.Min(imageSize, exe.Length - headerSize));

    // every relocation names a word holding a segment, which the loader biases by where it landed
    for (var i = 0; i < relocations; ++i) {
      var offset = Word(exe, relocationTable + i * 4);
      var segment = Word(exe, relocationTable + i * 4 + 2);
      var at = (_LOAD_SEGMENT + segment) * 16 + offset;
      WriteWord(at, (ushort)(ReadWord(at) + _LOAD_SEGMENT));
    }

    this._cs = (ushort)(_LOAD_SEGMENT + Word(exe, 0x16));
    this._ip = Word(exe, 0x14);
    this._ss = (ushort)(_LOAD_SEGMENT + Word(exe, 0x0E));
    this._r[_SP] = Word(exe, 0x10);
    this._ds = this._es = _PSP_SEGMENT;
  }

  private static ushort Word(byte[] bytes, int at) => (ushort)(bytes[at] | (bytes[at + 1] << 8));

  private void Execute(int maxSteps) {
    for (var step = 0; step < maxSteps; ++step) {
      if (this._halted)
        return;
      this.Step();
    }
    throw new Cpu8086Exception($"ran {maxSteps} instructions without terminating (runaway program?)");
  }

  private void ExecuteChild(byte[] image) {
    if (this._execDepth >= _MAX_EXEC_DEPTH)
      throw new Cpu8086Exception($"EXEC nesting exceeded {_MAX_EXEC_DEPTH} images");

    var child = new Cpu8086 { _execDepth = this._execDepth + 1 };
    foreach (var (name, file) in this._byName)
      child._byName[name] = file;
    foreach (var (name, executable) in this._executables)
      child._executables[name] = executable;
    foreach (var directory in this._directories)
      child._directories.Add(directory);

    child.Load(image);
    child.Execute(20_000_000);

    this._output.Append(child._output);
    this._printer.Append(child._printer);
    this._byName.Clear();
    foreach (var (name, file) in child._byName)
      this._byName[name] = file;
    this._directories.Clear();
    this._directories.UnionWith(child._directories);
    this._childExitCode = (byte)child.ExitCode;
  }

  // ---- memory ---------------------------------------------------------------------------------

  private static int Linear(ushort segment, ushort offset) => (segment * 16 + offset) & (_MEMORY - 1);
  private byte ReadByte(int at) => this._memory[at & (_MEMORY - 1)];
  private void WriteByte(int at, byte value) => this._memory[at & (_MEMORY - 1)] = value;
  private ushort ReadWord(int at) => (ushort)(this.ReadByte(at) | (this.ReadByte(at + 1) << 8));

  private void WriteWord(int at, ushort value) {
    this.WriteByte(at, (byte)value);
    this.WriteByte(at + 1, (byte)(value >> 8));
  }

  private byte Fetch() {
    var value = this.ReadByte(Linear(this._cs, this._ip));
    ++this._ip;
    return value;
  }

  private ushort FetchWord() {
    var lo = this.Fetch();
    return (ushort)(lo | (this.Fetch() << 8));
  }

  private void Push(ushort value) {
    this._r[_SP] -= 2;
    this.WriteWord(Linear(this._ss, this._r[_SP]), value);
  }

  private ushort Pop() {
    var value = this.ReadWord(Linear(this._ss, this._r[_SP]));
    this._r[_SP] += 2;
    return value;
  }

  // ---- 8-bit register halves ------------------------------------------------------------------

  private byte Reg8(int index) => (byte)(index < 4 ? this._r[index] : this._r[index - 4] >> 8);

  private void SetReg8(int index, byte value) {
    if (index < 4)
      this._r[index] = (ushort)((this._r[index] & 0xFF00) | value);
    else
      this._r[index - 4] = (ushort)((this._r[index - 4] & 0x00FF) | (value << 8));
  }

  private uint Reg32(int index) => this._r[index] | ((uint)this._rh[index] << 16);

  private void SetReg32(int index, uint value) {
    this._r[index] = (ushort)value;
    this._rh[index] = (ushort)(value >> 16);
  }

  // ---- ModRM ----------------------------------------------------------------------------------

  private ushort? _segmentOverride;

  private ushort DataSegment => this._segmentOverride ?? this._ds;

  private (int Mode, int Reg, int Address) ModRm() {
    var modrm = this.Fetch();
    var mode = modrm >> 6;
    var reg = (modrm >> 3) & 7;
    var rm = modrm & 7;
    if (mode == 3)
      return (3, reg, rm);                              // the operand IS a register

    ushort offset;
    var segment = this.DataSegment;
    if (mode == 0 && rm == 6) {
      offset = this.FetchWord();
    } else {
      offset = rm switch {
        0 => (ushort)(this._r[_BX] + this._r[_SI]),
        1 => (ushort)(this._r[_BX] + this._r[_DI]),
        2 => (ushort)(this._r[_BP] + this._r[_SI]),
        3 => (ushort)(this._r[_BP] + this._r[_DI]),
        4 => this._r[_SI],
        5 => this._r[_DI],
        6 => this._r[_BP],
        _ => this._r[_BX],
      };
      if (rm is 2 or 3 or 6 && this._segmentOverride is null)
        segment = this._ss;                             // BP-relative addressing defaults to the stack
      if (mode == 1)
        offset = (ushort)(offset + (sbyte)this.Fetch());
      else if (mode == 2)
        offset = (ushort)(offset + this.FetchWord());
    }
    return (mode, reg, Linear(segment, offset));
  }

  private ushort GetRm16(int mode, int address) => mode == 3 ? this._r[address] : this.ReadWord(address);

  private void SetRm16(int mode, int address, ushort value) {
    if (mode == 3)
      this._r[address] = value;
    else
      this.WriteWord(address, value);
  }

  private byte GetRm8(int mode, int address) => mode == 3 ? this.Reg8(address) : this.ReadByte(address);

  private void SetRm8(int mode, int address, byte value) {
    if (mode == 3)
      this.SetReg8(address, value);
    else
      this.WriteByte(address, value);
  }

  // ---- flags ----------------------------------------------------------------------------------

  private void SetLogicFlags16(ushort value) {
    this._cf = this._of = false;
    this._zf = value == 0;
    this._sf = (value & 0x8000) != 0;
    this._pf = Parity((byte)value);
  }

  private void SetLogicFlags32(uint value) {
    this._cf = this._of = false;
    this._zf = value == 0;
    this._sf = (value & 0x80000000) != 0;
    this._pf = Parity((byte)value);
  }

  private void SetLogicFlags8(byte value) {
    this._cf = this._of = false;
    this._zf = value == 0;
    this._sf = (value & 0x80) != 0;
    this._pf = Parity(value);
  }

  private static bool Parity(byte value) {
    var bits = 0;
    for (var i = 0; i < 8; ++i)
      bits += (value >> i) & 1;
    return (bits & 1) == 0;
  }

  private ushort Add16(ushort a, ushort b, bool carry) {
    var sum = a + b + (carry ? 1 : 0);
    var result = (ushort)sum;
    this._cf = sum > 0xFFFF;
    this._af = (((a ^ b ^ result) & 0x10) != 0);
    this._of = ((~(a ^ b) & (a ^ result) & 0x8000) != 0);
    this._zf = result == 0;
    this._sf = (result & 0x8000) != 0;
    this._pf = Parity((byte)result);
    return result;
  }

  private ushort Sub16(ushort a, ushort b, bool borrow) {
    var difference = a - b - (borrow ? 1 : 0);
    var result = (ushort)difference;
    this._cf = difference < 0;
    this._af = (((a ^ b ^ result) & 0x10) != 0);
    this._of = (((a ^ b) & (a ^ result) & 0x8000) != 0);
    this._zf = result == 0;
    this._sf = (result & 0x8000) != 0;
    this._pf = Parity((byte)result);
    return result;
  }

  private byte Add8(byte a, byte b, bool carry) {
    var sum = a + b + (carry ? 1 : 0);
    var result = (byte)sum;
    this._cf = sum > 0xFF;
    this._of = ((~(a ^ b) & (a ^ result) & 0x80) != 0);
    this._zf = result == 0;
    this._sf = (result & 0x80) != 0;
    this._pf = Parity(result);
    return result;
  }

  private byte Sub8(byte a, byte b, bool borrow) {
    var difference = a - b - (borrow ? 1 : 0);
    var result = (byte)difference;
    this._cf = difference < 0;
    this._of = (((a ^ b) & (a ^ result) & 0x80) != 0);
    this._zf = result == 0;
    this._sf = (result & 0x80) != 0;
    this._pf = Parity(result);
    return result;
  }

  private ushort Alu16(int op, ushort a, ushort b) => op switch {
    0 => this.Add16(a, b, false),
    1 => Logic16(this, (ushort)(a | b)),
    2 => this.Add16(a, b, this._cf),
    3 => this.Sub16(a, b, this._cf),
    4 => Logic16(this, (ushort)(a & b)),
    5 => this.Sub16(a, b, false),
    6 => Logic16(this, (ushort)(a ^ b)),
    _ => this.Sub16(a, b, false),                      // CMP: flags only, result discarded
  };

  private static ushort Logic16(Cpu8086 cpu, ushort value) {
    cpu.SetLogicFlags16(value);
    return value;
  }

  private byte Alu8(int op, byte a, byte b) => op switch {
    0 => this.Add8(a, b, false),
    1 => Logic8(this, (byte)(a | b)),
    2 => this.Add8(a, b, this._cf),
    3 => this.Sub8(a, b, this._cf),
    4 => Logic8(this, (byte)(a & b)),
    5 => this.Sub8(a, b, false),
    6 => Logic8(this, (byte)(a ^ b)),
    _ => this.Sub8(a, b, false),
  };

  private static byte Logic8(Cpu8086 cpu, byte value) {
    cpu.SetLogicFlags8(value);
    return value;
  }

  private ushort Flags {
    get {
      ushort flags = 0x0002;
      if (this._cf) flags |= 0x0001;
      if (this._pf) flags |= 0x0004;
      if (this._af) flags |= 0x0010;
      if (this._zf) flags |= 0x0040;
      if (this._sf) flags |= 0x0080;
      if (this._df) flags |= 0x0400;
      if (this._of) flags |= 0x0800;
      return flags;
    }
    set {
      this._cf = (value & 0x0001) != 0;
      this._pf = (value & 0x0004) != 0;
      this._af = (value & 0x0010) != 0;
      this._zf = (value & 0x0040) != 0;
      this._sf = (value & 0x0080) != 0;
      this._df = (value & 0x0400) != 0;
      this._of = (value & 0x0800) != 0;
    }
  }

  private bool Condition(int code) => (code >> 1) switch {
    0 => this._of,
    1 => this._cf,
    2 => this._zf,
    3 => this._cf || this._zf,
    4 => this._sf,
    5 => this._pf,
    6 => this._sf != this._of,
    _ => this._zf || this._sf != this._of,
  } ^ ((code & 1) != 0);

  // ---- the instruction loop --------------------------------------------------------------------

  private void Step() {
    this._segmentOverride = null;
    var repeat = 0;                                     // 0 none, 1 REPNZ, 2 REPZ
    var operand32 = false;
    byte opcode;
    for (;;) {
      opcode = this.Fetch();
      switch (opcode) {
        case 0x26: this._segmentOverride = this._es; continue;
        case 0x2E: this._segmentOverride = this._cs; continue;
        case 0x36: this._segmentOverride = this._ss; continue;
        case 0x3E: this._segmentOverride = this._ds; continue;
        case 0x66: operand32 = true; continue;
        case 0xF2: repeat = 1; continue;
        case 0xF3: repeat = 2; continue;
      }
      break;
    }

    if (operand32) {
      this.StepDword(opcode, repeat);
      return;
    }

    switch (opcode) {
      // ---- ALU r/m,r and r,r/m ----
      case >= 0x00 and <= 0x3B when (opcode & 7) <= 3: {
        var op = opcode >> 3;
        var (mode, reg, address) = this.ModRm();
        var wide = (opcode & 1) != 0;
        var toReg = (opcode & 2) != 0;
        if (wide) {
          var a = toReg ? this._r[reg] : this.GetRm16(mode, address);
          var b = toReg ? this.GetRm16(mode, address) : this._r[reg];
          var result = this.Alu16(op, a, b);
          if (op != 7) {
            if (toReg) this._r[reg] = result; else this.SetRm16(mode, address, result);
          }
        } else {
          var a = toReg ? this.Reg8(reg) : this.GetRm8(mode, address);
          var b = toReg ? this.GetRm8(mode, address) : this.Reg8(reg);
          var result = this.Alu8(op, a, b);
          if (op != 7) {
            if (toReg) this.SetReg8(reg, result); else this.SetRm8(mode, address, result);
          }
        }
        return;
      }
      // ---- ALU AL/AX,imm ----
      case >= 0x04 and <= 0x3D when (opcode & 7) is 4 or 5: {
        var op = opcode >> 3;
        if ((opcode & 1) != 0) {
          var result = this.Alu16(op, this._r[_AX], this.FetchWord());
          if (op != 7)
            this._r[_AX] = result;
        } else {
          var result = this.Alu8(op, this.Reg8(_AX), this.Fetch());
          if (op != 7)
            this.SetReg8(_AX, result);
        }
        return;
      }

      case >= 0x40 and <= 0x47: {                       // INC r16 (CF untouched)
        var carry = this._cf;
        this._r[opcode - 0x40] = this.Add16(this._r[opcode - 0x40], 1, false);
        this._cf = carry;
        return;
      }
      case >= 0x48 and <= 0x4F: {                       // DEC r16
        var carry = this._cf;
        this._r[opcode - 0x48] = this.Sub16(this._r[opcode - 0x48], 1, false);
        this._cf = carry;
        return;
      }
      case >= 0x50 and <= 0x57: this.Push(this._r[opcode - 0x50]); return;
      case >= 0x58 and <= 0x5F: this._r[opcode - 0x58] = this.Pop(); return;

      case 0x06: this.Push(this._es); return;
      case 0x07: this._es = this.Pop(); return;
      case 0x0E: this.Push(this._cs); return;
      case 0x16: this.Push(this._ss); return;
      case 0x17: this._ss = this.Pop(); return;
      case 0x1E: this.Push(this._ds); return;
      case 0x1F: this._ds = this.Pop(); return;

      case 0x68: this.Push(this.FetchWord()); return;   // 186, but the emitter uses it
      case 0x69 or 0x6B: {                              // IMUL r16, r/m16, imm (186)
        var (mode, reg, address) = this.ModRm();
        var multiplicand = (short)this.GetRm16(mode, address);
        var multiplier = opcode == 0x69 ? (short)this.FetchWord() : (sbyte)this.Fetch();
        var product = multiplicand * multiplier;
        this._r[reg] = (ushort)product;
        this._cf = this._of = (short)this._r[reg] != product;
        return;
      }
      case 0x6A: this.Push((ushort)(sbyte)this.Fetch()); return;

      case >= 0x70 and <= 0x7F: {                       // Jcc rel8
        var delta = (sbyte)this.Fetch();
        if (this.Condition(opcode - 0x70))
          this._ip = (ushort)(this._ip + delta);
        return;
      }

      case 0x80 or 0x81 or 0x83: {                      // group 1: ALU r/m,imm
        var (mode, op, address) = this.ModRm();
        if (opcode == 0x80) {
          var result = this.Alu8(op, this.GetRm8(mode, address), this.Fetch());
          if (op != 7)
            this.SetRm8(mode, address, result);
        } else {
          var immediate = opcode == 0x81 ? this.FetchWord() : (ushort)(sbyte)this.Fetch();
          var result = this.Alu16(op, this.GetRm16(mode, address), immediate);
          if (op != 7)
            this.SetRm16(mode, address, result);
        }
        return;
      }

      case 0x84: { var (m, r, a) = this.ModRm(); this.SetLogicFlags8((byte)(this.GetRm8(m, a) & this.Reg8(r))); return; }
      case 0x85: { var (m, r, a) = this.ModRm(); this.SetLogicFlags16((ushort)(this.GetRm16(m, a) & this._r[r])); return; }
      case 0x86: { var (m, r, a) = this.ModRm(); var t = this.GetRm8(m, a); this.SetRm8(m, a, this.Reg8(r)); this.SetReg8(r, t); return; }
      case 0x87: { var (m, r, a) = this.ModRm(); var t = this.GetRm16(m, a); this.SetRm16(m, a, this._r[r]); this._r[r] = t; return; }

      case 0x88: { var (m, r, a) = this.ModRm(); this.SetRm8(m, a, this.Reg8(r)); return; }
      case 0x89: { var (m, r, a) = this.ModRm(); this.SetRm16(m, a, this._r[r]); return; }
      case 0x8A: { var (m, r, a) = this.ModRm(); this.SetReg8(r, this.GetRm8(m, a)); return; }
      case 0x8B: { var (m, r, a) = this.ModRm(); this._r[r] = this.GetRm16(m, a); return; }
      case 0x8C: { var (m, r, a) = this.ModRm(); this.SetRm16(m, a, this.Segment(r)); return; }
      case 0x8E: { var (m, r, a) = this.ModRm(); this.SetSegment(r, this.GetRm16(m, a)); return; }
      case 0x8D: {                                      // LEA: the ADDRESS, not the contents
        var (mode, reg, address) = this.ModRm();
        if (mode == 3)
          throw new Cpu8086Exception("LEA with a register operand");
        this._r[reg] = (ushort)(address - Linear(this.DataSegment, 0));
        return;
      }

      case 0x90: return;                                // NOP
      case >= 0x91 and <= 0x97: {
        var index = opcode - 0x90;
        (this._r[_AX], this._r[index]) = (this._r[index], this._r[_AX]);
        return;
      }
      case 0x98: this._r[_AX] = (ushort)(sbyte)this.Reg8(_AX); return;                     // CBW
      case 0x99: this._r[_DX] = (ushort)((this._r[_AX] & 0x8000) != 0 ? 0xFFFF : 0); return; // CWD
      case 0x9C: this.Push(this.Flags); return;
      case 0x9D: this.Flags = this.Pop(); return;
      case 0x9E: {                                      // SAHF
        var ah = this.Reg8(4);
        this._cf = (ah & 0x01) != 0; this._pf = (ah & 0x04) != 0; this._af = (ah & 0x10) != 0;
        this._zf = (ah & 0x40) != 0; this._sf = (ah & 0x80) != 0;
        return;
      }
      case 0x9F: this.SetReg8(4, (byte)(this.Flags & 0xD5)); return;                        // LAHF

      case 0xA0: this.SetReg8(_AX, this.ReadByte(Linear(this.DataSegment, this.FetchWord()))); return;
      case 0xA1: this._r[_AX] = this.ReadWord(Linear(this.DataSegment, this.FetchWord())); return;
      case 0xA2: this.WriteByte(Linear(this.DataSegment, this.FetchWord()), this.Reg8(_AX)); return;
      case 0xA3: this.WriteWord(Linear(this.DataSegment, this.FetchWord()), this._r[_AX]); return;

      case 0xA4 or 0xA5 or 0xAA or 0xAB or 0xAC or 0xAD or 0xAE or 0xAF or 0xA6 or 0xA7:
        this.StringOp(opcode, repeat);
        return;

      case 0xA8: this.SetLogicFlags8((byte)(this.Reg8(_AX) & this.Fetch())); return;
      case 0xA9: this.SetLogicFlags16((ushort)(this._r[_AX] & this.FetchWord())); return;

      case >= 0xB0 and <= 0xB7: this.SetReg8(opcode - 0xB0, this.Fetch()); return;
      case >= 0xB8 and <= 0xBF: this._r[opcode - 0xB8] = this.FetchWord(); return;

      case 0xC0 or 0xC1: {                              // 186 shift by immediate
        var (mode, op, address) = this.ModRm();
        var count = this.Fetch();
        if (opcode == 0xC0)
          this.SetRm8(mode, address, this.Shift8(op, this.GetRm8(mode, address), count));
        else
          this.SetRm16(mode, address, this.Shift16(op, this.GetRm16(mode, address), count));
        return;
      }
      case 0xC2: { var pop = this.FetchWord(); this._ip = this.Pop(); this._r[_SP] += pop; return; }
      case 0xC3: this._ip = this.Pop(); return;
      case 0xC6: { var (m, _, a) = this.ModRm(); this.SetRm8(m, a, this.Fetch()); return; }
      case 0xC7: { var (m, _, a) = this.ModRm(); this.SetRm16(m, a, this.FetchWord()); return; }
      case 0xCB: { this._ip = this.Pop(); this._cs = this.Pop(); return; }
      case 0xCD: this.Interrupt(this.Fetch()); return;
      case 0xCF: { this._ip = this.Pop(); this._cs = this.Pop(); this.Flags = this.Pop(); return; }

      case >= 0xD0 and <= 0xD3: {
        var (mode, op, address) = this.ModRm();
        var count = (opcode & 2) != 0 ? this.Reg8(_CX) : (byte)1;
        if ((opcode & 1) == 0)
          this.SetRm8(mode, address, this.Shift8(op, this.GetRm8(mode, address), count));
        else
          this.SetRm16(mode, address, this.Shift16(op, this.GetRm16(mode, address), count));
        return;
      }

      case 0x9B: return;                                // FWAIT - nothing to synchronise with here
      case >= 0xD8 and <= 0xDF: this.X87(opcode); return;

      case 0xE0 or 0xE1 or 0xE2: {                      // LOOPNZ / LOOPZ / LOOP
        var delta = (sbyte)this.Fetch();
        var taken = --this._r[_CX] != 0
          && (opcode == 0xE2 || (opcode == 0xE1 ? this._zf : !this._zf));
        if (taken)
          this._ip = (ushort)(this._ip + delta);
        return;
      }
      case 0xE3: { var delta = (sbyte)this.Fetch(); if (this._r[_CX] == 0) this._ip = (ushort)(this._ip + delta); return; }
      case 0xE8: { var delta = (short)this.FetchWord(); this.Push(this._ip); this._ip = (ushort)(this._ip + delta); return; }
      case 0xE9: { var delta = (short)this.FetchWord(); this._ip = (ushort)(this._ip + delta); return; }
      case 0xEA: { var offset = this.FetchWord(); this._cs = this.FetchWord(); this._ip = offset; return; }
      case 0xEB: { var delta = (sbyte)this.Fetch(); this._ip = (ushort)(this._ip + delta); return; }
      case 0x9A: { var offset = this.FetchWord(); var segment = this.FetchWord(); this.Push(this._cs); this.Push(this._ip); this._cs = segment; this._ip = offset; return; }

      case 0xF4: this._halted = true; return;
      case 0xF5: this._cf = !this._cf; return;
      case 0xF8: this._cf = false; return;
      case 0xF9: this._cf = true; return;
      case 0xFA or 0xFB: return;                        // CLI/STI - no interrupts are delivered here
      case 0xFC: this._df = false; return;
      case 0xFD: this._df = true; return;

      case 0x0F: this.TwoByte(); return;
      case 0xF6 or 0xF7: this.Group3(opcode); return;
      case 0xFE or 0xFF: this.Group45(opcode); return;
      // POP r/m16. The compiler emits it for ON ERROR's handler save/restore and for DRAW's
      // no-update prefix, and the interpreter simply had no case for it.
      case 0x8F: { var (mode, _, address) = this.ModRm(); this.SetRm16(mode, address, this.Pop()); return; }

      default:
        throw new Cpu8086Exception(
          $"unimplemented opcode {opcode:X2} at {this._cs:X4}:{this._ip - 1:X4} " +
          $"SP={this._r[_SP]:X4} BP={this._r[_BP]:X4}");
    }
  }

  /// <summary>
  /// The operand-size-prefixed operations required by the executable 386 differentials: static
  /// zero-fill and copies, native LONG loop arithmetic, LONG shifts, and QUAD bitwise/shift halves.
  /// Every other 32-bit encoding is still rejected loudly; this subset grows only alongside compiler
  /// output with a behavioral oracle.
  /// </summary>
  private void StepDword(byte opcode, int repeat) {
    if (opcode is >= 0xB8 and <= 0xBF && repeat == 0) { // MOV r32,imm32
      var low = this.FetchWord();
      var high = this.FetchWord();
      this.SetReg32(opcode - 0xB8, low | ((uint)high << 16));
      return;
    }
    if (opcode is >= 0x00 and <= 0x3B && (opcode & 7) is 1 or 3 && repeat == 0) {
      var operation = opcode >> 3;
      var (mode, register, address) = this.ModRm();
      var toRegister = (opcode & 2) != 0;
      var left = toRegister ? this.Reg32(register) : this.GetRm32(mode, address);
      var right = toRegister ? this.GetRm32(mode, address) : this.Reg32(register);
      var result = this.Alu32(operation, left, right);
      if (operation != 7) {
        if (toRegister)
          this.SetReg32(register, result);
        else
          this.SetRm32(mode, address, result);
      }
      return;
    }
    if (opcode is 0x81 or 0x83 && repeat == 0) {       // ALU r/m32,imm32/sign-extended imm8
      var (mode, operation, address) = this.ModRm();
      var immediate = opcode == 0x83
        ? unchecked((uint)(int)(sbyte)this.Fetch())
        : this.FetchWord() | ((uint)this.FetchWord() << 16);
      var result = this.Alu32(operation, this.GetRm32(mode, address), immediate);
      if (operation != 7)
        this.SetRm32(mode, address, result);
      return;
    }
    if (opcode == 0xC7 && repeat == 0) {               // MOV r/m32,imm32
      var (mode, operation, address) = this.ModRm();
      if (operation != 0)
        throw new Cpu8086Exception($"unimplemented dword C7 operation /{operation}");
      var value = this.FetchWord() | ((uint)this.FetchWord() << 16);
      this.SetRm32(mode, address, value);
      return;
    }
    if (opcode == 0x8B && repeat == 0) {                 // MOV r32,m32
      var (mode, register, address) = this.ModRm();
      this.SetReg32(register, this.GetRm32(mode, address));
      return;
    }
    if (opcode == 0x89 && repeat == 0) {                 // MOV m32,r32
      var (mode, register, address) = this.ModRm();
      this.SetRm32(mode, address, this.Reg32(register));
      return;
    }
    if (opcode == 0xA1 && repeat == 0) {                 // MOV EAX,moffs32
      this.SetReg32(_AX, this.ReadDword(Linear(this.DataSegment, this.FetchWord())));
      return;
    }
    if (opcode == 0xA3 && repeat == 0) {                 // MOV moffs32,EAX
      this.WriteDword(Linear(this.DataSegment, this.FetchWord()), this.Reg32(_AX));
      return;
    }
    if (opcode == 0xA5 && repeat == 2) {                 // REP MOVSD copies DS:SI to ES:DI
      var step = (ushort)(this._df ? -4 : 4);
      while (this._r[_CX] != 0) {
        var source = Linear(this.DataSegment, this._r[_SI]);
        var destination = Linear(this._es, this._r[_DI]);
        this.WriteDword(destination, this.ReadDword(source));
        this._r[_SI] += step;
        this._r[_DI] += step;
        --this._r[_CX];
      }
      return;
    }
    if (opcode == 0xAB && repeat == 2) {                 // REP STOSD stores EAX into ES:EDI
      var value = this.Reg32(_AX);
      while (this._r[_CX] != 0) {
        this.WriteDword(Linear(this._es, this._r[_DI]), value);
        this._r[_DI] += (ushort)(this._df ? -4 : 4);
        --this._r[_CX];
      }
      return;
    }
    if (opcode == 0x99 && repeat == 0) {                 // CDQ: sign-extend EAX into EDX:EAX
      this.SetReg32(_DX, (this.Reg32(_AX) & 0x80000000) != 0 ? uint.MaxValue : 0);
      return;
    }
    if (opcode == 0xF7 && repeat == 0) {
      this.Group3Dword();
      return;
    }
    if (opcode == 0xC1 && repeat == 0) {                 // SHL/SHR/SAR dword,imm8
      var (mode, operation, address) = this.ModRm();
      var count = this.Fetch();
      var value = mode == 3 ? this.Reg32(address) : this.ReadDword(address);
      var result = this.Shift32(operation, value, count);
      if (mode == 3)
        this.SetReg32(address, result);
      else
        this.WriteDword(address, result);
      return;
    }
    if (opcode == 0x0F && repeat == 0) {
      this.StepDwordTwoByte();
      return;
    }
    throw new Cpu8086Exception($"unimplemented opcode 66 {opcode:X2} at {this._cs:X4}:{this._ip - 2:X4}");
  }

  private void StepDwordTwoByte() {
    var opcode = this.Fetch();
    if (opcode is not (0xA4 or 0xAC))
      throw new Cpu8086Exception($"unimplemented opcode 66 0F {opcode:X2}");

    var (mode, source, destination) = this.ModRm();
    if (mode != 3)
      throw new Cpu8086Exception("only register dword SHLD/SHRD is supported");
    var count = this.Fetch() & 0x1F;
    if (count == 0)
      return;

    var original = this.Reg32(destination);
    var other = this.Reg32(source);
    var result = opcode == 0xA4
      ? original << count | other >> (32 - count)
      : original >> count | other << (32 - count);
    this._cf = opcode == 0xA4
      ? ((original >> (32 - count)) & 1) != 0
      : ((original >> (count - 1)) & 1) != 0;
    this.SetReg32(destination, result);
    this._zf = result == 0;
    this._sf = (result & 0x80000000) != 0;
    this._pf = Parity((byte)result);
    if (count == 1)
      this._of = opcode == 0xA4
        ? this._sf ^ this._cf
        : ((original ^ result) & 0x80000000) != 0;
  }

  /// <summary>The operand-size-prefixed TEST/NOT/NEG/MUL/IMUL/DIV/IDIV group.</summary>
  private void Group3Dword() {
    var (mode, operation, address) = this.ModRm();
    var operand = this.GetRm32(mode, address);
    switch (operation) {
      case 0 or 1:                                      // TEST r/m32,imm32
        this.SetLogicFlags32(operand & (this.FetchWord() | ((uint)this.FetchWord() << 16)));
        return;
      case 2:                                           // NOT
        this.SetRm32(mode, address, ~operand);
        return;
      case 3:                                           // NEG
        this.SetRm32(mode, address, this.Sub32(0, operand, false));
        return;
      case 4: {                                         // MUL EDX:EAX = EAX * r/m32
        var product = (ulong)this.Reg32(_AX) * operand;
        this.SetReg32(_AX, (uint)product);
        this.SetReg32(_DX, (uint)(product >> 32));
        this._cf = this._of = (product >> 32) != 0;
        return;
      }
      case 5: {                                         // IMUL EDX:EAX = EAX * r/m32
        var product = (long)(int)this.Reg32(_AX) * (int)operand;
        var low = (uint)product;
        this.SetReg32(_AX, low);
        this.SetReg32(_DX, (uint)((ulong)product >> 32));
        this._cf = this._of = product != (long)(int)low;
        return;
      }
      case 6: {                                         // DIV EDX:EAX / r/m32
        if (operand == 0)
          throw new Cpu8086Exception("divide by zero (dword DIV)");
        var dividend = ((ulong)this.Reg32(_DX) << 32) | this.Reg32(_AX);
        var quotient = dividend / operand;
        if (quotient > uint.MaxValue)
          throw new Cpu8086Exception("divide overflow (dword DIV)");
        this.SetReg32(_AX, (uint)quotient);
        this.SetReg32(_DX, (uint)(dividend % operand));
        return;
      }
      default: {                                        // IDIV EDX:EAX / r/m32
        var divisor = (int)operand;
        if (divisor == 0)
          throw new Cpu8086Exception("divide by zero (dword IDIV)");
        var dividend = ((long)(int)this.Reg32(_DX) << 32) | this.Reg32(_AX);
        if (dividend == long.MinValue && divisor == -1)
          throw new Cpu8086Exception("divide overflow (dword IDIV)");
        var quotient = dividend / divisor;
        if (quotient is < int.MinValue or > int.MaxValue)
          throw new Cpu8086Exception("divide overflow (dword IDIV)");
        this.SetReg32(_AX, (uint)(int)quotient);
        this.SetReg32(_DX, (uint)(int)(dividend % divisor));
        return;
      }
    }
  }

  // ---- x87 ---------------------------------------------------------------------------------------

  /// <summary>
  /// One x87 value. FILD must retain every bit of a signed 64-bit integer: extended precision has a
  /// 64-bit significand, while a host double has only 53. Keeping that integral form alongside the
  /// floating approximation prevents FILD/FISTP and integral x87 arithmetic from losing low bits.
  /// </summary>
  private readonly record struct X87Value(double Approximation, Int128? Integer = null) {
    public double AsDouble => this.Integer is { } exact ? (double)exact : this.Approximation;

    public static X87Value Exact(Int128 value) => new(0, value);
    public static X87Value Floating(double value) => new(value);

    public X87Value Abs() => this.Integer is { } exact
      ? Exact(Int128.Abs(exact))
      : Floating(Math.Abs(this.Approximation));

    public X87Value Negate() => this.Integer is { } exact
      ? Exact(-exact)
      : Floating(-this.Approximation);
  }

  private readonly X87Value[] _st = new X87Value[8];
  private int _top = 8;                                 // the stack grows DOWN; 8 means empty
  private ushort _status;                               // only the condition-code bits are modelled

  private X87Value St(int index) => this._st[(this._top + index) & 7];
  private void SetSt(int index, X87Value value) => this._st[(this._top + index) & 7] = value;
  private void SetFloatingSt(int index, double value) => this.SetSt(index, X87Value.Floating(value));

  private void FPush(double value) {
    this._top = (this._top - 1) & 7;
    this._st[this._top & 7] = X87Value.Floating(value);
  }

  private void FPush(X87Value value) {
    this._top = (this._top - 1) & 7;
    this._st[this._top & 7] = value;
  }

  private void FPushInteger(Int128 value) {
    this._top = (this._top - 1) & 7;
    this._st[this._top & 7] = X87Value.Exact(value);
  }

  private void FPop() => this._top = (this._top + 1) & 7;

  /// <summary>
  /// x87, with exact integral values and host doubles standing in for non-integral extended values.
  ///
  /// That approximation is deliberate and it bounds what this interpreter may be used for. Both
  /// non-integral values in a differential comparison run on the SAME approximation. Integer loads
  /// are not allowed that shortcut: the hardware represents every signed qword exactly, and reducing
  /// one to 53 bits made the two code generators appear to disagree when only the interpreter did.
  /// This is still NOT a statement about matching the eleven extra fraction bits of a real 8087 for
  /// non-integral temporaries; the golden battery remains the authority there.
  /// </summary>
  private void X87(byte opcode) {
    var start = this._ip - 1;
    if (this.ReadByte(Linear(this._cs, this._ip)) >= 0xC0) {
      this.X87Register(opcode, this.Fetch(), start);
      return;
    }

    var (_, reg, address) = this.ModRm();
    switch (opcode) {
      case 0xD9 when reg == 0: this.FPush(BitConverter.Int32BitsToSingle((int)this.ReadDword(address))); return;
      case 0xD9 when reg is 2 or 3:
        this.WriteDword(address, (uint)BitConverter.SingleToInt32Bits((float)this.St(0).AsDouble));
        if (reg == 3) this.FPop();
        return;
      // FLDCW. Ignoring this used to be harmless-looking and was not: INT and FIX are implemented by
      // setting the ROUNDING MODE and calling FRNDINT, so a CPU that always rounds to nearest turns
      // INT(2.7) into 3. The direct emitter was right on real hardware the whole time and this said
      // otherwise, which is exactly backwards for a reference implementation.
      case 0xD9 when reg == 5: this._controlWord = this.ReadWord(address); return;
      case 0xD9 when reg == 7: this.WriteWord(address, this._controlWord); return;  // FSTCW/FNSTCW
      case 0xDD when reg == 0: this.FPush(BitConverter.Int64BitsToDouble((long)this.ReadQword(address))); return;
      case 0xDD when reg is 2 or 3:
        this.WriteQword(address, (ulong)BitConverter.DoubleToInt64Bits(this.St(0).AsDouble));
        if (reg == 3) this.FPop();
        return;
      // FSTSW m2byte - the 8087 way to get at the condition codes a compare just set. The 287's
      // FSTSW AX does not exist on this target, so this is the only way, and ROUND needs it to tell
      // which side of zero a value was on before taking its magnitude.
      case 0xDD when reg == 7: this.WriteWord(address, this._status); return;
      case 0xDB when reg == 0: this.FPushInteger((int)this.ReadDword(address)); return;
      case 0xDB when reg is 2 or 3:
        this.WriteDword(address, (uint)NarrowInt32(this.RoundToInteger(this.St(0))));
        if (reg == 3) this.FPop();
        return;
      case 0xDB when reg == 5: this.FPush(this.ReadExtended(address)); return;
      case 0xDB when reg == 7: this.WriteExtended(address, this.St(0).AsDouble); this.FPop(); return;
      case 0xDF when reg == 0: this.FPushInteger((short)this.ReadWord(address)); return;
      case 0xDF when reg is 2 or 3:
        this.WriteWord(address, (ushort)NarrowInt16(this.RoundToInteger(this.St(0))));
        if (reg == 3) this.FPop();
        return;
      case 0xDF when reg == 5: this.FPushInteger((long)this.ReadQword(address)); return;
      case 0xDF when reg == 7:
        this.WriteQword(address, (ulong)NarrowInt64(this.RoundToInteger(this.St(0))));
        this.FPop();
        return;
      case 0xD8: this.MemoryArithmetic(reg,
        X87Value.Floating(BitConverter.Int32BitsToSingle((int)this.ReadDword(address)))); return;
      case 0xDC: this.MemoryArithmetic(reg,
        X87Value.Floating(BitConverter.Int64BitsToDouble((long)this.ReadQword(address)))); return;
      case 0xDA: this.MemoryArithmetic(reg, X87Value.Exact((int)this.ReadDword(address))); return;
      case 0xDE: this.MemoryArithmetic(reg, X87Value.Exact((short)this.ReadWord(address))); return;
      default:
        throw new Cpu8086Exception($"unimplemented x87 {opcode:X2} /{reg} at {this._cs:X4}:{start:X4}");
    }
  }

  private void MemoryArithmetic(int reg, X87Value operand) {
    if (reg is 2 or 3) {                                // FCOM / FCOMP
      this.Compare(this.St(0), operand);
      if (reg == 3)
        this.FPop();
      return;
    }
    this.SetSt(0, Arithmetic(reg, this.St(0), operand));
  }

  private void X87Register(byte opcode, byte modrm, int start) {
    var index = modrm & 7;
    switch (opcode, modrm) {
      case (0xDB, 0xE3): this._top = 8; this._status = 0; return;             // FINIT / FNINIT
      case (0xDB, 0xE2): this._status = 0; return;                            // FNCLEX
      case (0xD9, 0xE0): this.SetSt(0, this.St(0).Negate()); return;          // FCHS
      case (0xD9, 0xE1): this.SetSt(0, this.St(0).Abs()); return;             // FABS
      case (0xD9, 0xE4): this.Compare(this.St(0), X87Value.Exact(0)); return; // FTST
      case (0xD9, 0xE8): this.FPushInteger(1); return;                        // FLD1
      case (0xD9, 0xEE): this.FPushInteger(0); return;                        // FLDZ
      case (0xD9, 0xEB): this.FPush(Math.PI); return;                         // FLDPI
      case (0xD9, 0xE9): this.FPush(Math.Log2(10)); return;                   // FLDL2T
      case (0xD9, 0xEA): this.FPush(Math.Log2(Math.E)); return;               // FLDL2E
      case (0xD9, 0xEC): this.FPush(Math.Log10(2)); return;                   // FLDLG2
      case (0xD9, 0xED): this.FPush(Math.Log(2)); return;                     // FLDLN2
      case (0xD9, 0xFA): this.SetFloatingSt(0, Math.Sqrt(this.St(0).AsDouble)); return; // FSQRT
      case (0xD9, 0xFE): this.SetFloatingSt(0, Math.Sin(this.St(0).AsDouble)); return;  // FSIN
      case (0xD9, 0xFF): this.SetFloatingSt(0, Math.Cos(this.St(0).AsDouble)); return;  // FCOS
      // FPTAN replaces ST(0) with its tangent and then PUSHES 1.0 - the extra push is why every
      // caller follows it with an FSTP that throws the 1.0 away
      case (0xD9, 0xF2):
        this.SetFloatingSt(0, Math.Tan(this.St(0).AsDouble));
        this.FPushInteger(1);
        return;
      case (0xD9, 0xFC): this.SetSt(0, X87Value.Exact(this.RoundToInteger(this.St(0)))); return; // FRNDINT
      case (0xD9, 0xF0):
        this.SetFloatingSt(0, Math.Pow(2, this.St(0).AsDouble) - 1);
        return;
      case (0xD9, 0xF1): {                                                    // FYL2X
        var y = this.St(1);
        var x = this.St(0);
        this.FPop();
        this.SetFloatingSt(0, y.AsDouble * Math.Log2(x.AsDouble));
        return;
      }
      case (0xD9, 0xF3): {                                                    // FPATAN
        var y = this.St(1);
        var x = this.St(0);
        this.FPop();
        this.SetFloatingSt(0, Math.Atan2(y.AsDouble, x.AsDouble));
        return;
      }
      case (0xD9, 0xFD): {                                                    // FSCALE
        this.SetFloatingSt(0, this.St(0).AsDouble * Math.Pow(2, Math.Truncate(this.St(1).AsDouble)));
        return;
      }
      case (0xD9, 0xF8): {                                                    // FPREM
        var a = this.St(0).AsDouble;
        var b = this.St(1).AsDouble;
        var quotient = b == 0 ? 0 : Math.Truncate(a / b);
        this.SetFloatingSt(0, b == 0 ? double.NaN : a - b * quotient);
        // C2 clear: the reduction completed. C0, C3 and C1 carry the low THREE bits of the
        // quotient - bit 2, bit 1 and bit 0 in that order - which is what a range reduction reads
        // them for: reducing modulo pi/2 leaves the quadrant in them. Clearing C2 without setting
        // those left a caller reading whatever the previous FSTSW happened to have.
        var bits = (long)Math.Abs(quotient);
        this._status &= unchecked((ushort)~0x4700);                           // C0|C1|C2|C3
        if ((bits & 1) != 0)
          this._status |= 0x0200;                                             // C1 = quotient bit 0
        if ((bits & 2) != 0)
          this._status |= 0x4000;                                             // C3 = quotient bit 1
        if ((bits & 4) != 0)
          this._status |= 0x0100;                                             // C0 = quotient bit 2
        return;
      }
      case (0xDE, 0xD9): {                                                    // FCOMPP
        this.Compare(this.St(0), this.St(1));
        this.FPop();
        this.FPop();
        return;
      }
      case (0xDF, 0xE0):                                                      // FSTSW AX
        this._r[_AX] = (ushort)((this._r[_AX] & 0x00FF) | (this._status & 0xFF00));
        return;
    }

    switch (opcode) {
      case 0xD9 when modrm is >= 0xC0 and <= 0xC7: this.FPush(this.St(index)); return;      // FLD st(i)
      case 0xD9 when modrm is >= 0xC8 and <= 0xCF: {                                        // FXCH
        var swapped = this.St(0);
        this.SetSt(0, this.St(index));
        this.SetSt(index, swapped);
        return;
      }
      case 0xDD when modrm is >= 0xC0 and <= 0xC7: return;                                  // FFREE
      case 0xDD when modrm is >= 0xD0 and <= 0xD7: this.SetSt(index, this.St(0)); return;   // FST st(i)
      case 0xDD when modrm is >= 0xD8 and <= 0xDF:                                          // FSTP st(i)
        this.SetSt(index, this.St(0));
        this.FPop();
        return;
      case 0xD8 when modrm is >= 0xD0 and <= 0xDF:                                          // FCOM / FCOMP st(i)
        this.Compare(this.St(0), this.St(index));
        if (modrm >= 0xD8)
          this.FPop();
        return;
      case 0xD8 or 0xDC or 0xDE: {
        // D8 computes into ST(0); DC and DE compute into ST(i), and DE pops afterwards.
        //
        // And the DC/DE register forms SWAP the reversing pair: DE /5 is FSUBP (ST(i) - ST(0)) while
        // D8 /4 is FSUB (ST(0) - ST(i)) - the encodings for FSUB/FSUBR and FDIV/FDIVR exchange places
        // between the two directions. Reading /5 as "reverse" in both is a sign flip on every
        // subtraction that reaches the FPU, which is exactly what PB's float-shaped integer
        // arithmetic does.
        var op = (modrm >> 3) & 7;
        var intoStack0 = opcode == 0xD8;
        if (!intoStack0 && op >= 4)
          op ^= 1;
        var result = intoStack0
          ? Arithmetic(op, this.St(0), this.St(index))
          : Arithmetic(op, this.St(index), this.St(0));
        if (intoStack0)
          this.SetSt(0, result);
        else
          this.SetSt(index, result);
        if (opcode == 0xDE)
          this.FPop();
        return;
      }
      default:
        throw new Cpu8086Exception($"unimplemented x87 {opcode:X2} {modrm:X2} at {this._cs:X4}:{start:X4}");
    }
  }

  private static X87Value Arithmetic(int op, X87Value a, X87Value b) {
    if (a.Integer is { } ai && b.Integer is { } bi) {
      var exact = op switch {
        0 => ai + bi,
        1 => ai * bi,
        4 => ai - bi,
        5 => bi - ai,
        6 when bi != 0 && ai % bi == 0 => ai / bi,
        7 when ai != 0 && bi % ai == 0 => bi / ai,
        _ => (Int128?)null,
      };
      // A signed QUAD result is within this range, and every such integer fits the x87's 64-bit
      // significand exactly. Wider intermediate integers fall back to the floating approximation.
      if (exact.HasValue && exact.Value >= long.MinValue && exact.Value <= long.MaxValue)
        return X87Value.Exact(exact.Value);
    }

    var ad = a.AsDouble;
    var bd = b.AsDouble;
    return X87Value.Floating(op switch {
      0 => ad + bd,
      1 => ad * bd,
      4 => ad - bd,
      5 => bd - ad,                                    // the R forms reverse the operands
      6 => ad / bd,
      7 => bd / ad,
      _ => throw new Cpu8086Exception($"unimplemented x87 arithmetic {op}"),
    });
  }

  /// <summary>FISTP stores by the control word, which FINIT leaves at nearest-ties-to-even.</summary>
  /// <summary>The x87 control word; only its rounding-control field (bits 11-10) is modelled.</summary>
  private ushort _controlWord = 0x037F;

  /// <summary>
  /// FRNDINT and the integer stores round by the control word's RC field, not always to nearest:
  /// 00 nearest-even, 01 toward -infinity (BASIC's INT), 10 toward +infinity, 11 toward zero (FIX).
  /// </summary>
  private Int128 RoundToInteger(X87Value value) {
    if (value.Integer is { } exact)
      return exact;
    var rounded = (this._controlWord & 0x0C00) switch {
      0x0400 => Math.Floor(value.Approximation),
      0x0800 => Math.Ceiling(value.Approximation),
      0x0C00 => Math.Truncate(value.Approximation),
      _ => Math.Round(value.Approximation, MidpointRounding.ToEven),
    };
    return double.IsFinite(rounded) && rounded >= (double)Int128.MinValue && rounded <= (double)Int128.MaxValue
      ? (Int128)rounded
      : Int128.MinValue;
  }

  private static short NarrowInt16(Int128 value)
    => value >= short.MinValue && value <= short.MaxValue ? (short)value : short.MinValue;

  private static int NarrowInt32(Int128 value)
    => value >= int.MinValue && value <= int.MaxValue ? (int)value : int.MinValue;

  private static long NarrowInt64(Int128 value)
    => value >= long.MinValue && value <= long.MaxValue ? (long)value : long.MinValue;

  /// <summary>
  /// The comparison condition codes, in the status-word bits FSTSW hands to SAHF
  /// (C3 -&gt; ZF, C0 -&gt; CF).
  /// </summary>
  private void Compare(X87Value a, X87Value b) {
    this._status = (ushort)(this._status & ~0x4700);
    if (a.Integer is { } ai && b.Integer is { } bi) {
      if (ai < bi)
        this._status |= 0x0100;
      else if (ai == bi)
        this._status |= 0x4000;
      return;
    }
    var ad = a.AsDouble;
    var bd = b.AsDouble;
    if (double.IsNaN(ad) || double.IsNaN(bd))
      this._status |= 0x4500;                          // unordered: C3 C2 C0
    else if (ad < bd)
      this._status |= 0x0100;                          // C0
    else if (ad == bd)
      this._status |= 0x4000;                          // C3
  }

  private uint ReadDword(int at) => (uint)(this.ReadWord(at) | (this.ReadWord(at + 2) << 16));

  private void WriteDword(int at, uint value) {
    this.WriteWord(at, (ushort)value);
    this.WriteWord(at + 2, (ushort)(value >> 16));
  }

  private ulong ReadQword(int at) => this.ReadDword(at) | ((ulong)this.ReadDword(at + 4) << 32);

  private void WriteQword(int at, ulong value) {
    this.WriteDword(at, (uint)value);
    this.WriteDword(at + 4, (uint)(value >> 32));
  }

  // an 80-bit extended value read into (and written from) a double - the mantissa bits that do not
  // fit are exactly the approximation this interpreter is explicit about
  private double ReadExtended(int at) {
    var mantissa = this.ReadQword(at);
    var signExponent = this.ReadWord(at + 8);
    var exponent = signExponent & 0x7FFF;
    var negative = (signExponent & 0x8000) != 0;
    if (exponent == 0 && mantissa == 0)
      return negative ? -0.0 : 0.0;
    var value = mantissa * Math.Pow(2, exponent - 16383 - 63);
    return negative ? -value : value;
  }

  private void WriteExtended(int at, double value) {
    if (value == 0) {
      this.WriteQword(at, 0);
      this.WriteWord(at + 8, (ushort)(double.IsNegative(value) ? 0x8000 : 0));
      return;
    }
    var negative = value < 0;
    var magnitude = Math.Abs(value);
    var exponent = (int)Math.Floor(Math.Log2(magnitude));
    var mantissa = (ulong)Math.Round(magnitude * Math.Pow(2, 63 - exponent));
    this.WriteQword(at, mantissa);
    this.WriteWord(at + 8, (ushort)((negative ? 0x8000 : 0) | (exponent + 16383)));
  }

  /// <summary>
  /// The 386 two-byte opcodes the optimizer emits under <c>$CPU 80386</c>: near conditional jumps
  /// (an 8-bit displacement does not always reach), <c>SETcc</c>, <c>MOVZX</c>/<c>MOVSX</c>, the
  /// two-operand <c>IMUL</c> and the double shifts. Anything else in the space throws.
  /// </summary>
  private void TwoByte() {
    var opcode = this.Fetch();
    switch (opcode) {
      case >= 0x80 and <= 0x8F: {                       // Jcc rel16
        var delta = (short)this.FetchWord();
        if (this.Condition(opcode - 0x80))
          this._ip = (ushort)(this._ip + delta);
        return;
      }
      case >= 0x90 and <= 0x9F: {                       // SETcc r/m8
        var (mode, _, address) = this.ModRm();
        this.SetRm8(mode, address, (byte)(this.Condition(opcode - 0x90) ? 1 : 0));
        return;
      }
      case 0xAF: {                                      // IMUL r16, r/m16
        var (mode, reg, address) = this.ModRm();
        var product = (short)this._r[reg] * (short)this.GetRm16(mode, address);
        this._r[reg] = (ushort)product;
        this._cf = this._of = (short)this._r[reg] != product;
        return;
      }
      case 0xB6: { var (m, r, a) = this.ModRm(); this._r[r] = this.GetRm8(m, a); return; }               // MOVZX r16, r/m8
      case 0xB7: { var (m, r, a) = this.ModRm(); this._r[r] = this.GetRm16(m, a); return; }
      case 0xBE: { var (m, r, a) = this.ModRm(); this._r[r] = (ushort)(sbyte)this.GetRm8(m, a); return; } // MOVSX r16, r/m8
      case 0xBF: { var (m, r, a) = this.ModRm(); this._r[r] = this.GetRm16(m, a); return; }
      case 0xA4 or 0xAC: {                              // SHLD / SHRD r/m16, r16, imm8
        var (mode, reg, address) = this.ModRm();
        var count = this.Fetch() & 0x1F;
        if (count == 0)
          return;
        var value = this.GetRm16(mode, address);
        var other = this._r[reg];
        var pair = opcode == 0xA4
          ? ((uint)value << 16 | other) << count
          : ((uint)other << 16 | value) >> count;
        value = (ushort)(opcode == 0xA4 ? pair >> 16 : pair);
        this.SetRm16(mode, address, value);
        this._zf = value == 0;
        this._sf = (value & 0x8000) != 0;
        return;
      }
      default:
        throw new Cpu8086Exception($"unimplemented 0F {opcode:X2} at {this._cs:X4}:{this._ip - 2:X4}");
    }
  }

  private ushort Segment(int index) => index switch { 0 => this._es, 1 => this._cs, 2 => this._ss, _ => this._ds };

  private void SetSegment(int index, ushort value) {
    switch (index) {
      case 0: this._es = value; break;
      case 1: this._cs = value; break;
      case 2: this._ss = value; break;
      default: this._ds = value; break;
    }
  }

  private ushort Shift16(int op, ushort value, int count) {
    for (var i = 0; i < (count & 0x1F); ++i)
      switch (op) {
        case 0: this._cf = (value & 0x8000) != 0; value = (ushort)((value << 1) | (this._cf ? 1 : 0)); break;   // ROL
        case 1: this._cf = (value & 1) != 0; value = (ushort)((value >> 1) | (this._cf ? 0x8000 : 0)); break;   // ROR
        case 2: { var carry = this._cf; this._cf = (value & 0x8000) != 0; value = (ushort)((value << 1) | (carry ? 1 : 0)); break; }
        case 3: { var carry = this._cf; this._cf = (value & 1) != 0; value = (ushort)((value >> 1) | (carry ? 0x8000 : 0)); break; }
        case 4 or 6: this._cf = (value & 0x8000) != 0; value = (ushort)(value << 1); break;
        case 5: this._cf = (value & 1) != 0; value = (ushort)(value >> 1); break;
        default: this._cf = (value & 1) != 0; value = (ushort)((short)value >> 1); break;                        // SAR
      }
    if ((count & 0x1F) != 0 && op >= 4) {
      this._zf = value == 0;
      this._sf = (value & 0x8000) != 0;
      this._pf = Parity((byte)value);
    }
    return value;
  }

  private uint Shift32(int operation, uint value, int count) {
    var masked = count & 0x1F;
    var original = value;
    for (var i = 0; i < masked; ++i)
      switch (operation) {
        case 0:
          this._cf = (value & 0x80000000) != 0;
          value = value << 1 | (this._cf ? 1U : 0);
          break;
        case 1:
          this._cf = (value & 1) != 0;
          value = value >> 1 | (this._cf ? 0x80000000U : 0);
          break;
        case 2: {
          var carry = this._cf;
          this._cf = (value & 0x80000000) != 0;
          value = value << 1 | (carry ? 1U : 0);
          break;
        }
        case 3: {
          var carry = this._cf;
          this._cf = (value & 1) != 0;
          value = value >> 1 | (carry ? 0x80000000U : 0);
          break;
        }
        case 4 or 6: this._cf = (value & 0x80000000) != 0; value <<= 1; break;
        case 5: this._cf = (value & 1) != 0; value >>= 1; break;
        case 7: this._cf = (value & 1) != 0; value = (uint)((int)value >> 1); break;
        default: throw new Cpu8086Exception($"unimplemented dword shift operation /{operation}");
      }
    if (masked == 0)
      return value;
    if (operation < 4) {
      if (masked == 1)
        this._of = operation is 0 or 2
          ? ((value & 0x80000000) != 0) ^ this._cf
          : ((value >> 31) ^ ((value >> 30) & 1)) != 0;
      return value;
    }
    this._zf = value == 0;
    this._sf = (value & 0x80000000) != 0;
    this._pf = Parity((byte)value);
    if (masked == 1)
      this._of = operation switch {
        4 or 6 => this._sf ^ this._cf,
        5 => (original & 0x80000000) != 0,
        7 => false,
        _ => this._of,
      };
    return value;
  }

  private uint GetRm32(int mode, int address) => mode == 3 ? this.Reg32(address) : this.ReadDword(address);

  private void SetRm32(int mode, int address, uint value) {
    if (mode == 3)
      this.SetReg32(address, value);
    else
      this.WriteDword(address, value);
  }

  private uint Add32(uint left, uint right, bool carry) {
    var sum = (ulong)left + right + (carry ? 1UL : 0UL);
    var result = (uint)sum;
    this._cf = sum > uint.MaxValue;
    this._af = ((left ^ right ^ result) & 0x10) != 0;
    this._of = (~(left ^ right) & (left ^ result) & 0x80000000) != 0;
    this._zf = result == 0;
    this._sf = (result & 0x80000000) != 0;
    this._pf = Parity((byte)result);
    return result;
  }

  private uint Sub32(uint left, uint right, bool borrow) {
    var difference = (long)left - right - (borrow ? 1L : 0L);
    var result = (uint)difference;
    this._cf = difference < 0;
    this._af = ((left ^ right ^ result) & 0x10) != 0;
    this._of = ((left ^ right) & (left ^ result) & 0x80000000) != 0;
    this._zf = result == 0;
    this._sf = (result & 0x80000000) != 0;
    this._pf = Parity((byte)result);
    return result;
  }

  private uint Alu32(int operation, uint left, uint right) => operation switch {
    0 => this.Add32(left, right, false),
    1 => Logic32(this, left | right),
    2 => this.Add32(left, right, this._cf),
    3 => this.Sub32(left, right, this._cf),
    4 => Logic32(this, left & right),
    5 => this.Sub32(left, right, false),
    6 => Logic32(this, left ^ right),
    _ => this.Sub32(left, right, false),
  };

  private static uint Logic32(Cpu8086 cpu, uint value) {
    cpu.SetLogicFlags32(value);
    return value;
  }

  private byte Shift8(int op, byte value, int count) {
    for (var i = 0; i < (count & 0x1F); ++i)
      switch (op) {
        case 0: this._cf = (value & 0x80) != 0; value = (byte)((value << 1) | (this._cf ? 1 : 0)); break;
        case 1: this._cf = (value & 1) != 0; value = (byte)((value >> 1) | (this._cf ? 0x80 : 0)); break;
        case 2: { var carry = this._cf; this._cf = (value & 0x80) != 0; value = (byte)((value << 1) | (carry ? 1 : 0)); break; }
        case 3: { var carry = this._cf; this._cf = (value & 1) != 0; value = (byte)((value >> 1) | (carry ? 0x80 : 0)); break; }
        case 4 or 6: this._cf = (value & 0x80) != 0; value = (byte)(value << 1); break;
        case 5: this._cf = (value & 1) != 0; value = (byte)(value >> 1); break;
        default: this._cf = (value & 1) != 0; value = (byte)((sbyte)value >> 1); break;
      }
    if ((count & 0x1F) != 0 && op >= 4) {
      this._zf = value == 0;
      this._sf = (value & 0x80) != 0;
      this._pf = Parity(value);
    }
    return value;
  }

  private void Group3(byte opcode) {
    var (mode, op, address) = this.ModRm();
    var wide = (opcode & 1) != 0;
    switch (op) {
      case 0 or 1:                                      // TEST r/m,imm
        if (wide)
          this.SetLogicFlags16((ushort)(this.GetRm16(mode, address) & this.FetchWord()));
        else
          this.SetLogicFlags8((byte)(this.GetRm8(mode, address) & this.Fetch()));
        return;
      case 2:                                           // NOT (flags untouched)
        if (wide) this.SetRm16(mode, address, (ushort)~this.GetRm16(mode, address));
        else this.SetRm8(mode, address, (byte)~this.GetRm8(mode, address));
        return;
      case 3:                                           // NEG
        if (wide) this.SetRm16(mode, address, this.Sub16(0, this.GetRm16(mode, address), false));
        else this.SetRm8(mode, address, this.Sub8(0, this.GetRm8(mode, address), false));
        return;
      case 4: {                                         // MUL (unsigned)
        if (wide) {
          var product = (uint)this._r[_AX] * this.GetRm16(mode, address);
          this._r[_AX] = (ushort)product;
          this._r[_DX] = (ushort)(product >> 16);
          this._cf = this._of = this._r[_DX] != 0;
        } else {
          var product = (ushort)(this.Reg8(_AX) * this.GetRm8(mode, address));
          this._r[_AX] = product;
          this._cf = this._of = (product >> 8) != 0;
        }
        return;
      }
      case 5: {                                         // IMUL (signed)
        if (wide) {
          var product = (short)this._r[_AX] * (short)this.GetRm16(mode, address);
          this._r[_AX] = (ushort)product;
          this._r[_DX] = (ushort)(product >> 16);
          this._cf = this._of = (short)this._r[_AX] != product;
        } else {
          var product = (sbyte)this.Reg8(_AX) * (sbyte)this.GetRm8(mode, address);
          this._r[_AX] = (ushort)product;
          this._cf = this._of = (sbyte)(product & 0xFF) != product;
        }
        return;
      }
      case 6: {                                         // DIV
        if (wide) {
          var divisor = this.GetRm16(mode, address);
          if (divisor == 0) throw new Cpu8086Exception("divide by zero (DIV)");
          var dividend = ((uint)this._r[_DX] << 16) | this._r[_AX];
          this._r[_AX] = (ushort)(dividend / divisor);
          this._r[_DX] = (ushort)(dividend % divisor);
        } else {
          var divisor = this.GetRm8(mode, address);
          if (divisor == 0) throw new Cpu8086Exception("divide by zero (DIV)");
          this.SetReg8(_AX, (byte)(this._r[_AX] / divisor));
          this.SetReg8(4, (byte)(this._r[_AX] % divisor));
        }
        return;
      }
      default: {                                        // IDIV
        if (wide) {
          var divisor = (short)this.GetRm16(mode, address);
          if (divisor == 0) throw new Cpu8086Exception("divide by zero (IDIV)");
          var dividend = (int)(((uint)this._r[_DX] << 16) | this._r[_AX]);
          this._r[_AX] = (ushort)(dividend / divisor);
          this._r[_DX] = (ushort)(dividend % divisor);
        } else {
          var divisor = (sbyte)this.GetRm8(mode, address);
          if (divisor == 0) throw new Cpu8086Exception("divide by zero (IDIV)");
          var dividend = (short)this._r[_AX];
          this.SetReg8(_AX, (byte)(dividend / divisor));
          this.SetReg8(4, (byte)(dividend % divisor));
        }
        return;
      }
    }
  }

  private void Group45(byte opcode) {
    var (mode, op, address) = this.ModRm();
    if (opcode == 0xFE) {
      var carry = this._cf;
      this.SetRm8(mode, address, op == 0
        ? this.Add8(this.GetRm8(mode, address), 1, false)
        : this.Sub8(this.GetRm8(mode, address), 1, false));
      this._cf = carry;
      return;
    }
    switch (op) {
      case 0 or 1: {
        var carry = this._cf;
        this.SetRm16(mode, address, op == 0
          ? this.Add16(this.GetRm16(mode, address), 1, false)
          : this.Sub16(this.GetRm16(mode, address), 1, false));
        this._cf = carry;
        return;
      }
      case 2: { this.Push(this._ip); this._ip = this.GetRm16(mode, address); return; }        // CALL r/m
      case 3: {                                                                               // CALL FAR [m]
        var offset = this.ReadWord(address);
        var segment = this.ReadWord(address + 2);
        this.Push(this._cs);
        this.Push(this._ip);
        this._cs = segment;
        this._ip = offset;
        return;
      }
      case 4: this._ip = this.GetRm16(mode, address); return;                                 // JMP r/m
      case 5: { this._ip = this.ReadWord(address); this._cs = this.ReadWord(address + 2); return; }
      case 6: this.Push(this.GetRm16(mode, address)); return;                                 // PUSH r/m
      default: throw new Cpu8086Exception($"unimplemented group 5 operation {op}");
    }
  }

  private void StringOp(byte opcode, int repeat) {
    var wide = (opcode & 1) != 0;
    var step = (ushort)(this._df ? (wide ? -2 : -1) : (wide ? 2 : 1));
    var count = repeat == 0 ? 1 : this._r[_CX];
    var compares = opcode is 0xA6 or 0xA7 or 0xAE or 0xAF;

    while (count-- > 0) {
      var source = Linear(this.DataSegment, this._r[_SI]);
      var destination = Linear(this._es, this._r[_DI]);
      switch (opcode) {
        case 0xA4: this.WriteByte(destination, this.ReadByte(source)); this._r[_SI] += step; this._r[_DI] += step; break;
        case 0xA5: this.WriteWord(destination, this.ReadWord(source)); this._r[_SI] += step; this._r[_DI] += step; break;
        case 0xAA: this.WriteByte(destination, this.Reg8(_AX)); this._r[_DI] += step; break;
        case 0xAB: this.WriteWord(destination, this._r[_AX]); this._r[_DI] += step; break;
        case 0xAC: this.SetReg8(_AX, this.ReadByte(source)); this._r[_SI] += step; break;
        case 0xAD: this._r[_AX] = this.ReadWord(source); this._r[_SI] += step; break;
        case 0xA6: this.Sub8(this.ReadByte(source), this.ReadByte(destination), false); this._r[_SI] += step; this._r[_DI] += step; break;
        case 0xA7: this.Sub16(this.ReadWord(source), this.ReadWord(destination), false); this._r[_SI] += step; this._r[_DI] += step; break;
        case 0xAE: this.Sub8(this.Reg8(_AX), this.ReadByte(destination), false); this._r[_DI] += step; break;
        default: this.Sub16(this._r[_AX], this.ReadWord(destination), false); this._r[_DI] += step; break;
      }
      if (repeat != 0) {
        --this._r[_CX];
        if (compares && this._zf != (repeat == 2))
          break;
      }
    }
  }

  // ---- DOS / BIOS -------------------------------------------------------------------------------

  private void Interrupt(byte number) {
    switch (number) {
      case 0x21: this.Dos(); return;
      case 0x67: this.Ems(); return;
      case 0x10: this.Bios10(); return;
      case 0x20: this._halted = true; return;
      case 0x1A: this._r[_CX] = this._r[_DX] = 0; return;      // clock ticks - a fixed zero time
      default: throw new Cpu8086Exception($"unhandled INT {number:X2}h (AX={this._r[_AX]:X4})");
    }
  }

  private void Ems() {
    switch (this.Reg8(4)) {
      case 0x41:                                                // get page-frame segment
        this._r[_BX] = _EMS_FRAME_SEGMENT;
        this.SetReg8(4, 0);
        return;
      case 0x42: {                                              // get unallocated and total pages
        var allocated = this._emsHandles.Values.Sum(storage => storage.Length / _EMS_PAGE_SIZE);
        this._r[_BX] = (ushort)(_EMS_TOTAL_PAGES - allocated);
        this._r[_DX] = _EMS_TOTAL_PAGES;
        this.SetReg8(4, 0);
        return;
      }
      case 0x43: this.AllocateEmsPages(); return;
      case 0x44: this.MapEmsPage(); return;
      case 0x45: this.ReleaseEmsHandle(); return;
      default: throw new Cpu8086Exception($"unhandled INT 67h (AX={this._r[_AX]:X4})");
    }
  }

  private void AllocateEmsPages() {
    var pages = this._r[_BX];
    var allocated = this._emsHandles.Values.Sum(storage => storage.Length / _EMS_PAGE_SIZE);
    if (pages == 0) {
      this.SetReg8(4, 0x89);                                    // zero pages requested
      return;
    }
    if (pages > _EMS_TOTAL_PAGES) {
      this.SetReg8(4, 0x87);                                    // request exceeds total pages
      return;
    }
    if (pages > _EMS_TOTAL_PAGES - allocated) {
      this.SetReg8(4, 0x88);                                    // insufficient free pages
      return;
    }
    while (this._emsHandles.ContainsKey(this._nextEmsHandle))
      ++this._nextEmsHandle;
    this._emsHandles[this._nextEmsHandle] = new byte[pages * _EMS_PAGE_SIZE];
    this._r[_DX] = this._nextEmsHandle++;
    this.SetReg8(4, 0);
  }

  private void MapEmsPage() {
    var physicalPage = this.Reg8(_AX);
    if (physicalPage >= this._emsMappings.Length) {
      this.SetReg8(4, 0x8B);                                    // invalid physical page
      return;
    }
    var handle = this._r[_DX];
    if (!this._emsHandles.TryGetValue(handle, out var storage)) {
      this.SetReg8(4, 0x83);                                    // invalid handle
      return;
    }
    var logicalPage = this._r[_BX];
    if (logicalPage >= storage.Length / _EMS_PAGE_SIZE) {
      this.SetReg8(4, 0x8A);                                    // logical page outside allocation
      return;
    }

    this.FlushEmsMapping(physicalPage);
    Array.Copy(storage, logicalPage * _EMS_PAGE_SIZE, this._memory,
      _EMS_FRAME_SEGMENT * 16 + physicalPage * _EMS_PAGE_SIZE, _EMS_PAGE_SIZE);
    this._emsMappings[physicalPage] = new(handle, logicalPage);
    this.SetReg8(4, 0);
  }

  private void ReleaseEmsHandle() {
    var handle = this._r[_DX];
    if (!this._emsHandles.ContainsKey(handle)) {
      this.SetReg8(4, 0x83);                                    // invalid handle
      return;
    }
    for (var physicalPage = 0; physicalPage < this._emsMappings.Length; ++physicalPage) {
      if (this._emsMappings[physicalPage] is not { Handle: var mapped } || mapped != handle)
        continue;
      this.FlushEmsMapping(physicalPage);
      this._emsMappings[physicalPage] = null;
    }
    this._emsHandles.Remove(handle);
    this.SetReg8(4, 0);
  }

  private void FlushEmsMapping(int physicalPage) {
    if (this._emsMappings[physicalPage] is not { } mapping
        || !this._emsHandles.TryGetValue(mapping.Handle, out var storage))
      return;
    Array.Copy(this._memory, _EMS_FRAME_SEGMENT * 16 + physicalPage * _EMS_PAGE_SIZE,
      storage, mapping.LogicalPage * _EMS_PAGE_SIZE, _EMS_PAGE_SIZE);
  }

  private void Dos() {
    var ah = this.Reg8(4);
    switch (ah) {
      case 0x30: this._r[_AX] = 0x0006; return;                // DOS 6.0
      case 0x25 or 0x35: return;                               // set/get interrupt vector - nothing to do here
      case 0x4B: {                                             // load and execute a child program
        var subfunction = this.Reg8(_AX);
        if (subfunction != 0)
          throw new Cpu8086Exception($"unhandled DOS EXEC AL={subfunction:X2}h");
        var name = this.CString(Linear(this._ds, this._r[_DX]));
        if (!this._executables.TryGetValue(name, out var image))
          throw new Cpu8086Exception($"unavailable EXEC target {name}");
        this.ExecuteChild(image);
        this._r[_AX] = 0;
        this._cf = false;
        return;
      }
      case 0x4C: this.ExitCode = this.Reg8(_AX); this._halted = true; return;
      case 0x4D: this._r[_AX] = this._childExitCode; this._cf = false; return;
      case 0x40: {                                             // write BX=handle, CX=count, DS:DX=buffer
        var handle = this._r[_BX];
        var count = this._r[_CX];
        var at = Linear(this._ds, this._r[_DX]);
        if (handle is 1 or 2)
          for (var i = 0; i < count; ++i)
            this._output.Append((char)this.ReadByte(at + i));
        else if (handle == 4)                                  // PRN, which DOS opens for every program
          for (var i = 0; i < count; ++i)
            this._printer.Append((char)this.ReadByte(at + i));
        // A write lands AT the file position and advances it - it does not append. Appending is what
        // this did, and it is the same answer only while a program writes its records in order: a
        // RANDOM PUT to record 3 before record 1 would have produced a file whose CONTENTS were right
        // for a compiler that seeked to the wrong place. A gap past the end reads back as zeroes, and
        // a write of zero bytes is DOS's own truncate-here, which is how SETEOF is spelled.
        else if (this._files.TryGetValue(handle, out var open)) {
          var bytes = open.File.Bytes;
          while (bytes.Count < open.Position)
            bytes.Add(0);
          if (count == 0) {
            bytes.RemoveRange(open.Position, bytes.Count - open.Position);
          } else {
            for (var i = 0; i < count; ++i, ++open.Position) {
              var value = this.ReadByte(at + i);
              if (open.Position < bytes.Count)
                bytes[open.Position] = value;
              else
                bytes.Add(value);
            }
          }
        }
        this._r[_AX] = count;
        this._cf = false;
        return;
      }
      case 0x3C or 0x5B: {                                     // create file - truncates an existing one
        var name = this.CString(Linear(this._ds, this._r[_DX]));
        if (!this._byName.TryGetValue(name, out var file))
          this._byName[name] = file = new MemoryFile { Name = name };
        file.Bytes.Clear();
        this._files[this._nextHandle] = new OpenFile { File = file };
        this._r[_AX] = (ushort)this._nextHandle++;
        this._cf = false;
        return;
      }
      case 0x3D: {                                             // open file
        var name = this.CString(Linear(this._ds, this._r[_DX]));
        if (!this._byName.TryGetValue(name, out var file)) {
          this._cf = true;
          this._r[_AX] = 2;                                    // file not found
          return;
        }
        this._files[this._nextHandle] = new OpenFile { File = file };
        this._r[_AX] = (ushort)this._nextHandle++;
        this._cf = false;
        return;
      }
      case 0x3E: this._files.Remove(this._r[_BX]); this._cf = false; return;
      case 0x3F: {                                             // read
        if (!this._files.TryGetValue(this._r[_BX], out var open)) { this._cf = true; this._r[_AX] = 6; return; }
        var at = Linear(this._ds, this._r[_DX]);
        var wanted = Math.Max(0, Math.Min(this._r[_CX], open.File.Bytes.Count - open.Position));
        for (var i = 0; i < wanted; ++i)
          this.WriteByte(at + i, open.File.Bytes[open.Position + i]);
        open.Position += wanted;
        this._r[_AX] = (ushort)wanted;
        this._cf = false;
        return;
      }
      case 0x42: {                                             // seek
        if (!this._files.TryGetValue(this._r[_BX], out var open)) { this._cf = true; this._r[_AX] = 6; return; }
        var offset = (this._r[_CX] << 16) | this._r[_DX];
        open.Position = this.Reg8(_AX) switch {
          0 => offset,
          1 => open.Position + offset,
          _ => open.File.Bytes.Count + offset,
        };
        this._r[_DX] = (ushort)(open.Position >> 16);
        this._r[_AX] = (ushort)open.Position;
        this._cf = false;
        return;
      }
      // rename. NAME old$ AS new$ is a DOS call the interpreter simply did not have, so every program
      // using it stopped here rather than being compared.
      case 0x56: {
        var from = this.CString(Linear(this._ds, this._r[_DX]));
        var to = this.CString(Linear(this._es, this._r[_DI]));
        if (!this._byName.Remove(from, out var file) || this._byName.ContainsKey(to)) {
          if (file is not null)
            this._byName[from] = file;
          this._cf = true;
          this._r[_AX] = 2;
          return;
        }
        file.Name = to;
        this._byName[to] = file;
        this._cf = false;
        return;
      }
      case 0x41: {                                             // delete
        this._byName.Remove(this.CString(Linear(this._ds, this._r[_DX])));
        this._cf = false;
        return;
      }
      case 0x44: {                                             // IOCTL get device information
        var subfunction = this.Reg8(_AX);
        if (subfunction != 0)
          throw new Cpu8086Exception($"unhandled DOS IOCTL AL={subfunction:X2}h");
        var handle = this._r[_BX];
        if (handle <= 4) {
          this._r[_DX] = 0x0080;                               // standard handles are character devices
          this._cf = false;
        } else if (this._files.ContainsKey(handle)) {
          this._r[_DX] = 0;                                    // disk file
          this._cf = false;
        } else {
          this._r[_AX] = 6;                                    // invalid handle
          this._cf = true;
        }
        return;
      }
      case 0x48: {                                             // allocate paragraphs
        this._r[_AX] = this._nextFreeSegment;
        this._nextFreeSegment += this._r[_BX] == 0 ? (ushort)1 : this._r[_BX];
        this._cf = false;
        return;
      }
      case 0x49 or 0x4A: this._cf = false; return;             // free / resize - the arena is never exhausted here
      // directory calls. There is no real file system behind this interpreter, only the in-memory
      // file map, so a directory is just a name that has been created - enough for a program to make
      // one, remove it, and be told which of those succeeded.
      case 0x39: {                                             // create directory
        var name = this.CString(Linear(this._ds, this._r[_DX]));
        this._cf = !this._directories.Add(name);
        if (this._cf) this._r[_AX] = 5;                        // access denied: it already exists
        return;
      }
      case 0x3A: {                                             // remove directory
        this._cf = !this._directories.Remove(this.CString(Linear(this._ds, this._r[_DX])));
        if (this._cf) this._r[_AX] = 3;                        // path not found
        return;
      }
      case 0x3B: {                                             // set current directory
        var name = this.CString(Linear(this._ds, this._r[_DX]));
        this._cf = name != "\\" && name != "." && !this._directories.Contains(name);
        if (this._cf) this._r[_AX] = 3;
        return;
      }
      case 0x58: this._cf = true; return;                      // UMB link/strategy: report unsupported
      case 0x09: {                                             // print '$'-terminated string
        var at = Linear(this._ds, this._r[_DX]);
        for (var i = 0; this.ReadByte(at + i) != (byte)'$'; ++i)
          this._output.Append((char)this.ReadByte(at + i));
        return;
      }
      default:
        throw new Cpu8086Exception($"unhandled DOS call AH={ah:X2}h (AX={this._r[_AX]:X4})");
    }
  }

  private void Bios10() {
    switch (this.Reg8(4)) {
      // set video mode. Nothing here draws, so the mode itself only has to be remembered for AH=0Fh;
      // what matters is that a real BIOS CLEARS the frame buffer on a mode set, and a graphics test
      // that read a stale pixel from a previous run would pass or fail for the wrong reason.
      case 0x00: {
        this._videoMode = this.Reg8(_AX);
        if (this._videoMode == 0x13)
          Array.Clear(this._memory, 0xA0000, 320 * 200);
        return;
      }
      case 0x02 or 0x01 or 0x05 or 0x06 or 0x09 or 0x0A: return;   // cursor / scroll / attribute writes
      case 0x03: this._r[_CX] = 0x0607; this._r[_DX] = 0; return;   // cursor at 0,0
      case 0x0E: this._output.Append((char)this.Reg8(_AX)); return; // teletype
      // report the mode that was actually set, with the 80-column text default before any mode set
      case 0x0F: this._r[_AX] = (ushort)((80 << 8) | this._videoMode); this.SetReg8(7, 0); return;
      default: throw new Cpu8086Exception($"unhandled BIOS video call AH={this.Reg8(4):X2}h");
    }
  }

  private string CString(int at) {
    var text = new StringBuilder();
    for (var i = 0; this.ReadByte(at + i) != 0; ++i)
      text.Append((char)this.ReadByte(at + i));
    return text.ToString();
  }
}

/// <summary>Something the interpreter will not guess at: an unimplemented opcode, an unhandled DOS call, a runaway program.</summary>
public sealed class Cpu8086Exception(string message) : Exception(message);
