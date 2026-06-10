namespace PowerBasic.Compiler.Emit;

/// <summary>One linked artifact ready for the MZ writer.</summary>
public sealed record LinkedImage(byte[] Code, byte[] Data, uint BssSize, IReadOnlyList<int> SegmentRelocationSites, IReadOnlyDictionary<string, uint> ResolvedExports);

/// <summary>Raised when symbol resolution fails or signatures mismatch.</summary>
public sealed class LinkException(string message) : Exception(message);

/// <summary>
/// Resolves a main image's imports against explicitly linked units and
/// libraries (docs/FORMATS.md "Linking model"): units link unconditionally,
/// libraries contribute only units that satisfy unresolved imports; signature
/// hashes must match; everything is laid out into one combined code+data image.
/// </summary>
public sealed class Linker {

  private readonly List<PbuFile> _mandatoryUnits = [];
  private readonly List<PblFile> _libraries = [];

  public void AddUnit(PbuFile unit) => this._mandatoryUnits.Add(unit);
  public void AddLibrary(PblFile library) => this._libraries.Add(library);

  /// <summary>
  /// Links <paramref name="main"/> (a unit-shaped image whose exports are the
  /// program's own procedures) into one image. Layout: main code, then each
  /// pulled unit's code; data areas concatenated after all code in the same order.
  /// </summary>
  public LinkedImage Link(PbuFile main) {
    // 1. decide which units participate: all mandatory ones, plus library units
    //    pulled in while imports remain unresolved (transitively)
    var participating = new List<PbuFile> { main };
    participating.AddRange(this._mandatoryUnits);

    var exporters = new Dictionary<string, (PbuFile Unit, PbuExport Export)>(StringComparer.OrdinalIgnoreCase);
    void Index(PbuFile unit) {
      foreach (var export in unit.Exports)
        if (!exporters.TryAdd(export.Name, (unit, export)))
          throw new LinkException($"duplicate symbol {export.Name} (in {exporters[export.Name].Unit.Name} and {unit.Name})");
    }
    foreach (var unit in participating)
      Index(unit);

    for (var grew = true; grew;) {
      grew = false;
      foreach (var import in participating.SelectMany(u => u.Imports).ToList()) {
        if (exporters.ContainsKey(import.Name))
          continue;
        var provider = this._libraries.Select(l => l.FindExporter(import.Name)).FirstOrDefault(u => u != null);
        if (provider == null)
          continue; // reported below once the pull set is final
        participating.Add(provider);
        Index(provider);
        grew = true;
      }
    }

    // 2. verify all imports resolve with matching signatures
    foreach (var unit in participating)
      foreach (var import in unit.Imports) {
        if (!exporters.TryGetValue(import.Name, out var found))
          throw new LinkException($"unresolved symbol {import.Name} (imported by {unit.Name})");
        if (found.Export.SignatureHash != import.SignatureHash)
          throw new LinkException($"signature mismatch for {import.Name}: {unit.Name} expects a different parameter list than {found.Unit.Name} provides");
      }

    // 3. layout: code blocks back to back, then data blocks; BSS accumulates.
    // Blocks are kept word-aligned so the units' internal Align(2) data holds.
    var codeBase = new Dictionary<PbuFile, uint>(ReferenceEqualityComparer.Instance);
    var dataBase = new Dictionary<PbuFile, uint>(ReferenceEqualityComparer.Instance);
    var codeSize = 0u;
    foreach (var unit in participating) {
      codeSize = (codeSize + 1) & ~1u;
      codeBase[unit] = codeSize;
      codeSize += (uint)unit.Code.Length;
    }
    codeSize = (codeSize + 1) & ~1u; // the data area starts word-aligned behind the code
    var dataSize = 0u;
    var bssSize = 0u;
    foreach (var unit in participating) {
      dataSize = (dataSize + 1) & ~1u;
      dataBase[unit] = dataSize;
      dataSize += (uint)unit.Data.Length;
      bssSize += unit.BssSize;
    }

    var code = new byte[codeSize];
    var data = new byte[dataSize];
    foreach (var unit in participating) {
      unit.Code.CopyTo(code, codeBase[unit]);
      unit.Data.CopyTo(data, dataBase[unit]);
    }

    // 4. apply fixups (data offsets are relative to the data base which the
    //    caller appends right after code - data fixup sites get code length added)
    var segmentSites = new List<int>();
    foreach (var unit in participating)
      foreach (var fixup in unit.Fixups) {
        var site = (int)(codeBase[unit] + fixup.Offset);
        switch (fixup.Kind) {
          case PbuFixupKind.NearCode:
            Patch16(code, site, (ushort)(Read16(code, site) + codeBase[unit]));
            break;

          case PbuFixupKind.DataOffset:
            Patch16(code, site, (ushort)(Read16(code, site) + codeSize + dataBase[unit]));
            break;

          case PbuFixupKind.Segment:
            segmentSites.Add(site);
            break;

          case PbuFixupKind.ImportCall or PbuFixupKind.ImportOffset: {
            if (fixup.Target >= unit.Imports.Count)
              throw new LinkException($"fixup in {unit.Name} references import #{fixup.Target} of {unit.Imports.Count}");
            var import = unit.Imports[fixup.Target];
            var (providerUnit, export) = exporters[import.Name];
            var target = codeBase[providerUnit] + export.CodeOffset;
            if (fixup.Kind == PbuFixupKind.ImportOffset)
              Patch16(code, site, (ushort)(Read16(code, site) + target)); // site keeps its addend
            else
              // near-call displacement is relative to the byte after the 16-bit operand
              Patch16(code, site, (ushort)(target - (uint)(site + 2)));
            break;
          }

          default:
            throw new LinkException($"unknown fixup kind {fixup.Kind} in {unit.Name}");
        }
      }

    var resolved = exporters.ToDictionary(kv => kv.Key, kv => codeBase[kv.Value.Unit] + kv.Value.Export.CodeOffset, StringComparer.OrdinalIgnoreCase);
    return new(code, data, bssSize, segmentSites, resolved);
  }

  private static ushort Read16(byte[] image, int site) => (ushort)(image[site] | (image[site + 1] << 8));

  private static void Patch16(byte[] image, int site, ushort value) {
    image[site] = (byte)value;
    image[site + 1] = (byte)(value >> 8);
  }
}
