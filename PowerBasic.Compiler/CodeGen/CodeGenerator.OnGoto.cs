using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  /// <summary>
  /// ON n GOTO/GOSUB l1, l2, ...: the 1-based selector picks the target;
  /// out-of-range values (0 or beyond the list) fall through to the next
  /// statement, like PB. The GOSUB variant returns behind the dispatch.
  /// </summary>
  private void EmitOnGoto(OnGotoStmt og) {
    var asm = this._asm;
    this.EmitExpression(og.Selector);
    this.Coerce(model.TypeOf(og.Selector), PbType.Integer, og.Selector);

    // O0029: for four or more targets the 1-based selector indexes a jump table (O(1) dispatch) instead of the
    // linear dec/JNZ chain - both smaller and faster past three targets. The single unsigned bounds check
    // reproduces PB's fall-through: selector 1 gives index 0; selector 0 decrements to 0xFFFF which is >= count
    // (unsigned), and any value beyond the list is >= count too, so both skip to the next statement. A word
    // table of the target label offsets follows the indexed jump, reached only as data. Optimize-gated, so the
    // faithful build keeps the chain byte-for-byte.
    if (this.Optimize && og.Targets.Count >= 4) {
      var doneT = asm.DefineLabel();
      var table = asm.DefineLabel();
      asm.Dec(Reg.AX);                              // 1-based selector -> 0-based index
      asm.Cmp(Reg.AX, (Imm)og.Targets.Count);
      asm.Jae(doneT);                                // unsigned: out of 0..count-1 falls through
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 1);                            // word-sized entries
      if (og.IsGosub) {
        asm.Call(Mem.Word(Reg.BX, table));           // GOSUB: indirect call, returns behind here
        asm.Jmp(doneT);
      } else
        asm.Jmp(Mem.Word(Reg.BX, table));            // GOTO: indirect jump out of the dispatch
      asm.MarkLabel(table);                          // data: reached only via the indexed jump above
      foreach (var target in og.Targets)
        asm.Dw(this.UserLabel(target));
      asm.MarkLabel(doneT);
      return;
    }

    var done = asm.DefineLabel();
    foreach (var target in og.Targets) {
      var next = asm.DefineLabel();
      asm.Dec(Reg.AX);                 // selector value 1 selects the first label
      asm.Jnz(next);
      if (og.IsGosub) {
        asm.Call(this.UserLabel(target));
        asm.Jmp(done);
      } else
        asm.Jmp(this.UserLabel(target));
      asm.MarkLabel(next);
    }
    asm.MarkLabel(done);
  }
}
