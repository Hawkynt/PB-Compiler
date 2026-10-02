using System.Buffers.Binary;
using System.Text;
using PowerBasic.Compiler.Backend.X86Native;

namespace PowerBasic.Compiler.Emit.Elf;

/// <summary>
/// Linux ELF files for a native i386 or x64 program, written without a linker: a static executable
/// that needs no C library, a relocatable object exporting <c>pb_main</c> for a C program to link,
/// and an <c>ar</c> archive of that object with the symbol index <c>ld</c> searches.
/// </summary>
public static class ElfWriter {

  private const long Amd64Base = 0x400000;
  private const long I386Base = 0x08048000;

  /// <summary>
  /// A static executable: one loadable segment holding the headers, the code, the data and - in memory
  /// only - the zeroed storage, entered at <c>_start</c>.
  /// </summary>
  public static byte[] Executable(X86NativeCompiler.Program program) {
    ArgumentNullException.ThrowIfNull(program);
    var asm = program.Assembler;
    var is64 = asm.Machine == X86Machine.Amd64;
    var headerSize = is64 ? 64 + 56 : 52 + 32;
    var baseAddress = is64 ? Amd64Base : I386Base;
    var textOffset = Align(headerSize, 16);
    var dataOffset = Align(textOffset + asm.Text.Count, 16);
    var fileSize = dataOffset + asm.Data.Count;
    var bssAddress = baseAddress + Align(fileSize, 16);
    var memorySize = bssAddress - baseAddress + asm.BssSize;
    var (text, data) = asm.Link(baseAddress + textOffset, baseAddress + dataOffset, bssAddress);
    var entry = asm.AddressOf(program.Start, baseAddress + textOffset, baseAddress + dataOffset, bssAddress);

    var file = new byte[fileSize];
    var w = new Cursor(file, is64);
    w.Identity(type: 2, machine: is64 ? 62 : 3);
    w.Address(entry);                       // e_entry
    w.Address(is64 ? 64 : 52);              // e_phoff
    w.Address(0);                           // e_shoff
    w.U32(0);                               // e_flags
    w.U16(is64 ? 64 : 52);                  // e_ehsize
    w.U16(is64 ? 56 : 32);                  // e_phentsize
    w.U16(1);                               // e_phnum
    w.U16(is64 ? 64 : 40);                  // e_shentsize
    w.U16(0);                               // e_shnum
    w.U16(0);                               // e_shstrndx
    // the program header: PT_LOAD, readable, writable and executable
    if (is64) {
      w.U32(1);
      w.U32(7);
      w.Address(0);
      w.Address(baseAddress);
      w.Address(baseAddress);
      w.Address(fileSize);
      w.Address(memorySize);
      w.Address(0x1000);
    } else {
      w.U32(1);
      w.Address(0);
      w.Address(baseAddress);
      w.Address(baseAddress);
      w.Address(fileSize);
      w.Address(memorySize);
      w.U32(7);
      w.Address(0x1000);
    }
    text.CopyTo(file, textOffset);
    data.CopyTo(file, dataOffset);
    return file;
  }

  /// <summary>
  /// A relocatable object: <c>.text</c>, <c>.data</c> and <c>.bss</c>, two global functions, and a
  /// relocation for every reference that crosses a section - the rest are resolved in place.
  /// <c>pb_main</c> is the program for a C caller; <c>pb_start</c> is an entry point that runs it and
  /// exits, for linking the object on its own (<c>ld -e pb_start</c>).
  /// </summary>
  public static byte[] Object(X86NativeCompiler.Program program) {
    ArgumentNullException.ThrowIfNull(program);
    var asm = program.Assembler;
    var is64 = asm.Machine == X86Machine.Amd64;
    var (text, data) = asm.Link(0, 0, 0);
    var textRelocations = new List<(long Offset, int Type, int Symbol, long Addend)>();
    var dataRelocations = new List<(long Offset, int Type, int Symbol, long Addend)>();
    foreach (var fixup in asm.Fixups) {
      var (section, offset) = asm.Labels[fixup.Target];
      var crosses = section != fixup.Section;
      if (!crosses && fixup.Kind is X86FixupKind.Relative32 or X86FixupKind.RipRelative32)
        continue;
      var symbol = 1 + (int)section;
      var target = offset + fixup.Addend;
      var bytes = fixup.Section == X86Section.Text ? text : data;
      var list = fixup.Section == X86Section.Text ? textRelocations : dataRelocations;
      var trailing = fixup.Kind == X86FixupKind.RipRelative32 ? fixup.TrailingBytes : 0;
      int type;
      long addend;
      switch (fixup.Kind) {
        case X86FixupKind.Relative32 or X86FixupKind.RipRelative32:
          type = 2;                                   // R_X86_64_PC32 / R_386_PC32
          addend = target - 4 - trailing;
          break;
        case X86FixupKind.Absolute64:
          type = 1;                                   // R_X86_64_64
          addend = target;
          break;
        default:
          type = is64 ? 10 : 1;                       // R_X86_64_32 / R_386_32
          addend = target;
          break;
      }
      // RELA carries the addend in the entry; REL in the field it patches
      var width = fixup.Kind == X86FixupKind.Absolute64 ? 8 : 4;
      var field = is64 ? 0 : addend;
      for (var i = 0; i < width; ++i)
        bytes[fixup.Offset + i] = (byte)(field >> (8 * i));
      list.Add((fixup.Offset, type, symbol, addend));
    }

    var strings = new StringTable();
    var mainName = strings.Add("pb_main");
    var startName = strings.Add("pb_start");
    var sectionNames = new StringTable();
    var names = new[] { "", ".text", ".data", ".bss", ".symtab", ".strtab",
      is64 ? ".rela.text" : ".rel.text", is64 ? ".rela.data" : ".rel.data", ".shstrtab" }
      .Select(sectionNames.Add).ToArray();

    var symbolSize = is64 ? 24 : 16;
    var symbols = new byte[symbolSize * 6];
    var s = new Cursor(symbols, is64);
    s.Symbol(0, 0, 0, 0, 0);
    for (var section = 1; section <= 3; ++section)
      s.Symbol(0, 0, 0, info: 3, section);    // STB_LOCAL, STT_SECTION
    var main = asm.Labels[program.Export];
    s.Symbol(mainName, main.Offset, 0, info: (1 << 4) | 2, 1);   // STB_GLOBAL, STT_FUNC
    s.Symbol(startName, asm.Labels[program.Start].Offset, 0, info: (1 << 4) | 2, 1);

    byte[] Relocations(List<(long Offset, int Type, int Symbol, long Addend)> list) {
      var entry = is64 ? 24 : 8;
      var bytes = new byte[list.Count * entry];
      var r = new Cursor(bytes, is64);
      foreach (var (offset, type, symbol, addend) in list) {
        if (is64) {
          r.U64((ulong)offset);
          r.U64(((ulong)symbol << 32) | (uint)type);
          r.U64((ulong)addend);
        } else {
          r.U32((uint)offset);
          r.U32(((uint)symbol << 8) | (uint)type);
        }
      }
      return bytes;
    }

    var contents = new[] {
      Array.Empty<byte>(), text, data, Array.Empty<byte>(), symbols, strings.Bytes(),
      Relocations(textRelocations), Relocations(dataRelocations), sectionNames.Bytes(),
    };
    var headerSize = is64 ? 64 : 52;
    var offsets = new long[contents.Length];
    var at = (long)headerSize;
    for (var i = 1; i < contents.Length; ++i) {
      at = Align(at, 16);
      offsets[i] = at;
      if (i != 3)
        at += contents[i].Length;
    }
    var sectionHeaders = Align(at, 16);
    var headerEntry = is64 ? 64 : 40;
    var file = new byte[sectionHeaders + headerEntry * contents.Length];
    for (var i = 1; i < contents.Length; ++i)
      if (i != 3)
        contents[i].CopyTo(file, offsets[i]);

    var w = new Cursor(file, is64);
    w.Identity(type: 1, machine: is64 ? 62 : 3);
    w.Address(0);                            // e_entry
    w.Address(0);                            // e_phoff
    w.Address(sectionHeaders);               // e_shoff
    w.U32(0);
    w.U16(headerSize);
    w.U16(0);
    w.U16(0);
    w.U16(headerEntry);
    w.U16(contents.Length);
    w.U16(8);                                // .shstrtab

    var h = new Cursor(file, is64) { Position = (int)sectionHeaders };
    // name, type, flags, offset, size, link, info, alignment, entry size
    h.Section(0, 0, 0, 0, 0, 0, 0, 0, 0);
    h.Section(names[1], 1, 6, offsets[1], text.Length, 0, 0, 16, 0);
    h.Section(names[2], 1, 3, offsets[2], data.Length, 0, 0, 16, 0);
    h.Section(names[3], 8, 3, offsets[3], asm.BssSize, 0, 0, 16, 0);
    h.Section(names[4], 2, 0, offsets[4], symbols.Length, 5, 4, 8, symbolSize);
    h.Section(names[5], 3, 0, offsets[5], contents[5].Length, 0, 0, 1, 0);
    h.Section(names[6], is64 ? 4 : 9, 0x40, offsets[6], contents[6].Length, 4, 1, 8, is64 ? 24 : 8);
    h.Section(names[7], is64 ? 4 : 9, 0x40, offsets[7], contents[7].Length, 4, 2, 8, is64 ? 24 : 8);
    h.Section(names[8], 3, 0, offsets[8], contents[8].Length, 0, 0, 1, 0);
    return file;
  }

  /// <summary>
  /// An <c>ar</c> archive of <paramref name="objects"/>, led by the GNU symbol index that tells a linker
  /// which member defines which global.
  /// </summary>
  public static byte[] Archive(IReadOnlyList<(string Name, byte[] Contents, IReadOnlyList<string> Symbols)> objects) {
    ArgumentNullException.ThrowIfNull(objects);
    var symbols = objects.SelectMany((member, index) => member.Symbols.Select(symbol => (symbol, index))).ToList();
    var indexSize = 4 + 4 * symbols.Count + symbols.Sum(entry => Encoding.ASCII.GetByteCount(entry.symbol) + 1);
    var memberOffsets = new List<int>();
    var at = 8 + 60 + Align(indexSize, 2);
    foreach (var member in objects) {
      memberOffsets.Add(at);
      at += 60 + Align(member.Contents.Length, 2);
    }

    var output = new List<byte>(Encoding.ASCII.GetBytes("!<arch>\n"));
    var index = new byte[indexSize];
    BinaryPrimitives.WriteUInt32BigEndian(index, (uint)symbols.Count);
    for (var i = 0; i < symbols.Count; ++i)
      BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(4 + 4 * i), (uint)memberOffsets[symbols[i].index]);
    var name = 4 + 4 * symbols.Count;
    foreach (var (symbol, _) in symbols) {
      Encoding.ASCII.GetBytes(symbol).CopyTo(index, name);
      name += symbol.Length + 1;
    }
    AddMember(output, "/", index);
    foreach (var member in objects)
      AddMember(output, member.Name + "/", member.Contents);
    return [.. output];
  }

  private static void AddMember(List<byte> output, string name, byte[] contents) {
    var header = $"{name,-16}{"0",-12}{"0",-6}{"0",-6}{"644",-8}{contents.Length,-10}`\n";
    output.AddRange(Encoding.ASCII.GetBytes(header));
    output.AddRange(contents);
    if (contents.Length % 2 != 0)
      output.Add((byte)'\n');
  }

  private static long Align(long value, int alignment) => (value + alignment - 1) / alignment * alignment;
  private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

  /// <summary>A NUL-separated string table; offset zero is the empty name.</summary>
  private sealed class StringTable {
    private readonly List<byte> _bytes = [0];

    public int Add(string text) {
      if (text.Length == 0)
        return 0;
      var offset = this._bytes.Count;
      this._bytes.AddRange(Encoding.ASCII.GetBytes(text));
      this._bytes.Add(0);
      return offset;
    }

    public byte[] Bytes() => [.. this._bytes];
  }

  /// <summary>Little-endian field writer that knows which fields are address-sized.</summary>
  private sealed class Cursor(byte[] bytes, bool is64) {
    public int Position { get; set; }

    public void U16(int value) { BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(this.Position), (ushort)value); this.Position += 2; }
    public void U32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(this.Position), value); this.Position += 4; }
    public void U32(int value) => this.U32((uint)value);
    public void U64(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(this.Position), value); this.Position += 8; }

    public void Address(long value) {
      if (is64)
        this.U64((ulong)value);
      else
        this.U32((uint)value);
    }

    /// <summary><c>e_ident</c>, <c>e_type</c>, <c>e_machine</c>, <c>e_version</c>.</summary>
    public void Identity(int type, int machine) {
      bytes[0] = 0x7F;
      bytes[1] = (byte)'E';
      bytes[2] = (byte)'L';
      bytes[3] = (byte)'F';
      bytes[4] = (byte)(is64 ? 2 : 1);
      bytes[5] = 1;
      bytes[6] = 1;
      this.Position = 16;
      this.U16(type);
      this.U16(machine);
      this.U32(1);
    }

    public void Symbol(int name, long value, long size, int info, int section) {
      if (is64) {
        this.U32(name);
        bytes[this.Position++] = (byte)info;
        bytes[this.Position++] = 0;
        this.U16(section);
        this.U64((ulong)value);
        this.U64((ulong)size);
      } else {
        this.U32(name);
        this.U32((uint)value);
        this.U32((uint)size);
        bytes[this.Position++] = (byte)info;
        bytes[this.Position++] = 0;
        this.U16(section);
      }
    }

    public void Section(int name, int type, long flags, long offset, long size, int link, int info, long alignment, long entrySize) {
      this.U32(name);
      this.U32(type);
      this.Address(flags);
      this.Address(0);
      this.Address(offset);
      this.Address(size);
      this.U32(link);
      this.U32(info);
      this.Address(alignment);
      this.Address(entrySize);
    }
  }
}
