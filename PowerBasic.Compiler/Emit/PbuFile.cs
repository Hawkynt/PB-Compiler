using System.Text;

namespace PowerBasic.Compiler.Emit;

/// <summary>Export kind inside a compiled unit.</summary>
public enum PbuExportKind : byte { Sub = 0, Function = 1 }

/// <summary>Relocation kinds inside a unit's code image (see docs/FORMATS.md).</summary>
public enum PbuFixupKind : byte {
  /// <summary>16-bit offset relative to the unit's code base.</summary>
  NearCode = 0,
  /// <summary>16-bit offset relative to the unit's data base.</summary>
  DataOffset = 1,
  /// <summary>Segment paragraph value - becomes an MZ relocation when linked.</summary>
  Segment = 2,
  /// <summary>16-bit near-call target resolved from the import with <see cref="PbuFixup.Target"/> index.</summary>
  ImportCall = 3,
  /// <summary>16-bit absolute offset of the import (data cells, CODEPTR); the site's addend is kept.</summary>
  ImportOffset = 4,
}

public sealed record PbuExport(string Name, PbuExportKind Kind, uint SignatureHash, uint CodeOffset);

public sealed record PbuImport(string Name, uint SignatureHash);

public sealed record PbuCommonBlock(string Name, uint Size);

/// <summary>
/// A relocation in a unit's image. <paramref name="Offset"/> is relative to the unit's
/// code base, or - when <paramref name="InData"/> is set - its data base (a site sitting
/// in the data image, as a foreign far/data initializer can). <paramref name="InData"/>
/// is in-memory only: it occurs solely on foreign OMF objects (lowered, never persisted),
/// since BASIC codegen emits only code-site fixups, so it is not serialized.
/// </summary>
public sealed record PbuFixup(uint Offset, PbuFixupKind Kind, ushort Target, bool InData = false);

/// <summary>CPU/feature requirement flags of a unit.</summary>
[Flags]
public enum PbuCpuFlags : ushort { None = 0, Needs186 = 1, Needs286 = 2, Needs386 = 4, UsesFpu = 8 }

/// <summary>The semantic control transfer that closes a relocatable basic-block fragment.</summary>
public enum PbuFragmentControlKind : byte {
  /// <summary>No CFG successor: return, trap, indirect transfer, or an ordinary terminal byte sequence retained in the body.</summary>
  Preserve = 0,
  /// <summary>Exactly one CFG successor; the post-link rewriter may materialize/remove a JMP as layout requires.</summary>
  Unconditional = 1,
  /// <summary>Two CFG successors; <see cref="PbuFragment.PrimaryTargetBlockId"/> is taken when <see cref="PbuFragment.Condition"/> holds.</summary>
  Conditional = 2,
}

/// <summary>
/// O0360 block metadata carried through PBU. <paramref name="Offset"/>/<paramref name="Length"/> name
/// the original byte range; <paramref name="BodyLength"/> excludes the explicit terminal Jcc/JMP
/// sequence that can be reconstructed from <paramref name="Control"/> after a move. Stable block IDs
/// are function-local and deliberately independent of physical order.
/// </summary>
public sealed record PbuFragment(
  string Function,
  int BlockId,
  uint Offset,
  uint Length,
  uint BodyLength,
  PbuFragmentControlKind Control,
  int PrimaryTargetBlockId,
  int SecondaryTargetBlockId,
  byte Condition,
  IReadOnlyList<int> Successors);

/// <summary>Semantic kind of one already-resolved internal PC-relative instruction retained in PBU2.</summary>
public enum PbuRelativeFixupKind : byte { Call = 0, Jump = 1, Conditional = 2 }

/// <summary>
/// An internal relative instruction from the assembled unit. Terminal block transfers may be replaced
/// from <see cref="PbuFragment"/> control metadata; calls and other retained transfers are repatched
/// against their moved target after layout.
/// </summary>
public sealed record PbuRelativeFixup(
  uint InstructionOffset,
  byte EncodedLength,
  PbuRelativeFixupKind Kind,
  byte Condition,
  uint TargetOffset);

/// <summary>
/// A compiled unit (<c>$COMPILE UNIT</c>) in PB-Compiler's own documented
/// container format - see docs/FORMATS.md. Not compatible with proprietary
/// PowerBASIC 3.5 units by design (REQUIREMENTS.md W2).
/// </summary>
public sealed class PbuFile {

  private static readonly byte[] _magic = "PBU1"u8.ToArray();
  private const ushort _version = 2;
  private const ushort _oldestSupportedVersion = 1;

  public required string Name { get; init; }
  public PbuCpuFlags CpuFlags { get; init; }
  /// <summary>In-memory only: this unit was lowered from a foreign OMF object/library, so its symbols are case-sensitive (C/C++/asm publics) and excluded from BASIC's case-insensitive resolution. Never serialized.</summary>
  public bool Foreign { get; set; }
  public List<PbuExport> Exports { get; } = [];
  public List<PbuImport> Imports { get; } = [];
  public List<PbuCommonBlock> Commons { get; } = [];
  public byte[] Code { get; set; } = [];
  public byte[] Data { get; set; } = [];
  public uint BssSize { get; set; }
  public List<PbuFixup> Fixups { get; } = [];
  public List<PbuFragment> Fragments { get; } = [];
  public List<PbuRelativeFixup> RelativeFixups { get; } = [];

  /// <summary>FNV-1a-32 over the canonical signature string; the linker rejects mismatches.</summary>
  public static uint HashSignature(string canonicalSignature) {
    var hash = 2166136261u;
    foreach (var b in Encoding.ASCII.GetBytes(canonicalSignature.ToUpperInvariant())) {
      hash ^= b;
      hash *= 16777619u;
    }
    return hash;
  }

  public void Write(Stream stream) {
    using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
    w.Write(_magic);
    w.Write(_version);
    w.Write((ushort)this.CpuFlags);
    WriteString(w, this.Name);

    w.Write(CheckedCount(this.Exports.Count, "exports"));
    foreach (var e in this.Exports) {
      WriteString(w, e.Name);
      w.Write((byte)e.Kind);
      w.Write(e.SignatureHash);
      w.Write(e.CodeOffset);
    }

    w.Write(CheckedCount(this.Imports.Count, "imports"));
    foreach (var i in this.Imports) {
      WriteString(w, i.Name);
      w.Write(i.SignatureHash);
    }

    w.Write(CheckedCount(this.Commons.Count, "common blocks"));
    foreach (var c in this.Commons) {
      WriteString(w, c.Name);
      w.Write(c.Size);
    }

    w.Write((uint)this.Code.Length);
    w.Write(this.Code);
    w.Write((uint)this.Data.Length);
    w.Write(this.Data);
    w.Write(this.BssSize);

    w.Write(CheckedCount(this.Fixups.Count, "fixups"));
    foreach (var f in this.Fixups) {
      w.Write(f.Offset);
      w.Write((byte)f.Kind);
      w.Write(f.Target);
    }

    w.Write(CheckedCount(this.Fragments.Count, "fragments"));
    foreach (var fragment in this.Fragments) {
      WriteString(w, fragment.Function);
      WriteNonNegative(w, fragment.BlockId, "fragment block id");
      w.Write(fragment.Offset);
      w.Write(fragment.Length);
      w.Write(fragment.BodyLength);
      w.Write((byte)fragment.Control);
      w.Write(fragment.PrimaryTargetBlockId);
      w.Write(fragment.SecondaryTargetBlockId);
      w.Write(fragment.Condition);
      w.Write(CheckedCount(fragment.Successors.Count, "fragment successors"));
      foreach (var successor in fragment.Successors)
        WriteNonNegative(w, successor, "fragment successor id");
    }

    w.Write((uint)this.RelativeFixups.Count);
    foreach (var fixup in this.RelativeFixups) {
      w.Write(fixup.InstructionOffset);
      w.Write(fixup.EncodedLength);
      w.Write((byte)fixup.Kind);
      w.Write(fixup.Condition);
      w.Write(fixup.TargetOffset);
    }
  }

  public static PbuFile Read(Stream stream) {
    using var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
    if (!r.ReadBytes(4).AsSpan().SequenceEqual(_magic))
      throw new InvalidDataException("not a PBU1 unit file");
    var version = r.ReadUInt16();
    if (version is < _oldestSupportedVersion or > _version)
      throw new InvalidDataException($"unsupported PBU version {version}");

    var cpuFlags = (PbuCpuFlags)r.ReadUInt16();
    var unit = new PbuFile { Name = ReadString(r), CpuFlags = cpuFlags };

    for (var n = r.ReadUInt16(); n > 0; --n)
      unit.Exports.Add(new(ReadString(r), (PbuExportKind)r.ReadByte(), r.ReadUInt32(), r.ReadUInt32()));
    for (var n = r.ReadUInt16(); n > 0; --n)
      unit.Imports.Add(new(ReadString(r), r.ReadUInt32()));
    for (var n = r.ReadUInt16(); n > 0; --n)
      unit.Commons.Add(new(ReadString(r), r.ReadUInt32()));

    unit.Code = r.ReadBytes(checked((int)r.ReadUInt32()));
    unit.Data = r.ReadBytes(checked((int)r.ReadUInt32()));
    unit.BssSize = r.ReadUInt32();

    for (var n = r.ReadUInt16(); n > 0; --n)
      unit.Fixups.Add(new(r.ReadUInt32(), (PbuFixupKind)r.ReadByte(), r.ReadUInt16()));

    if (version == 1)
      return unit;

    for (var n = r.ReadUInt16(); n > 0; --n) {
      var function = ReadString(r);
      var blockId = ReadNonNegativeInt32(r, "fragment block id");
      var offset = r.ReadUInt32();
      var length = r.ReadUInt32();
      var bodyLength = r.ReadUInt32();
      var control = (PbuFragmentControlKind)r.ReadByte();
      var primary = r.ReadInt32();
      var secondary = r.ReadInt32();
      var condition = r.ReadByte();
      var successorCount = r.ReadUInt16();
      var successors = new int[successorCount];
      for (var successor = 0; successor < successorCount; ++successor)
        successors[successor] = ReadNonNegativeInt32(r, "fragment successor id");
      unit.Fragments.Add(new(function, blockId, offset, length, bodyLength, control,
        primary, secondary, condition, successors));
    }

    var relativeCount = r.ReadUInt32();
    for (var n = 0u; n < relativeCount; ++n)
      unit.RelativeFixups.Add(new(r.ReadUInt32(), r.ReadByte(),
        (PbuRelativeFixupKind)r.ReadByte(), r.ReadByte(), r.ReadUInt32()));

    return unit;
  }

  private static ushort CheckedCount(int count, string what) {
    if ((uint)count > ushort.MaxValue)
      throw new InvalidDataException($"too many {what}: {count}");
    return (ushort)count;
  }

  private static void WriteNonNegative(BinaryWriter writer, int value, string what) {
    if (value < 0)
      throw new InvalidDataException($"{what} cannot be negative: {value}");
    writer.Write(value);
  }

  private static int ReadNonNegativeInt32(BinaryReader reader, string what) {
    var value = reader.ReadInt32();
    if (value < 0)
      throw new InvalidDataException($"{what} cannot be negative: {value}");
    return value;
  }

  private static void WriteString(BinaryWriter w, string value) {
    var bytes = Encoding.ASCII.GetBytes(value);
    if (bytes.Length > byte.MaxValue)
      throw new InvalidDataException($"name too long: {value}");
    w.Write((byte)bytes.Length);
    w.Write(bytes);
  }

  private static string ReadString(BinaryReader r) => Encoding.ASCII.GetString(r.ReadBytes(r.ReadByte()));
}
