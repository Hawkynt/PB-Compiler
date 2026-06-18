using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  /// <summary>Bytes one argument occupies on the stack (BYREF = near pointer; BYVAL = value, word-aligned).</summary>
  private static int ParamSlotSize(VariableSymbol p) => p.ByVal ? Math.Max(2, (p.Type.Size + 1) & ~1) : 2;

  /// <summary>
  /// Registers that carry the leading word-sized arguments, in parameter order, for the
  /// register conventions (empirically matched to the genuine compilers): Watcom's WATCALL
  /// uses AX,DX,BX,CX; Microsoft/Borland FASTCALL use AX,DX,BX. Empty for stack conventions.
  /// </summary>
  private static Reg[] ConventionRegisters(CallConvention c) => c switch {
    CallConvention.Watcall => [Reg.AX, Reg.DX, Reg.BX, Reg.CX],
    CallConvention.Fastcall => [Reg.AX, Reg.DX, Reg.BX],
    _ => [],
  };

  /// <summary>True when the convention passes the leading arguments in registers (WATCALL/FASTCALL).</summary>
  private static bool IsRegisterConvention(ProcedureSymbol proc) => ConventionRegisters(proc.CallConv).Length > 0;

  /// <summary>How many of <paramref name="proc"/>'s parameters arrive in registers (the rest spill to the stack).</summary>
  private static int RegisterParamCount(ProcedureSymbol proc)
    => Math.Min(ConventionRegisters(proc.CallConv).Length, proc.Parameters.Count);

  /// <summary>
  /// True when a register convention is used but a parameter does not fit the common-case
  /// model (every parameter must be a single word - a BYVAL scalar &lt;= 2 bytes or a BYREF
  /// near pointer). LONG/float/struct/string-by-value in a register convention need the
  /// full per-compiler size rules, which we deliberately do not implement; reject them.
  /// </summary>
  private static bool HasUnsupportedRegisterParam(ProcedureSymbol proc)
    => IsRegisterConvention(proc) && proc.Parameters.Any(p => ParamSlotSize(p) != 2);

  /// <summary>True when the convention pushes (stack) arguments right to left: CDECL, STDCALL and WATCALL's overflow; BASIC/PASCAL/FASTCALL push left to right.</summary>
  private static bool PushesRightToLeft(ProcedureSymbol proc) => proc.CallConv is CallConvention.Cdecl or CallConvention.Stdcall or CallConvention.Watcall;

  /// <summary>True when the caller cleans the stack after the call (CDECL only); BASIC/STDCALL/PASCAL/FASTCALL/WATCALL clean any stack args in the callee via RET n.</summary>
  private static bool CallerCleansStack(ProcedureSymbol proc) => proc.CallConv == CallConvention.Cdecl;

  /// <summary>
  /// Assigns BP-relative offsets: parameters at [BP+4..] (pushed left to right -
  /// BASIC/PASCAL - so the last parameter sits at [BP+4]; CDECL/STDCALL push right
  /// to left, so the FIRST parameter sits at [BP+4]), stack locals below BP. STATIC
  /// variables and arrays use data segment slots instead.
  /// </summary>
  private int LayoutFrame(ProcedureSymbol proc) {
    this._frameLocalBytes = 0;

    // register-convention (WATCALL/FASTCALL) parameters arrive in registers; give them
    // negative slots at the top of the frame ([BP-2], [BP-4], ...) that the prologue fills
    // by spilling AX,DX,BX(,CX). Stack conventions take no register params (regCount = 0).
    var regCount = RegisterParamCount(proc);
    for (var i = 0; i < regCount; ++i) {
      this._frameLocalBytes += 2;
      proc.Parameters[i].Offset = -this._frameLocalBytes;
    }

    // stack parameters: a register convention's overflow (index >= regCount) or, for a
    // stack convention, every parameter. Positive [BP+4..] in push order - RTL puts the
    // first stack parameter at [BP+4], LTR puts the last there.
    var offset = 4;
    var stackParams = Enumerable.Range(regCount, proc.Parameters.Count - regCount).ToList();
    foreach (var i in PushesRightToLeft(proc) ? stackParams : Enumerable.Reverse(stackParams)) {
      proc.Parameters[i].Offset = offset;
      offset += ParamSlotSize(proc.Parameters[i]);
    }
    var paramBytes = offset - 4;

    foreach (var symbol in this.StackLocalsOf(proc)) {
      this._frameLocalBytes += Math.Max(2, (symbol.Type.Size + 1) & ~1);
      symbol.Offset = -this._frameLocalBytes;
    }
    return paramBytes;
  }

  private IEnumerable<VariableSymbol> StackLocalsOf(ProcedureSymbol proc) {
    var seen = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
    foreach (var symbol in proc.Variables.Values)
      if (symbol.Storage == VariableStorage.Local && !symbol.IsArray && seen.Add(symbol))
        yield return symbol;
  }

  private void EmitProcedure(ProcedureSymbol proc) {
    var asm = this._asm;
    this._currentProc = proc;
    var outerLabels = this._userLabels;
    this._userLabels = new(StringComparer.OrdinalIgnoreCase);
    var paramBytes = this.LayoutFrame(proc);
    this._currentParamBytes = paramBytes;
    if (HasUnsupportedRegisterParam(proc))
      this.Errors.Add(new(proc.Position, $"{proc.CallConv} {proc.Name}: a register-convention parameter must be word-sized (BYVAL <= 2 bytes or BYREF); LONG/float/UDT/string need the full per-compiler ABI rules"));
    this._epilogue = asm.DefineLabel($"p_{proc.Name}_end");
    this._trackResume = ContainsErrorHandling(proc.Body!);

    // pb36 C2 ($CPU 80486): 16-byte-align procedure entries to the 486 cache
    // line - reached only by CALL, so the NOP pad never executes
    if (this.Optimize && this.Cpu486)
      asm.AlignCode(16);
    asm.MarkLabel(this.ProcLabelOf(proc));
    // PB 3.6 capturing lambda entry: the env far pointer arrives in BX:CX. The frame
    // zeroing clobbers CX (its word counter) but not DX, so stash the env SEGMENT in
    // DX now - the save below writes BX (offset) and DX (segment) into the hidden
    // local. A stack closure's CX is SS; a heap closure's CX is the env block segment.
    if (proc.ClosureEnvPtr != null)
      asm.Mov(Reg.DX, Reg.CX);
    if (this.CheckStack) { // $ERROR STACK ON: SP headroom probe -> Error 201 (oracle-verified)
      var roomy = asm.DefineLabel();
      asm.Cmp(Reg.SP, Mem.Word(asm.Lbl("rt_stackmin")));
      asm.Ja(roomy);
      asm.Mov(Reg.AX, 201);
      asm.Call(this._rt.Raise);
      asm.MarkLabel(roomy);
    }

    // pb36 O19: when every (non-string) local is definitely assigned before
    // use and no error handler can re-enter with stale state, the whole-frame
    // zero fill collapses to zeroing just the dynamic-string handle slots
    // (those must stay 0 for the first StrAssign and the epilogue StrFree)
    var stackLocals = this.StackLocalsOf(proc).ToList();
    var elideZeroing = this.Optimize && !this._trackResume
      && CanElideFrameZeroing(model, proc.Body!, stackLocals);

    // pb36 O14: self-calls in tail position become frame-reusing jumps when
    // nothing must outlive the call - no error handler, no GOSUB returns, no
    // string/FLEX locals pending release, and every parameter is a small
    // BYVAL scalar whose slot can be rewritten in place
    this._tailSelfCalls = null;
    this._tailEntry = null;
    this._tailGeneralCalls = null;
    // A is eligible to drop a tail call when nothing in its frame must outlive the
    // call: no error handler or GOSUB returns, no string/FLEX local pending release
    // (numeric locals only), a stack (callee-cleans) convention with [BP+] params and
    // no capturing-lambda env save. The same proof the self-call optimization needs.
    var tailEligible = this.Optimize && !this._trackResume
        && !proc.IsCdecl
        && !IsRegisterConvention(proc)   // register params live in negative slots; the in-place tail rewrite assumes [BP+] stack params
        && proc.ClosureEnvPtr == null   // a capturing lambda's env-save must run on every entry
        && proc.Parameters.All(p => p.ByVal && p.Type is ScalarType { IsFloat: false, ByteSize: <= 4 })
        && stackLocals.All(l => l.Type is ScalarType)
        && !ContainsGosub(proc.Body!);
    if (tailEligible) {
      var tails = CollectTailSelfCalls(proc.Body!, proc, model);
      if (tails.Count > 0) {
        this._tailSelfCalls = tails;
        this._tailEntry = asm.DefineLabel($"p_{proc.Name}_tail");
      }
      // O14 general tail calls: tail-position calls to OTHER in-module procs B that
      // are themselves stack-convention with small BYVAL-scalar params (so B's frame
      // can be laid out by plain word pushes and B cleans its own args via RET n).
      // Restricted to SUBs: a FUNCTION's epilogue loads its result variable into the
      // return registers, which the tail jump would skip - a bare tail CALL inside a
      // FUNCTION must still fall through to that epilogue, so leave functions alone.
      if (!proc.IsFunction) {
        var general = CollectGeneralTailCalls(proc.Body!, proc, model);
        if (general.Count > 0)
          this._tailGeneralCalls = general;
      }
    }

    this.PrepareCse(proc.Body!);
    // the function result variable is read by RETURN, not by any body statement -
    // pass it as implicitly read so SCCP/DSE never fold or drop its writes
    var resultVar = proc.IsFunction && proc.Variables.TryGetValue(proc.Name, out var rv) ? rv : null;
    this.PrepareSccp(proc.Body!, resultVar);
    // register-convention entry: spill AX,DX,BX(,CX) into the parameters' negative slots
    var spillRegs = ConventionRegisters(proc.CallConv)[..RegisterParamCount(proc)];
    this.BeginFrame(elideZeroing, this._tailEntry, spillRegs);
    if (elideZeroing)
      foreach (var local in stackLocals)
        if (local.Type is StringType or FlexType)
          asm.Mov(Mem.Word(Reg.BP, local.Offset), (Imm)0);

    // PB 3.6 capturing lambda: save the far environment pointer into its hidden local.
    // It arrived in BX:CX; BX survives the frame setup and the segment was parked in DX
    // at entry (CX is clobbered by the frame zeroing) - so store BX (offset) and DX
    // (segment, = SS for a stack closure, = the heap block segment for an escaping one).
    if (proc.ClosureEnvPtr is { } envPtr) {
      asm.Mov(Mem.Word(Reg.BP, envPtr.Offset), Reg.BX);
      asm.Mov(Mem.Word(Reg.BP, envPtr.Offset + 2), Reg.DX);
    }

    // procedures that arm ON ERROR save and restore the caller's handler state
    Mem? savedHandler = null;
    if (this._trackResume) {
      savedHandler = this.AllocTemp(6);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_onerr")));
      asm.Mov(savedHandler.Value.WithSize(OperandSize.Word), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_onerr_bp")));
      asm.Mov(Adjust(savedHandler.Value, 2, OperandSize.Word), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_onerr_sp")));
      asm.Mov(Adjust(savedHandler.Value, 4, OperandSize.Word), Reg.AX);
    }

    // O16: the interval lattice for THIS procedure body (parameters start unknown). Procedures
    // are emitted after the main body and one at a time, so swapping the cached points here is
    // safe; restored afterwards for tidiness.
    var outerPoints = this._intervalPoints;
    if (this.Optimize)
      this._intervalPoints = IntervalRangeAnalysis.AnalyzeProgramPoints(proc.Body!, model);

    foreach (var statement in proc.Body!)
      this.EmitStatement(statement);

    this._intervalPoints = outerPoints;
    asm.MarkLabel(this._epilogue);
    if (savedHandler is { } saved) {
      asm.Mov(Reg.AX, saved.WithSize(OperandSize.Word));
      asm.Mov(Mem.Word(asm.Lbl("rt_onerr")), Reg.AX);
      asm.Mov(Reg.AX, Adjust(saved, 2, OperandSize.Word));
      asm.Mov(Mem.Word(asm.Lbl("rt_onerr_bp")), Reg.AX);
      asm.Mov(Reg.AX, Adjust(saved, 4, OperandSize.Word));
      asm.Mov(Mem.Word(asm.Lbl("rt_onerr_sp")), Reg.AX);
    }

    // release string ownership: stack locals and BYVAL string parameters
    // (resultVar computed above)
    foreach (var symbol in this.StackLocalsOf(proc))
      if (symbol.Type is StringType or FlexType && !ReferenceEquals(symbol, resultVar)) {
        asm.Mov(Reg.AX, Mem.Word(Reg.BP, symbol.Offset));
        asm.Call(this._rt.StrFree);
      }
    foreach (var parameter in proc.Parameters)
      if (parameter is { ByVal: true, Type: StringType or FlexType }) {
        asm.Mov(Reg.AX, Mem.Word(Reg.BP, parameter.Offset));
        asm.Call(this._rt.StrFree);
      }

    if (resultVar != null)
      this.EmitLoadPlace(new(Mem.At(Reg.BP, resultVar.Offset), false), resultVar.Type, null!);
    else if (proc.IsFunction)
      this.Errors.Add(new(proc.Position, $"FUNCTION {proc.Name} has no result variable"));

    asm.Mov(Reg.SP, Reg.BP);
    asm.Pop(Reg.BP);
    if (paramBytes > 0 && !CallerCleansStack(proc))   // CDECL: the caller cleans up; BASIC/STDCALL/PASCAL clean here
      asm.Ret((ushort)paramBytes);
    else
      asm.Ret();

    this.EndFrame();
    this._userLabels = outerLabels;
    this._currentProc = null;
    this._trackResume = false;
  }

  private void EmitCallStatement(CallStmt c) {
    if (!model.CallBindings.TryGetValue(c, out var proc)) {
      this.Unsupported(c);
      return;
    }
    if (this._tailSelfCalls?.Contains(c) == true && ReferenceEquals(proc, this._currentProc)
        && c.Arguments.Count == proc.Parameters.Count) {
      this.EmitTailSelfCall(proc, c.Arguments);
      return;
    }
    if (this._tailGeneralCalls != null && this._tailGeneralCalls.TryGetValue(c, out var tailTarget)
        && ReferenceEquals(tailTarget, proc) && c.Arguments.Count == proc.Parameters.Count) {
      this.EmitGeneralTailCall(proc, model.ReorderedArguments.GetValueOrDefault(c) ?? c.Arguments);
      return;
    }
    this.EmitCall(proc, model.ReorderedArguments.GetValueOrDefault(c) ?? c.Arguments, wantResult: false, c.Position);
  }

  /// <summary>
  /// pb36 O14: evaluates the new arguments left to right onto the stack (old
  /// parameter values stay readable during evaluation), pops them into the
  /// BYVAL parameter slots and jumps back to the frame entry - recursion in
  /// constant stack space.
  /// </summary>
  private void EmitTailSelfCall(ProcedureSymbol proc, IReadOnlyList<Expression> args) {
    var asm = this._asm;
    for (var i = 0; i < args.Count; ++i) {
      var parameter = proc.Parameters[i];
      this.EmitExpression(args[i]);
      this.Coerce(model.TypeOf(args[i]), parameter.Type, args[i]);
      if (parameter.Type.Size > 2)
        asm.Push(Reg.DX);
      asm.Push(Reg.AX);
    }
    for (var i = args.Count - 1; i >= 0; --i) {
      var parameter = proc.Parameters[i];
      asm.Pop(Reg.AX);
      asm.Mov(Mem.Word(Reg.BP, parameter.Offset), Reg.AX);
      if (parameter.Type.Size > 2) {
        asm.Pop(Reg.DX);
        asm.Mov(Mem.Word(Reg.BP, parameter.Offset + 2), Reg.DX);
      }
    }
    asm.Jmp(this._tailEntry!);
  }

  /// <summary>
  /// pb36 O14 general tail call: A's last action is <c>CALL B(args); RET</c>. Pushes
  /// B's arguments in their normal call order (so B reads them exactly as from an
  /// ordinary CALL), then tears down A's frame and slides the freshly-built call frame
  /// (return address + B's arguments) up so its top sits at A's caller's pre-call SP -
  /// the same boundary A's own <c>RET na</c> would have restored. Finally it jumps to
  /// B's entry. B's prologue treats the slid words as a normal call frame and B's
  /// <c>RET nb</c> pops B's nb argument bytes and returns straight to A's caller.
  ///
  /// Stack balance: A's caller pushed na argument bytes then CALLed A (return address
  /// at [BP+2]). For B to return there with the caller's stack as A's <c>RET na</c>
  /// would have left it, B's frame top (return address slot) must land at BP+2+(na-nb):
  /// then B's prologue takes Bbp = BP+(na-nb), and B's <c>RET nb</c> ends at
  /// Bbp+4+nb = BP+4+na = the caller's pre-call SP. na may differ from nb; this code
  /// pushes B's nb bytes and discards A's na bytes implicitly via that offset, so the
  /// net cleanup the caller observes is exactly na - identical to a real CALL A.
  /// </summary>
  private void EmitGeneralTailCall(ProcedureSymbol proc, IReadOnlyList<Expression> args) {
    var asm = this._asm;

    // build B's call frame image on the stack: arguments in B's normal push order, so
    // [SP+2..] after the return-address word matches B's LayoutFrame exactly.
    var nb = 0;
    foreach (var i in PushesRightToLeft(proc) ? Enumerable.Range(0, args.Count).Reverse() : Enumerable.Range(0, args.Count)) {
      nb += ParamSlotSize(proc.Parameters[i]);
      var unusedTemp = 0;
      this.EmitArgumentPush(proc, args, i, ref unusedTemp, []);   // small BYVAL scalars only - no byref temps
    }

    // push A's caller return address as B's return-address word (it sits just below
    // the arguments, mirroring the word a real CALL would push). The block to relocate
    // is now [SP .. SP + nb + 1] = return address (offset 0) + nb argument bytes.
    var na = this._currentParamBytes;
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, 2)); // A's caller return address
    asm.Push(Reg.AX);
    asm.Mov(Reg.SI, Reg.SP);             // SI = source: top (return-address word) of the built B frame

    // slide the whole block (2 + nb bytes) up to its destination, whose return-address
    // slot is [BP + 2 + (na - nb)]. Destination is always at a higher address than the
    // source (the source is below A's torn-down locals), so copy the HIGHEST word first
    // to stay correct under the overlapping upward move.
    var dest = 2 + (na - nb);            // BP-relative byte offset of B's return-address slot
    for (var off = nb; off >= 0; off -= 2) {
      asm.Mov(Reg.AX, Mem.Word(Reg.SI, off));
      asm.Mov(Mem.Word(Reg.BP, dest + off), Reg.AX);
    }

    // SP -> B's return-address slot, then jump: B's "push BP" makes Bbp = BP+(na-nb).
    asm.Lea(Reg.SP, Mem.Word(Reg.BP, dest));
    asm.Jmp(this.ProcLabelOf(proc));
  }

  /// <summary>Statements whose CallStmt to <paramref name="proc"/> sits in tail position (last in the body or last in arms of trailing IF/SELECT chains).</summary>
  private static HashSet<Statement> CollectTailSelfCalls(IReadOnlyList<Statement> body, ProcedureSymbol proc, SemanticModel model) {
    var tails = new HashSet<Statement>(ReferenceEqualityComparer.Instance);
    Visit(body);
    return tails;

    void Visit(IReadOnlyList<Statement> block) {
      if (block.Count == 0)
        return;
      var last = block[^1];
      switch (last) {
        case CallStmt c when model.CallBindings.TryGetValue(c, out var target) && ReferenceEquals(target, proc):
          tails.Add(c);
          break;
        case IfStmt i:
          Visit(i.Then);
          foreach (var (_, armBody) in i.ElseIfs)
            Visit(armBody);
          if (i.Else != null)
            Visit(i.Else);
          break;
        case SelectStmt s:
          foreach (var arm in s.Arms)
            Visit(arm.Body);
          break;
      }
    }
  }

  /// <summary>
  /// pb36 O14: tail-position <c>CALL B</c> statements whose target B is a different
  /// in-module procedure eligible to be jumped to (a known local label, a stack /
  /// callee-cleans convention, and only small BYVAL-scalar parameters - so B's call
  /// frame is laid out by plain word pushes and B's own <c>RET n</c> balances the
  /// stack). Self-calls are handled by the in-place rewrite and excluded here.
  /// </summary>
  private static Dictionary<Statement, ProcedureSymbol> CollectGeneralTailCalls(IReadOnlyList<Statement> body, ProcedureSymbol self, SemanticModel model) {
    var tails = new Dictionary<Statement, ProcedureSymbol>(ReferenceEqualityComparer.Instance);
    Visit(body);
    return tails;

    void Visit(IReadOnlyList<Statement> block) {
      if (block.Count == 0)
        return;
      var last = block[^1];
      switch (last) {
        case CallStmt c
            when model.CallBindings.TryGetValue(c, out var target)
              && !ReferenceEquals(target, self)
              && IsGeneralTailCallTarget(target)
              && c.Arguments.Count == target.Parameters.Count:
          tails.Add(c, target);
          break;
        case IfStmt i:
          Visit(i.Then);
          foreach (var (_, armBody) in i.ElseIfs)
            Visit(armBody);
          if (i.Else != null)
            Visit(i.Else);
          break;
        case SelectStmt s:
          foreach (var arm in s.Arms)
            Visit(arm.Body);
          break;
      }
    }
  }

  /// <summary>
  /// True when procedure B may be the target of a general tail-call jump: an
  /// in-module definition (a local <c>p_B</c> label exists), a stack / callee-cleans
  /// convention (not CDECL, not a register convention - so B cleans its own arguments
  /// with <c>RET n</c> and they are pushed on the stack), no capturing-lambda env, and
  /// every parameter a small BYVAL scalar pushed as plain words.
  /// </summary>
  private static bool IsGeneralTailCallTarget(ProcedureSymbol proc)
    => !proc.IsExternal && proc.Body != null
       && !proc.IsFunction   // a discarded FUNCTION result needs its StrFree / FPU-pop cleanup, which the jump skips
       && !proc.IsCdecl
       && !IsRegisterConvention(proc)
       && proc.ClosureEnvPtr == null
       && proc.Parameters.All(p => p.ByVal && p.Type is ScalarType { IsFloat: false, ByteSize: <= 4 });

  private static bool ContainsGosub(IEnumerable<Statement> statements) {
    foreach (var statement in statements) {
      if (statement is GosubStmt or GosubPtrStmt or OnGotoStmt { IsGosub: true })
        return true;
      if (ChildStatementBlocks(statement).Any(ContainsGosub))
        return true;
    }
    return false;
  }

  /// <summary>
  /// Emits a SUB/FUNCTION invocation: arguments pushed left to right (BYREF =
  /// near pointer, BYVAL = value; BYVAL strings transfer temp ownership to the
  /// callee), RET n cleans up. Results: AX / DX:AX / ST0 / string handle in AX.
  /// </summary>
  /// <summary>
  /// pb36 O6: a small leaf SUB/FUNCTION inlines as its body at the call site -
  /// the frame, CALL and RET overhead disappear and (when every call inlines) the
  /// procedure itself is reachability-purged from the image. BYVAL scalar
  /// arguments evaluate once (caller effects and order preserved) into fresh
  /// per-inline frame temps; the body's statements emit with every read and write
  /// of a parameter, local or the result variable remapped onto those temps (so
  /// two inlinings - or a recursive shape - never collide), and a FUNCTION's
  /// result is the value left in the result temp. The trivial single-result-
  /// assignment FUNCTION (which needs no extra local temps) is the fast path.
  /// </summary>
  /// <summary>The structural inlining analysis of an eligible leaf proc, independent of any call site.</summary>
  private readonly record struct InlinableLeaf(
    IReadOnlyList<Statement> Body,
    VariableSymbol? ResultSymbol,
    List<VariableSymbol> Locals,
    Expression Site,
    AssignStmt? LastResultWrite,
    int ResultWrites);

  /// <summary>
  /// Decides whether <paramref name="proc"/> is a small leaf SUB/FUNCTION whose body
  /// can be substituted at a BASIC-convention call site, returning the body analysis
  /// (result variable, body locals to give fresh temps, the trivial-result shape).
  /// Call-site-independent: it gates only on proc properties, so a pre-pass can use it
  /// to find procedures that inline at every site (then reachability purges them).
  /// </summary>
  private InlinableLeaf? AnalyzeInlinableLeaf(ProcedureSymbol proc) {
    if (proc.Body is not { } body)
      return null;
    // BASIC convention only; no STATIC, no error handling, no closures - and a leaf body
    // of only simple scalar assignments (no calls/loops/labels/GOTO/GOSUB/RETURN/EXIT/
    // SELECT/nested procs). Anything uncertain falls back to a real call.
    if (proc.CallConv != CallConvention.Basic || proc.IsStatic
        || proc.ClosureEnvPtr != null || proc.Captures.Count > 0
        || ContainsErrorHandling(body))
      return null;
    if (proc.IsFunction && proc.ReturnType is not ScalarType)
      return null; // FIX/BCD are BcdType, strings/UDTs excluded with them
    foreach (var parameter in proc.Parameters)
      if (!parameter.ByVal || parameter.Type is not ScalarType)
        return null;

    // the implicit result variable (FUNCTION only); reads/writes of it map to a temp
    var resultSymbol = proc.IsFunction && proc.Variables.TryGetValue(proc.Name, out var rv) ? rv : null;
    if (proc.IsFunction && resultSymbol == null)
      return null;

    // every body statement must be a scalar assignment whose target is a parameter,
    // a stack local or the result, and whose value reads only those plus constants
    const int maxStatements = 8;
    if (body.Count is 0 or > maxStatements)
      return null;
    var locals = new List<VariableSymbol>();
    Expression? site = null;   // any body expression, for load/coerce diagnostics
    AssignStmt? lastResultWrite = null;
    var resultWrites = 0;
    foreach (var statement in body) {
      if (statement is MetaStmt or EquateStmt or DefTypeStmt)
        continue; // inert - no code
      // a plain scalar LOCAL declaration carries no code (the binder splices any
      // DIM-initializer out as a separate assignment); register its locals so they
      // get fresh zeroed temps and reads before the first write resolve to them
      if (statement is DimStmt dim) {
        if (dim.Storage != StorageClass.Local || dim.SharedFlag || dim.StaticFlag
            || dim.AtAddress != null || dim.Class != ArrayClass.Default)
          return null;
        foreach (var decl in dim.Variables) {
          if (decl.ArrayBounds != null)
            return null;
          var local = proc.Variables.GetValueOrDefault(KeyOf(decl.Name, decl.Suffix));
          if (local is not { Storage: VariableStorage.Local, Type: ScalarType } || local.IsArray)
            return null;
          if (!locals.Contains(local))
            locals.Add(local);
        }
        continue;
      }
      if (statement is not AssignStmt { Target: NameExpr targetName } assign)
        return null;
      if (!model.VariableBindings.TryGetValue(targetName, out var targetSymbol)
          || !this.InlinableTarget(targetSymbol, proc, resultSymbol, locals)
          || !this.InlinableExpression(assign.Value, proc, resultSymbol, locals))
        return null;
      if (model.TypeOf(assign.Target) is not ScalarType || model.TypeOf(assign.Value) is not ScalarType)
        return null;
      site ??= assign.Target;
      if (ReferenceEquals(targetSymbol, resultSymbol)) {
        lastResultWrite = assign;
        ++resultWrites;
      }
    }
    if (site == null)
      return null; // nothing but inert statements - no behaviour to inline

    return new InlinableLeaf(body, resultSymbol, locals, site, lastResultWrite, resultWrites);
  }

  private bool TryEmitInlinedFunction(ProcedureSymbol proc, IReadOnlyList<Expression> args, bool wantResult) {
    if (!this.Optimize)
      return false;
    // do not inline into an error-handling region: a fault inside the inlined body
    // would re-enter through the wrong RESUME / RESUME NEXT latch (each inlined
    // statement has its own, a real call has one). The purge pre-pass keeps the
    // procedure whenever the program has any error handling, so this fallback to a
    // real call can never strand a reference (the body is still emitted).
    if (this._trackResume)
      return false;
    if (args.Count != proc.Parameters.Count)
      return false;
    // a FUNCTION's result must be consumed (it is the value the inline leaves); a SUB
    // leaves no value, so it inlines whether or not the (absent) result is wanted
    if (proc.IsFunction && !wantResult)
      return false;
    if (this.AnalyzeInlinableLeaf(proc) is not { } leaf)
      return false;
    var (body, resultSymbol, locals, site, lastResultWrite, resultWrites) = leaf;

    var asm = this._asm;
    var outer = this._inlineParamSlots;
    var slots = new Dictionary<VariableSymbol, (Mem Cell, PbType Type)>(ReferenceEqualityComparer.Instance);
    var reserved = 0;

    Mem ReserveSlot(PbType type) {
      var bytes = Math.Max(2, (type.Size + 1) & ~1);
      reserved += bytes;
      return this.AllocTemp(bytes);
    }

    // bind each argument once into the parameter's fresh temp slot
    for (var i = 0; i < args.Count; ++i) {
      var parameter = proc.Parameters[i];
      this.EmitExpression(args[i]);
      this.Coerce(model.TypeOf(args[i]), parameter.Type, args[i]);
      var cell = ReserveSlot(parameter.Type);
      switch (KindOf(parameter.Type)) {
        case ValueKind.Int16:
          asm.Mov(cell, Reg.AX);
          break;
        case ValueKind.Int32:
          asm.Mov(cell, Reg.AX);
          asm.Mov(Adjust(cell, 2, OperandSize.Word), Reg.DX);
          break;
        default: // float parameters park x87-exact at their declared width
          asm.Fstp(Adjust(cell, 0, parameter.Type.Size == 4 ? OperandSize.Dword : OperandSize.Qword));
          break;
      }
      slots[parameter] = (cell, parameter.Type);
    }

    // fast path: a FUNCTION whose only effect is one assignment of an expression to
    // the result (no locals, the result not read inside it) emits that expression
    // straight into the evaluation registers - no result temp, no store/reload
    if (resultSymbol != null && locals.Count == 0 && resultWrites == 1
        && body.Count(s => s is not (MetaStmt or EquateStmt or DefTypeStmt)) == 1
        && !ReferencesVar(lastResultWrite!.Value, resultSymbol, model)) {
      this._inlineParamSlots = slots;
      this.EmitExpression(lastResultWrite.Value);
      this.Coerce(model.TypeOf(lastResultWrite.Value), proc.ReturnType!, lastResultWrite.Value);
      this._inlineParamSlots = outer;
      this.ReleaseTemp(reserved);
      return true;
    }

    // the result and every body local get their own zero-initialised temp - PB
    // locals read 0 before assignment, so a body that reads a local before writing
    // it (or a FUNCTION whose result was never assigned) sees 0, exactly as a real call
    foreach (var local in locals) {
      var cell = ReserveSlot(local.Type);
      this.ZeroSlot(cell, local.Type);
      slots[local] = (cell, local.Type);
    }
    if (resultSymbol != null) {
      var cell = ReserveSlot(resultSymbol.Type);
      this.ZeroSlot(cell, resultSymbol.Type);
      slots[resultSymbol] = (cell, resultSymbol.Type);
    }

    this._inlineParamSlots = slots;
    foreach (var statement in body)
      this.EmitStatement(statement);
    // the FUNCTION result is the value left in the result temp (the result variable's
    // declared type IS the return type, so no coercion is needed - it loads directly)
    if (resultSymbol != null)
      this.EmitLoadPlace(new(slots[resultSymbol].Cell, Far: false), proc.ReturnType!, site);
    this._inlineParamSlots = outer;
    this.ReleaseTemp(reserved);
    return true;
  }

  /// <summary>Zeroes a fresh inline-frame slot (numeric scalar) so an unassigned-before-read local matches a real call's zeroed frame.</summary>
  private void ZeroSlot(Mem cell, PbType type) {
    var asm = this._asm;
    switch (KindOf(type)) {
      case ValueKind.Int16:
        asm.Mov(cell.WithSize(OperandSize.Word), (Imm)0);
        break;
      case ValueKind.Int32:
        asm.Mov(cell.WithSize(OperandSize.Word), (Imm)0);
        asm.Mov(Adjust(cell, 2, OperandSize.Word), (Imm)0);
        break;
      default: // float / QUAD: write the whole width as zero words
        for (var w = 0; w < Math.Max(2, (type.Size + 1) & ~1); w += 2)
          asm.Mov(Adjust(cell, w, OperandSize.Word), (Imm)0);
        break;
    }
  }

  /// <summary>True when an assignment target is one of the procedure's own remappable scalar cells (parameter, the result, or a stack local that joins <paramref name="locals"/> on first sight).</summary>
  private bool InlinableTarget(VariableSymbol s, ProcedureSymbol proc, VariableSymbol? resultSymbol, List<VariableSymbol> locals) {
    if (proc.Parameters.Contains(s) || ReferenceEquals(s, resultSymbol))
      return true;
    if (s.Storage != VariableStorage.Local || s.Type is not ScalarType || s.IsArray)
      return false;
    if (!locals.Contains(s))
      locals.Add(s);
    return true;
  }

  /// <summary>True when the expression reads only the procedure's own parameters, locals, result, literals and equates through scalar operators - so it can emit against the per-inline temps.</summary>
  private bool InlinableExpression(Expression e, ProcedureSymbol proc, VariableSymbol? resultSymbol, List<VariableSymbol> locals) => e switch {
    IntegerLiteralExpr or FloatLiteralExpr or NamedConstantExpr => true,
    NameExpr n when model.IntrinsicBindings.ContainsKey(n) || model.CallBindings.ContainsKey(n) => false,
    NameExpr n => model.VariableBindings.TryGetValue(n, out var s)
      && (proc.Parameters.Contains(s) || ReferenceEquals(s, resultSymbol) || locals.Contains(s)),
    UnaryExpr u => this.InlinableExpression(u.Operand, proc, resultSymbol, locals),
    BinaryExpr b => this.InlinableExpression(b.Left, proc, resultSymbol, locals) && this.InlinableExpression(b.Right, proc, resultSymbol, locals),
    _ => false,
  };

  private void EmitCall(ProcedureSymbol proc, IReadOnlyList<Expression> args, bool wantResult, SourcePosition position) {
    var asm = this._asm;

    // PB 3.6 default parameter values: fill omitted trailing arguments with each
    // parameter's default expression (evaluated here, at the call site) before any
    // path that assumes full arity (inlining, tail call, the count check).
    if (args.Count < proc.Parameters.Count && !proc.IsCdecl
        && proc.Parameters[args.Count].DefaultValue != null) {
      var filled = new List<Expression>(args);
      for (var i = args.Count; i < proc.Parameters.Count && proc.Parameters[i].DefaultValue is { } d; ++i)
        filled.Add(d);
      args = filled;
    }

    if (this.TryEmitInlinedFunction(proc, args, wantResult))
      return;
    if (proc.IsExternal && !this._allowExternalCalls) {
      this.Unsupported(position, $"external procedure {proc.Name} (no $LINK provides it)");
      return;
    }
    var cdeclVariadic = proc.IsCdecl && args.Count >= proc.RequiredParameters && args.Count <= proc.Parameters.Count;
    if (args.Count != proc.Parameters.Count && !cdeclVariadic) {
      this.Unsupported(position, $"argument count for {proc.Name}");
      return;
    }

    var tempBytesUsed = 0;
    var stringTemps = new List<Mem>();
    var pushedBytes = 0;

    if (IsRegisterConvention(proc)) {
      // WATCALL/FASTCALL: stack overflow first (this convention's stack order), then the
      // leading args pushed and popped into AX,DX,BX(,CX) so they survive arg evaluation.
      var regs = ConventionRegisters(proc.CallConv);
      var regCount = RegisterParamCount(proc);
      var overflow = Enumerable.Range(regCount, args.Count - regCount).ToList();
      foreach (var i in PushesRightToLeft(proc) ? Enumerable.Reverse(overflow) : overflow) {
        pushedBytes += ParamSlotSize(proc.Parameters[i]);   // callee (RET n) cleans these
        this.EmitArgumentPush(proc, args, i, ref tempBytesUsed, stringTemps);
      }
      for (var i = 0; i < regCount; ++i)
        this.EmitArgumentPush(proc, args, i, ref tempBytesUsed, stringTemps);
      for (var i = regCount - 1; i >= 0; --i)
        asm.Pop(regs[i]);
    } else {
      // CDECL/STDCALL push right to left; BASIC/PASCAL push left to right
      foreach (var i in PushesRightToLeft(proc) ? Enumerable.Range(0, args.Count).Reverse() : Enumerable.Range(0, args.Count)) {
        pushedBytes += ParamSlotSize(proc.Parameters[i]);
        this.EmitArgumentPush(proc, args, i, ref tempBytesUsed, stringTemps);
      }
    }

    asm.Call(this.ProcLabelOf(proc));
    if (CallerCleansStack(proc) && pushedBytes > 0)   // CDECL only; others' callee RET n cleans
      asm.Add(Reg.SP, pushedBytes);

    var resultKind = proc is { IsFunction: true, ReturnType: { } rt } ? KindOf(rt) : (ValueKind?)null;
    if (stringTemps.Count > 0) {
      // protect the result registers while releasing byref string temps
      if (resultKind is ValueKind.Int16 or ValueKind.Str)
        asm.Push(Reg.AX);
      else if (resultKind == ValueKind.Int32) {
        asm.Push(Reg.DX);
        asm.Push(Reg.AX);
      }
      foreach (var cell in stringTemps) {
        asm.Mov(Reg.AX, cell.WithSize(OperandSize.Word));
        asm.Call(this._rt.StrFree);
      }
      if (resultKind is ValueKind.Int16 or ValueKind.Str)
        asm.Pop(Reg.AX);
      else if (resultKind == ValueKind.Int32) {
        asm.Pop(Reg.AX);
        asm.Pop(Reg.DX);
      }
    }
    this.ReleaseTemp(tempBytesUsed);

    if (wantResult || resultKind == null)
      return;

    // discarded FUNCTION result
    switch (resultKind) {
      case ValueKind.Str:
        asm.Call(this._rt.StrFree);
        break;
      case ValueKind.Float:
        asm.Fstp(St.St0);
        break;
    }
  }

  /// <summary>
  /// Evaluates argument <paramref name="i"/> of <paramref name="proc"/> and pushes its
  /// stack slot: a BYVAL value, a BYREF/ANY near pointer, an array descriptor, or a
  /// hidden copy-in temp's address. Shared by the stack and register call paths (the
  /// register path pops the leading pushes back into AX,DX,BX(,CX)).
  /// </summary>
  private void EmitArgumentPush(ProcedureSymbol proc, IReadOnlyList<Expression> args, int i, ref int tempBytesUsed, List<Mem> stringTemps) {
    var asm = this._asm;
    var parameter = proc.Parameters[i];
    var arg = args[i];
    var argType = model.TypeOf(arg);

    // BYVAL override (PB 3.2): the value itself is passed - against a BYREF
    // parameter the low word acts as the near address of the target
    if (arg is ByValArgExpr byValOverride) {
      var innerType = model.TypeOf(byValOverride.Value);
      if (parameter.ByVal)
        this.EmitByValArgument(byValOverride.Value, innerType, parameter.Type);
      else {
        this.EmitExpression(byValOverride.Value);
        asm.Push(Reg.AX); // offset word of the pointer/value
      }
      return;
    }

    if (parameter.Type is ArrayType || argType is ArrayType) {
      this.EmitArrayArgument(arg, proc);
      return;
    }

    if (parameter.Type is AnyType) {
      // BYREF ANY: address of whatever storage the argument names - no checks
      if (this.EmitPlace(arg) is { } anyPlace) {
        asm.Lea(Reg.BX, anyPlace.Cell);
        asm.Push(Reg.BX);
      } else
        this.Unsupported(arg, $"ANY argument to {proc.Name}");
      return;
    }

    if (parameter.ByVal) {
      this.EmitByValArgument(arg, argType, parameter.Type);
      return;
    }

    // BYREF: pass the address when the argument is a matching near lvalue,
    // otherwise copy into a hidden stack temp (copy-in only)
    if (Equals(argType, parameter.Type) && this.IsNearLValue(arg) && this.EmitPlace(arg) is { } place) {
      asm.Lea(Reg.BX, place.Cell);
      asm.Push(Reg.BX);
      return;
    }

    var slotBytes = Math.Max(2, (parameter.Type.Size + 1) & ~1);
    var temp = this.AllocTemp(slotBytes);
    tempBytesUsed += slotBytes;
    this.EmitExpression(arg);
    this.Coerce(argType, parameter.Type, arg);
    this.EmitStoreTempArgument(temp, parameter.Type, arg, stringTemps);
    asm.Lea(Reg.BX, temp);
    asm.Push(Reg.BX);
  }

  /// <summary>
  /// PB 3.6 typed procedure-pointer / closure call <c>f(args)</c>. The fat closure
  /// (far code pointer + far environment pointer) is stashed across argument
  /// evaluation; arguments are pushed BYVAL, each coerced to the pointer's declared
  /// parameter type (delegates pass by value, which also fixes the width mismatch an
  /// untyped CALL DWORD suffers); the environment is loaded into BX:CX (a capturing
  /// lambda reads it at entry, a non-capturing target ignores it) and the code half
  /// is far-called. The lifted target's RET n cleans the arguments, and a FUNCTION
  /// result is already in the evaluation registers on return.
  /// </summary>
  private void EmitProcPtrCall(CallOrIndexExpr call, ProcPtrType signature) {
    var asm = this._asm;

    if (!model.VariableBindings.TryGetValue(call, out var symbol)) {
      this.Unsupported(call, $"procedure pointer {call.Name}");
      return;
    }
    // copy the 8-byte closure into a temp first - argument evaluation clobbers registers
    var cell = this.TryDirectCell(symbol) is { } direct
      ? direct
      : this.LoadByRefPointer(symbol);                 // BYREF parameter: [BP+off] -> [BX]
    var closure = this.AllocTemp(8);
    for (var w = 0; w < 8; w += 2) {
      asm.Mov(Reg.AX, Adjust(cell, w, OperandSize.Word));
      asm.Mov(Adjust(closure, w, OperandSize.Word), Reg.AX);
    }

    var argc = Math.Min(call.Arguments.Count, signature.ParameterTypes.Count);
    for (var i = 0; i < argc; ++i)
      this.EmitByValArgument(call.Arguments[i], model.TypeOf(call.Arguments[i]), signature.ParameterTypes[i]);

    asm.Mov(Reg.BX, Adjust(closure, 4, OperandSize.Word));   // env offset -> BX
    asm.Mov(Reg.CX, Adjust(closure, 6, OperandSize.Word));   // env segment -> CX
    asm.CallFar(closure.WithSize(OperandSize.Dword));        // far-call the code half (low dword)
    this.ReleaseTemp(8);
  }

  /// <summary>
  /// PB 3.6 closure environment for a lambda value (BX:CX). A non-capturing lambda
  /// has a null env. A non-escaping capturing lambda uses the stage-1 STACK env: the
  /// enclosing frame itself (captured locals read by reference at their frame
  /// offsets). An ESCAPING capturing lambda allocates a HEAP env record and snapshots
  /// the captured locals' VALUES into it at creation - so the closure survives the
  /// dead frame, reading the by-value snapshot through the same env far pointer.
  /// </summary>
  private void EmitClosureEnv(ProcedureSymbol lambda) {
    var asm = this._asm;
    if (lambda.ClosureEnvPtr == null) {
      asm.Xor(Reg.BX, Reg.BX);
      asm.Xor(Reg.CX, Reg.CX);
      return;
    }
    if (lambda.IsEscapingClosure) {
      this.EmitHeapClosureEnv(lambda);
      return;
    }
    asm.Mov(Reg.BX, Reg.BP);   // env = enclosing frame offset (this BP)
    asm.Mov(Reg.CX, Reg.SS);   // env segment (stack)
  }

  /// <summary>
  /// PB 3.6 escaping closure: allocate a heap env block (far array heap), copy each
  /// captured local's current VALUE from the enclosing frame into its slot, and
  /// return the block as the env far pointer (offset in BX, segment in CX). Captured
  /// by value at creation - mutations after the closure escapes are NOT shared
  /// (documented in docs/PB36.md). The block is never freed (no GC).
  /// </summary>
  private void EmitHeapClosureEnv(ProcedureSymbol lambda) {
    var asm = this._asm;
    asm.Xor(Reg.DX, Reg.DX);
    asm.Mov(Reg.AX, lambda.ClosureEnvSize);
    asm.Call(this._rt.ArrAlloc);              // AX = block offset within rt_arrseg (zeroed)
    var blockOff = this.AllocTemp(2);
    asm.Mov(blockOff.WithSize(OperandSize.Word), Reg.AX);

    // ES = heap segment for the stores; the captured locals live at [BP+off]
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_arrseg")));
    asm.Mov(Reg.DI, Reg.AX);                  // DI = running slot address in the block
    var slot = 0;
    foreach (var captured in lambda.Captures) {
      var bytes = Math.Max(2, (captured.Type.Size + 1) & ~1);
      for (var w = 0; w < bytes; w += 2) {
        asm.Mov(Reg.AX, Mem.Word(Reg.BP, captured.Offset + w));
        asm.Mov(Mem.Word(Reg.DI, slot + w).Seg(Reg.ES), Reg.AX);
      }
      slot += bytes;
    }

    asm.Mov(Reg.BX, blockOff.WithSize(OperandSize.Word));   // env offset -> BX
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_arrseg")));        // env segment -> CX
    this.ReleaseTemp(2);
  }

  /// <summary>Loads a BYREF parameter's near pointer into BX and yields a <c>[BX]</c> cell (mirrors EmitPlace's NameExpr fallback).</summary>
  private Mem LoadByRefPointer(VariableSymbol symbol) {
    this._asm.Mov(Reg.BX, Mem.Word(Reg.BP, symbol.Offset));
    return Mem.At(Reg.BX);
  }

  /// <summary>
  /// Array argument: push the address of a dynamic-array descriptor. Static
  /// arrays get a shadow descriptor in the data area, (re)filled at the call
  /// site so the callee can index uniformly.
  /// </summary>
  private void EmitArrayArgument(Expression arg, ProcedureSymbol proc) {
    if (!model.VariableBindings.TryGetValue(arg, out var symbol) || symbol.Type is not ArrayType arrayType) {
      this.Unsupported(arg, $"array argument to {proc.Name}");
      return;
    }
    this.EmitArrayDescriptorPush(arg, symbol, arrayType);
  }

  private readonly Dictionary<VariableSymbol, Label> _shadowDescriptors = new(ReferenceEqualityComparer.Instance);

  private Label ShadowDescriptorOf(VariableSymbol symbol, ArrayType arrayType) {
    if (!this._shadowDescriptors.TryGetValue(symbol, out var label))
      this._shadowDescriptors[symbol] = label = this._asm.DefineLabel($"ad_{symbol.Name}_{this._shadowDescriptors.Count}");
    _ = arrayType;
    return label;
  }

  private void EmitByValArgument(Expression arg, PbType argType, PbType parameterType) {
    var asm = this._asm;
    this.EmitExpression(arg);
    if (parameterType is ProcPtrType) {     // fat closure: 8 bytes (code AX:DX + env BX:CX), pushed high word first
      asm.Push(Reg.CX);
      asm.Push(Reg.BX);
      asm.Push(Reg.DX);
      asm.Push(Reg.AX);
      return;
    }
    this.Coerce(argType, parameterType, arg);
    switch (KindOf(parameterType)) {
      case ValueKind.Int16 or ValueKind.Str:
        asm.Push(Reg.AX);
        break;
      case ValueKind.Int32:
        asm.Push(Reg.DX);
        asm.Push(Reg.AX);
        break;
      case ValueKind.Float: {
        var size = parameterType.Size;
        if (parameterType is BcdType { IsFixedPoint: true }) {        // FIX: scaled int64 cell
          asm.Call(asm.Lbl("rt_fixup"));
          asm.Fistp(Mem.Qword(this._scratch));
        } else
          switch (size) {
            case 4: asm.Fstp(Mem.Dword(this._scratch)); break;
            case 8: asm.Fstp(Mem.Qword(this._scratch)); break;
            default: asm.Fstp(Mem.Tbyte(this._scratch)); break;
          }
        for (var offset = ((size + 1) & ~1) - 2; offset >= 0; offset -= 2)
          asm.Push(Mem.Word(this._scratch, offset));
        break;
      }
    }
  }

  private void EmitStoreTempArgument(Mem temp, PbType type, Expression at, List<Mem> stringTemps) {
    var asm = this._asm;
    switch (KindOf(type)) {
      case ValueKind.Int16:
        asm.Mov(temp.WithSize(OperandSize.Word), Reg.AX);
        break;
      case ValueKind.Int32:
        asm.Mov(temp.WithSize(OperandSize.Word), Reg.AX);
        asm.Mov(Adjust(temp, 2, OperandSize.Word), Reg.DX);
        break;
      case ValueKind.Float:
        if (type is BcdType { IsFixedPoint: true }) {                  // FIX: scaled int64 cell
          asm.Call(asm.Lbl("rt_fixup"));
          asm.Fistp(temp.WithSize(OperandSize.Qword));
          break;
        }
        switch (type.Size) {
          case 4: asm.Fstp(temp.WithSize(OperandSize.Dword)); break;
          case 8: asm.Fstp(temp.WithSize(OperandSize.Qword)); break;
          default: asm.Fstp(temp.WithSize(OperandSize.Tbyte)); break;
        }
        break;
      case ValueKind.Str when type is StringType or FlexType:
        asm.Mov(temp.WithSize(OperandSize.Word), Reg.AX);
        stringTemps.Add(temp);
        break;
      default:
        this.Unsupported(at, $"byref temp of {type}");
        break;
    }
  }
}
