using System.Text;
using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  // $COMPILE UNIT / $LINK state: external calls allowed (resolved by the
  // linker), unit mode (no runtime emission), MZ relocation sites after linking
  private bool _isUnit;
  private bool _allowExternalCalls;
  private IReadOnlyList<int> _linkedSegmentSites = [];

  // listing (--list) bookkeeping: the code/data split captured at emission time
  // (the data area begins where the code ends). Pure reporting, never affects output.
  private int _listingCodeLength;
  private int _listingDataLength;

  /// <summary>One procedure entry in a <see cref="ListingInfo"/>.</summary>
  public readonly record struct ListingProcedure(string Name, bool IsFunction, string Signature, int CodeOffset, bool IsExternal);

  /// <summary>One named offset (a bound runtime label or a module data slot) in a <see cref="ListingInfo"/>.</summary>
  public readonly record struct ListingSymbol(string Name, int Offset, int Size);

  /// <summary>
  /// A read-only, post-emission snapshot of the compiled image for the <c>--list</c>
  /// front-end path: procedure offsets/signatures, bound runtime labels, module data
  /// slots and the code/data/bss sizes. Has no effect on code generation.
  /// </summary>
  public readonly record struct ListingInfo(
    int CodeLength,
    int DataLength,
    int BssSize,
    PbuCpuFlags CpuFlags,
    IReadOnlyList<ListingProcedure> Procedures,
    IReadOnlyList<ListingSymbol> RuntimeLabels,
    IReadOnlyList<ListingSymbol> DataSlots);

  /// <summary>
  /// Builds a <see cref="ListingInfo"/> from the just-emitted image. Call AFTER
  /// <see cref="EmitExecutable(IReadOnlyList{PbuFile}, IReadOnlyList{PblFile}, IReadOnlyList{Emit.Omf.OmfLibrary})"/>
  /// or <see cref="EmitUnit"/> - the procedure/data labels are bound to their final
  /// offsets only then. Pure reporting (read-only), so it never alters output.
  /// </summary>
  public ListingInfo DescribeImage() {
    var relocatable = this._asm.ToRelocatable();
    var bound = relocatable.BoundLabels;

    var procedures = this._procLabels
      .Select(kv => new ListingProcedure(
        kv.Key.Name,
        kv.Key.IsFunction,
        SignatureOf(kv.Key),
        kv.Value.IsBound ? kv.Value.Position : -1,
        kv.Key.IsExternal))
      .OrderBy(p => p.IsExternal)        // defined first, then link-resolved
      .ThenBy(p => p.CodeOffset)
      .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();

    // bound rt_* labels are the program's runtime surface (DescribeImage is the only consumer)
    var runtimeLabels = bound
      .Where(kv => kv.Key.StartsWith("rt_", StringComparison.Ordinal))
      .OrderBy(kv => kv.Value)
      .ThenBy(kv => kv.Key, StringComparer.Ordinal)
      .Select(kv => new ListingSymbol(kv.Key, kv.Value, 0))
      .ToList();

    var dataSlots = this._variableSlots
      .Where(kv => kv.Value.IsBound && (this._deadGlobals is null || !this._deadGlobals.Contains(kv.Key)))
      .Select(kv => new ListingSymbol(kv.Key.Name, kv.Value.Position, Math.Max(kv.Key.Type.Size, 1)))
      .OrderBy(s => s.Offset)
      .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();

    // BSS spans rt_bss_off..rt_bss_end when virtual BSS is enabled (pb36 self-contained main)
    var bssSize = bound.TryGetValue("rt_bss_off", out var bssOff) && bound.TryGetValue("rt_bss_end", out var bssEnd)
      ? Math.Max(bssEnd - bssOff, 0)
      : 0;

    var codeLength = this._listingCodeLength > 0 ? this._listingCodeLength : relocatable.Image.Length;
    return new ListingInfo(codeLength, this._listingDataLength, bssSize, this.CpuRequirementFlags(), procedures, runtimeLabels, dataSlots);
  }

  /// <summary>The unit-style CPU/feature requirement flags derived from $CPU (listing/header reporting).</summary>
  private PbuCpuFlags CpuRequirementFlags() {
    var flags = PbuCpuFlags.None;
    if (this.Cpu386)
      flags |= PbuCpuFlags.Needs386;
    if (this.Cpu486)
      flags |= PbuCpuFlags.Needs386; // 486 still requires (at least) a 386
    return flags;
  }

  /// <summary>
  /// Compiles a <c>$COMPILE UNIT</c> module: only procedures (module-level
  /// executable code is an error), every defined SUB/FUNCTION exported, every
  /// DECLAREd-but-undefined procedure and referenced runtime symbol imported.
  /// </summary>
  public PbuFile EmitUnit(string name) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    var asm = this._asm;
    this._isUnit = true;
    this._allowExternalCalls = true;
    this._scratch = asm.DefineLabel("cg_scratch");
    this._rt.BindExternal(asm);

    // a $COMPILE UNIT/LIB still optimizes its procedures' bodies (flow/data/statements),
    // but must NOT change calling semantics or drop any procedure - they are exported for
    // separate compilation. So only the intra-procedure passes run here: dead-statement
    // pruning and float demotion. The whole-program passes are deliberately excluded -
    // IPCP needs every call site (external callers are invisible to a unit), register
    // parameter passing changes the ABI, and dead-procedure elimination removes exports.
    if (this.Optimize) {
      OptPruner.Prune(model);
      OptFloatDemotion.Apply(model);
    }

    foreach (var statement in model.MainBody)
      switch (statement) {
        case MetaStmt or EquateStmt or DefTypeStmt or LabelStmt:
          break; // bookkeeping - nothing to execute

        case DataStmt d:
          this.Errors.Add(new(d.Position, "DATA is only allowed in the main module, not in a $COMPILE UNIT"));
          break;

        case DimStmt: // static module storage is laid out at compile time; dynamic
          break;      // descriptors start zeroed - procedures DIM/REDIM them at run time

        default:
          this.Errors.Add(new(statement.Position, "module-level code is not allowed in a $COMPILE UNIT"));
          break;
      }

    foreach (var proc in model.Procedures.Values)
      if (!proc.IsExternal)
        this.EmitProcedure(proc);

    this.EmitFarThunks();
    asm.Align(2, 0x90); // word-align so the data area stays aligned wherever the code lands
    var codeLength = asm.Position;
    this._listingCodeLength = codeLength; // listing: code/data split for DescribeImage
    this.EmitDataArea();
    asm.Align(2);
    this._listingDataLength = asm.Position - codeLength;

    var relocatable = asm.ToRelocatable();
    var unit = new PbuFile { Name = name };
    foreach (var (proc, label) in this._procLabels)
      if (!proc.IsExternal)
        unit.Exports.Add(new(proc.Name, proc.IsFunction ? PbuExportKind.Function : PbuExportKind.Sub,
          PbuFile.HashSignature(SignatureOf(proc)), (uint)label.Position));

    this.AddImportsAndFixups(unit, relocatable, codeLength);
    unit.Code = relocatable.Image[..codeLength];
    unit.Data = relocatable.Image[codeLength..];
    return unit;
  }

  /// <summary>
  /// Links the already-emitted main image against <paramref name="units"/> and
  /// <paramref name="libraries"/>: the main program becomes a unit-shaped image
  /// whose code blob keeps its own data inside (all internal references are
  /// final because the main image always lands at offset 0), exporting its
  /// defined procedures plus every bound runtime symbol so unit imports
  /// (rt_*) resolve against the embedded runtime.
  /// </summary>
  private byte[] LinkImage(IReadOnlyList<PbuFile> units, IReadOnlyList<PblFile> libraries, IReadOnlyList<Emit.Omf.OmfLibrary> omfLibraries) {
    var relocatable = this._asm.ToRelocatable();
    var main = new PbuFile { Name = "MAIN", Code = relocatable.Image };

    foreach (var (proc, label) in this._procLabels)
      if (!proc.IsExternal)
        main.Exports.Add(new(proc.Name, proc.IsFunction ? PbuExportKind.Function : PbuExportKind.Sub,
          PbuFile.HashSignature(SignatureOf(proc)), (uint)label.Position));

    // runtime export table: every bound named label (rt_*), hash 0 = unchecked
    foreach (var (symbol, position) in relocatable.BoundLabels)
      main.Exports.Add(new(symbol, PbuExportKind.Sub, 0u, (uint)position));

    this.AddImportsAndFixups(main, relocatable, relocatable.Image.Length);

    var linker = new Linker();
    foreach (var unit in units)
      linker.AddUnit(unit);
    foreach (var library in libraries)
      linker.AddLibrary(library);
    foreach (var omf in omfLibraries)
      linker.AddOmfLibrary(omf);

    try {
      var linked = linker.Link(main);
      if (linked.Code.Length + linked.Data.Length > 0x10000) {
        this.Errors.Add(new(default, $"linked image is {linked.Code.Length + linked.Data.Length} bytes - the single-segment model allows at most 64 KiB"));
        return [];
      }
      this._linkedSegmentSites = linked.SegmentRelocationSites;
      return [.. linked.Code, .. linked.Data];
    } catch (LinkException e) {
      this.Errors.Add(new(default, $"link: {e.Message}"));
      return [];
    } catch (Emit.Omf.OmfException e) {
      // a lazily-pulled foreign library member uses an OMF feature the tiny model cannot host
      this.Errors.Add(new(default, $"link: {e.Message}"));
      return [];
    }
  }

  /// <summary>
  /// Converts assembler relocations into PBU imports + fixups: internal
  /// absolute references become NearCode/DataOffset (split at
  /// <paramref name="codeLength"/>), segment words become Segment fixups, and
  /// external references become ImportCall (near displacement) or ImportOffset
  /// (absolute, addend kept in the site). Import hashes come from the DECLAREd
  /// signature; non-procedure symbols (runtime imports) hash as 0.
  /// </summary>
  private void AddImportsAndFixups(PbuFile unit, RelocatableImage relocatable, int codeLength) {
    var image = relocatable.Image;
    var importIndex = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);

    ushort ImportOf(string symbol) {
      if (importIndex.TryGetValue(symbol, out var index))
        return index;

      importIndex[symbol] = index = (ushort)unit.Imports.Count;
      var hash = model.Procedures.TryGetValue(symbol, out var proc) ? PbuFile.HashSignature(SignatureOf(proc)) : 0u;
      unit.Imports.Add(new(symbol, hash));
      return index;
    }

    foreach (var (site, kind, symbol) in relocatable.Relocations) {
      if (site >= codeLength) {
        this.Errors.Add(new(default, $"unit data area may not contain relocations (offset {site:X4})"));
        continue;
      }

      switch (kind) {
        case AsmRelocationKind.Absolute: {
          var value = image[site] | image[site + 1] << 8;
          if (value < codeLength) {
            unit.Fixups.Add(new((uint)site, PbuFixupKind.NearCode, 0));
            break;
          }
          value -= codeLength; // store relative to the unit's data base
          image[site] = (byte)value;
          image[site + 1] = (byte)(value >> 8);
          unit.Fixups.Add(new((uint)site, PbuFixupKind.DataOffset, 0));
          break;
        }

        case AsmRelocationKind.Segment:
          unit.Fixups.Add(new((uint)site, PbuFixupKind.Segment, 0));
          break;

        case AsmRelocationKind.ExternalRelative:
          unit.Fixups.Add(new((uint)site, PbuFixupKind.ImportCall, ImportOf(symbol!)));
          break;

        default:
          unit.Fixups.Add(new((uint)site, PbuFixupKind.ImportOffset, ImportOf(symbol!)));
          break;
      }
    }
  }

  #region canonical signatures

  /// <summary>
  /// The canonical signature string hashed into PBU exports/imports:
  /// <c>NAME(byval:type,byref:type,seg:type,...)-&gt;returntype</c> using
  /// lower-case PB type names (array parameters append <c>()</c>); hashing is
  /// case-insensitive. Documented in docs/FORMATS.md.
  /// </summary>
  public static string SignatureOf(ProcedureSymbol proc) {
    ArgumentNullException.ThrowIfNull(proc);
    var text = new StringBuilder(proc.Name).Append('(');
    for (var i = 0; i < proc.Parameters.Count; ++i) {
      if (i > 0)
        text.Append(',');
      var parameter = proc.Parameters[i];
      text.Append(parameter.ByVal ? "byval" : parameter.Seg ? "seg" : "byref").Append(':').Append(TypeNameOf(parameter.Type));
    }
    text.Append(')');
    if (proc.IsFunction)
      text.Append("->").Append(TypeNameOf(proc.ReturnType ?? PbType.Integer));
    return text.ToString();
  }

  private static string TypeNameOf(PbType type) => type switch {
    ScalarType s => s.Kind.ToString().ToLowerInvariant(),
    StringType => "string",
    FixedStringType f => $"string*{f.Length}",
    FlexType => "flex",
    AnyType => "any",
    UdtType u => u.Name,
    ArrayType a => TypeNameOf(a.Element) + "()",
    _ => "?",
  };

  #endregion
}
