using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 runtime trimming (docs/PB36.md P1/P2/P4): a one-time probe emission of
/// the FULL runtime maps every named runtime label to the section providing it
/// and every section to the labels it references; a reachability closure from
/// the labels the user program actually references then selects the minimal
/// section set. The probe is derived from the genuine emission, so the
/// dependency graph can never drift from the real runtime.
/// </summary>
internal static class RuntimeTrimmer {

  /// <summary>One runtime code/data section and the foreign labels its bytes reference.</summary>
  internal sealed record Section(string Name, IReadOnlySet<string> Needs);

  internal sealed record Analysis(
    IReadOnlyList<Section> Sections,
    IReadOnlyDictionary<string, string> ProviderOf,
    IReadOnlySet<string> EntryNeeds) {

    /// <summary>Closes <paramref name="seedLabels"/> (plus the entry stub's needs) over the section graph.</summary>
    internal HashSet<string> CloseOver(IEnumerable<string> seedLabels) {
      var byName = this.Sections.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
      var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var pending = new Stack<string>();
      foreach (var label in seedLabels.Concat(this.EntryNeeds))
        pending.Push(label);

      while (pending.Count > 0) {
        var label = pending.Pop();
        if (!seen.Add(label))
          continue;
        if (!this.ProviderOf.TryGetValue(label, out var sectionName))
          continue; // user/codegen label - bound outside the runtime
        if (!needed.Add(sectionName))
          continue;
        foreach (var need in byName[sectionName].Needs)
          pending.Push(need);
      }
      return needed;
    }
  }

  private static readonly Lazy<Analysis> _instance = new(Analyze);

  /// <summary>The cached probe analysis (the runtime is static, so one probe serves the process).</summary>
  internal static Analysis Instance => _instance.Value;

  private static Analysis Analyze() {
    var asm = new Assembler();
    var rt = new DosRuntime();
    var boundaries = new List<(string Name, int Start, int End)>();

    var entryStart = asm.Position;
    rt.EmitEntry(asm, asm.DefineLabel());
    boundaries.Add(("<entry>", entryStart, asm.Position));

    rt.EmitProcedures(asm, onSection: (name, start, end) => boundaries.Add((name, start, end)));

    var constStart = asm.Position;
    rt.EmitConstants(asm);
    boundaries.Add(("consts", constStart, asm.Position));

    rt.EmitData(asm, onSection: (name, start, end) => boundaries.Add((name, start, end)));

    // provider map: every bound REGISTERED label belongs to the section containing it
    var providerOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var label in asm.KnownNamedLabels.Where(l => l.IsBound))
      foreach (var (name, start, end) in boundaries)
        if (name != "<entry>" && label.Position >= start && label.Position < end) {
          providerOf[label.Name!] = name;
          break;
        }

    // per-section needs: named targets referenced in the range but provided elsewhere
    var references = asm.LabelReferences().Where(r => r.Target.Name != null).ToList();
    var sections = new List<Section>();
    IReadOnlySet<string> entryNeeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (name, start, end) in boundaries) {
      var needs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var (position, target) in references)
        if (position >= start && position < end
            && providerOf.TryGetValue(target.Name!, out var provider)
            && !provider.Equals(name, StringComparison.OrdinalIgnoreCase))
          needs.Add(target.Name!);

      if (name == "<entry>")
        entryNeeds = needs;
      else
        sections.Add(new(name, needs));
    }

    return new(sections, providerOf, entryNeeds);
  }
}
