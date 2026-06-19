using System.Globalization;
using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Emit;

/// <summary>
/// Renders a human-readable listing (<c>.LST</c>) of a compiled program: a map
/// of the emitted image with its header (sizes, dialect, target, CPU flags), a
/// procedure/symbol table (each SUB/FUNCTION and bound runtime label with its
/// code offset and signature), the module data layout, and the unit's
/// imports/exports. Pure and deterministic so it can be string-asserted in tests.
/// </summary>
public static class Listing {

  /// <summary>
  /// Builds the listing text for a program. <paramref name="info"/> is the
  /// post-emission snapshot from <see cref="CodeGenerator.DescribeImage"/>;
  /// <paramref name="unit"/> is the compiled unit when the source is a
  /// <c>$COMPILE UNIT</c> (its exports/imports are listed), otherwise null.
  /// </summary>
  public static string Render(string sourceName, SemanticModel model, CodeGenerator.ListingInfo info, PbuFile? unit = null) {
    ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
    ArgumentNullException.ThrowIfNull(model);

    var text = new StringBuilder();
    var isUnit = unit != null;

    text.AppendLine("PB-Compiler listing");
    text.AppendLine($"  source   : {sourceName}");
    text.AppendLine($"  dialect  : {model.Dialect.DisplayName()}");
    text.AppendLine($"  target   : {(isUnit ? "PBU unit" : "16-bit real-mode DOS (MZ)")}");
    text.AppendLine($"  cpu      : {DescribeCpu(info.CpuFlags)}");
    text.AppendLine($"  code     : {Bytes(info.CodeLength)}");
    text.AppendLine($"  data     : {Bytes(info.DataLength)}");
    text.AppendLine($"  bss      : {Bytes(info.BssSize)}");
    text.AppendLine();

    // procedure / symbol table
    text.AppendLine("Procedures");
    if (info.Procedures.Count == 0)
      text.AppendLine("  (none)");
    else
      foreach (var proc in info.Procedures) {
        var kind = proc.IsExternal ? "EXTERN" : proc.IsFunction ? "FUNCTION" : "SUB";
        var offset = proc.IsExternal || proc.CodeOffset < 0 ? "    ----" : $"  {proc.CodeOffset:X4}";
        text.AppendLine($"{offset}  {kind,-8}  {proc.Signature}");
      }
    text.AppendLine();

    // bound runtime labels (the program's runtime surface)
    text.AppendLine("Runtime");
    if (info.RuntimeLabels.Count == 0)
      text.AppendLine("  (none)");
    else
      foreach (var label in info.RuntimeLabels)
        text.AppendLine($"  {label.Offset:X4}  {label.Name}");
    text.AppendLine();

    // module data layout
    text.AppendLine("Data");
    if (info.DataSlots.Count == 0)
      text.AppendLine("  (none)");
    else
      foreach (var slot in info.DataSlots)
        text.AppendLine($"  {slot.Offset:X4}  {slot.Size,6}  {slot.Name}");
    text.AppendLine();

    // imports / exports (units / linked programs)
    if (isUnit) {
      text.AppendLine("Exports");
      if (unit!.Exports.Count == 0)
        text.AppendLine("  (none)");
      else
        foreach (var e in unit.Exports.OrderBy(e => e.CodeOffset).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
          text.AppendLine($"  {e.CodeOffset:X4}  {(e.Kind == PbuExportKind.Function ? "FUNCTION" : "SUB"),-8}  {e.Name}");
      text.AppendLine();

      text.AppendLine("Imports");
      if (unit.Imports.Count == 0)
        text.AppendLine("  (none)");
      else
        foreach (var i in unit.Imports.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
          text.AppendLine($"        {i.Name}");
      text.AppendLine();
    }

    return text.ToString();
  }

  private static string Bytes(int count)
    => string.Create(CultureInfo.InvariantCulture, $"{count} bytes (0x{count:X})");

  private static string DescribeCpu(PbuCpuFlags flags) {
    if (flags == PbuCpuFlags.None)
      return "8086";
    var parts = new List<string>();
    if (flags.HasFlag(PbuCpuFlags.Needs386))
      parts.Add("80386");
    else if (flags.HasFlag(PbuCpuFlags.Needs286))
      parts.Add("80286");
    else if (flags.HasFlag(PbuCpuFlags.Needs186))
      parts.Add("80186");
    if (flags.HasFlag(PbuCpuFlags.UsesFpu))
      parts.Add("FPU");
    return parts.Count > 0 ? string.Join("+", parts) : "8086";
  }
}
