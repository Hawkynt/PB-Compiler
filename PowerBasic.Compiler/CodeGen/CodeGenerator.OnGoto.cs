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
