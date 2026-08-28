using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  // DATA pool: module-level DATA statements collected in source order into a
  // [length word][bytes] sequence in the data area; rt_dataptr walks it.
  private List<string>? _dataItems;
  private Dictionary<string, int>? _dataLabelOffsets;

  /// <summary>True once a READ or RESTORE that actually references the DATA pool has been emitted - otherwise the pool is dead and is omitted.</summary>
  private bool _dataPoolReferenced;

  private void EnsureDataPool() {
    if (this._dataItems != null)
      return;
    this._dataItems = [];
    this._dataLabelOffsets = new(StringComparer.OrdinalIgnoreCase);
    var offset = 0;
    // Runtime.DataPool.Walk rather than a loop over MainBody: DATA is not executable, so a statement
    // written inside an IF/FOR/DO/SELECT block still contributes to the pool - which this walked past,
    // giving a program that reads such an item the NEXT one instead, or Error 4 when there was none.
    foreach (var (label, item) in Runtime.DataPool.Walk(model.MainBody))
      if (label is not null)
        this._dataLabelOffsets[label] = offset;
      else {
        this._dataItems.Add(item!);
        offset += 2 + item!.Length;
      }
  }

  private void EmitRead(ReadStmt read) {
    this.EnsureDataPool();
    this._dataPoolReferenced = true;   // rt_readdata walks rt_dataptr/rt_datapool
    foreach (var target in read.Targets) {
      this._asm.Call(this._asm.Lbl("rt_readdata"));
      this.EmitStoreReadValue(target);
    }
  }

  private void EmitRestore(RestoreStmt restore) {
    this.EnsureDataPool();
    this._dataPoolReferenced = true;   // RESTORE points rt_dataptr at rt_datapool + offset
    var offset = 0;
    if (restore.Target is { } target && !this._dataLabelOffsets!.TryGetValue(target, out offset)) {
      this.Unsupported(restore);
      return;
    }
    this._asm.Mov(Mem.Word(this._asm.Lbl("rt_dataptr")), Imm.OffsetOf(this._asm.Lbl("rt_datapool"), offset));
  }

  /// <summary>Emits the DATA pool plus its read pointer; called from the data area.</summary>
  private void EmitDataPool() {
    var asm = this._asm;
    this.EnsureDataPool();
    asm.Align(2);
    asm.MarkLabel("rt_dataptr");
    asm.Dw(asm.Lbl("rt_datapool"));
    asm.MarkLabel("rt_datapool");
    // the read pointer (rt_dataptr) is referenced unconditionally by the runtime, so the labels stay
    // bound - but the DATA bytes themselves are dead when nothing reads them, so emit an empty pool
    if (this._dataPoolReferenced)
      foreach (var item in this._dataItems!) {
        asm.Dw((ushort)item.Length);
        if (item.Length > 0)
          asm.Db(item);
      }
    asm.MarkLabel("rt_dataend");
  }
}
