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

public sealed record PbuFixup(uint Offset, PbuFixupKind Kind, ushort Target);

/// <summary>CPU/feature requirement flags of a unit.</summary>
[Flags]
public enum PbuCpuFlags : ushort { None = 0, Needs186 = 1, Needs286 = 2, Needs386 = 4, UsesFpu = 8 }

/// <summary>
/// A compiled unit (<c>$COMPILE UNIT</c>) in PB-Compiler's own documented
/// container format - see docs/FORMATS.md. Not compatible with proprietary
/// PowerBASIC 3.5 units by design (REQUIREMENTS.md W2).
/// </summary>
public sealed class PbuFile {

  private static readonly byte[] _magic = "PBU1"u8.ToArray();
  private const ushort _version = 1;

  public required string Name { get; init; }
  public PbuCpuFlags CpuFlags { get; init; }
  public List<PbuExport> Exports { get; } = [];
  public List<PbuImport> Imports { get; } = [];
  public List<PbuCommonBlock> Commons { get; } = [];
  public byte[] Code { get; set; } = [];
  public byte[] Data { get; set; } = [];
  public uint BssSize { get; set; }
  public List<PbuFixup> Fixups { get; } = [];

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

    w.Write((ushort)this.Exports.Count);
    foreach (var e in this.Exports) {
      WriteString(w, e.Name);
      w.Write((byte)e.Kind);
      w.Write(e.SignatureHash);
      w.Write(e.CodeOffset);
    }

    w.Write((ushort)this.Imports.Count);
    foreach (var i in this.Imports) {
      WriteString(w, i.Name);
      w.Write(i.SignatureHash);
    }

    w.Write((ushort)this.Commons.Count);
    foreach (var c in this.Commons) {
      WriteString(w, c.Name);
      w.Write(c.Size);
    }

    w.Write((uint)this.Code.Length);
    w.Write(this.Code);
    w.Write((uint)this.Data.Length);
    w.Write(this.Data);
    w.Write(this.BssSize);

    w.Write((ushort)this.Fixups.Count);
    foreach (var f in this.Fixups) {
      w.Write(f.Offset);
      w.Write((byte)f.Kind);
      w.Write(f.Target);
    }
  }

  public static PbuFile Read(Stream stream) {
    using var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
    if (!r.ReadBytes(4).AsSpan().SequenceEqual(_magic))
      throw new InvalidDataException("not a PBU1 unit file");
    var version = r.ReadUInt16();
    if (version != _version)
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

    return unit;
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
