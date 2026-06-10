using System.Text;

namespace PowerBasic.Compiler.Emit;

/// <summary>
/// A unit library (<c>.PBL</c>): a table-of-contents over concatenated
/// <see cref="PbuFile"/> blobs. <c>$LINK "X.PBL"</c> makes all units available;
/// only units satisfying unresolved imports are pulled in (see docs/FORMATS.md).
/// </summary>
public sealed class PblFile {

  private static readonly byte[] _magic = "PBL1"u8.ToArray();
  private const ushort _version = 1;

  public List<PbuFile> Units { get; } = [];

  public PbuFile? FindUnit(string name) => this.Units.FirstOrDefault(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

  /// <summary>Finds the first unit exporting <paramref name="symbol"/>.</summary>
  public PbuFile? FindExporter(string symbol) => this.Units.FirstOrDefault(u => u.Exports.Any(e => e.Name.Equals(symbol, StringComparison.OrdinalIgnoreCase)));

  public void Write(Stream stream) {
    // serialize the units first to learn their lengths for the TOC
    var blobs = new List<(string Name, byte[] Bytes)>();
    foreach (var unit in this.Units) {
      using var buffer = new MemoryStream();
      unit.Write(buffer);
      blobs.Add((unit.Name, buffer.ToArray()));
    }

    using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
    w.Write(_magic);
    w.Write(_version);
    w.Write((ushort)blobs.Count);

    // TOC size must be known to compute blob offsets: name lengths are fixed now
    var tocSize = 4 + 2 + 2 + blobs.Sum(b => 1 + Encoding.ASCII.GetByteCount(b.Name) + 4 + 4);
    var offset = (uint)tocSize;
    foreach (var (name, bytes) in blobs) {
      var nameBytes = Encoding.ASCII.GetBytes(name);
      w.Write((byte)nameBytes.Length);
      w.Write(nameBytes);
      w.Write(offset);
      w.Write((uint)bytes.Length);
      offset += (uint)bytes.Length;
    }

    foreach (var (_, bytes) in blobs)
      w.Write(bytes);
  }

  public static PblFile Read(Stream stream) {
    var start = stream.Position;
    using var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
    if (!r.ReadBytes(4).AsSpan().SequenceEqual(_magic))
      throw new InvalidDataException("not a PBL1 library file");
    var version = r.ReadUInt16();
    if (version != _version)
      throw new InvalidDataException($"unsupported PBL version {version}");

    var entries = new List<(uint Offset, uint Length)>();
    for (var n = r.ReadUInt16(); n > 0; --n) {
      _ = Encoding.ASCII.GetString(r.ReadBytes(r.ReadByte())); // TOC name (units carry their own)
      entries.Add((r.ReadUInt32(), r.ReadUInt32()));
    }

    var library = new PblFile();
    foreach (var (offset, length) in entries) {
      stream.Position = start + offset;
      var blob = r.ReadBytes(checked((int)length));
      using var unitStream = new MemoryStream(blob);
      library.Units.Add(PbuFile.Read(unitStream));
    }

    return library;
  }
}
