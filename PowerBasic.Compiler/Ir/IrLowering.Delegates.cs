using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// pb36 delegates: a procedure pointer that carries an environment with it.
///
/// <para>
/// The value is a FAT CLOSURE - eight bytes, four words, in the order the direct emitter lays them
/// out: the far code pointer (offset, then segment), then the far environment pointer (offset, then
/// segment). The code half names an <see cref="IrFarEntry"/> rather than the procedure itself,
/// because the call through it is far and the procedure's own <c>RET n</c> is near; the thunk is what
/// reconciles the two. Both paths building the identical eight bytes is what lets a closure made in
/// one be called from the other.
/// </para>
/// <para>
/// A closure is never an SSA value. It is storage, always - the delegate variable's own slot, or a
/// temporary the call site copies one into before evaluating arguments - because the IR's type
/// lattice is scalar and a four-word aggregate has no place in it. That is not a workaround: the one
/// operation performed on the whole thing is a far call through a cell, which needs an address
/// anyway.
/// </para>
/// </summary>
public sealed partial class IrLowering {

  /// <summary>Words in a fat closure: code offset, code segment, environment offset, environment segment.</summary>
  private const int ClosureWords = 4;

  /// <summary>Bytes in a fat closure - what a whole-closure copy moves and what a BYVAL one pushes.</summary>
  private const int ClosureBytes = ClosureWords * 2;

  /// <summary>Word index of the environment's offset within a closure (its segment follows).</summary>
  private const int ClosureEnvWord = 2;

  /// <summary>A fresh eight-byte closure object in the frame.</summary>
  private IrValue ClosureStorage(string name)
    => this._entry.InsertAt(this._entryAllocaCount++,
      new IrAlloca(IrType.I16) { Count = ClosureWords, Name = name });

  private IrValue ClosureWord(IrValue closure, int index)
    => this._b.Gep(closure, new IrConstantInt(IrType.I16, index), IrType.I16);

  private void CopyClosure(IrValue destination, IrValue source)
    => this._b.Call(IrType.Void,
      this.RuntimeFn("llvm.memcpy.p0.p0.i32", IrType.Void, IrType.Ptr, IrType.Ptr, IrType.I32, IrType.I1),
      destination, source, new IrConstantInt(IrType.I32, ClosureBytes), IrBuilder.ConstBool(false));

  /// <summary>The segment every code address in this program shares.</summary>
  private IrValue CodeSegment() => this._b.Call(IrType.I16, this.RuntimeFn("rt_codeseg", IrType.I16));

  /// <summary>
  /// The storage a delegate-valued expression already occupies, or null when it has none and has to
  /// be built. A delegate variable, a delegate parameter and a delegate field are all just cells.
  /// </summary>
  private IrValue? ExistingClosureStorage(Expression expr) {
    if (this._model.TypeOf(expr) is not ProcPtrType)
      return null;
    if (expr is NameExpr && this._model.VariableBindings.TryGetValue(expr, out var symbol)
        && symbol.Type is ProcPtrType)
      return this.SlotFor(symbol);
    // A FUNCTION whose result is a delegate hands back the ADDRESS of the closure it built - that is
    // what IrFunction.ReturnsClosure means - so the call's own value is already the storage. This is
    // what COMPOSE and BIND are: each returns the thunk it synthesized, as a closure.
    if (expr is CallOrIndexExpr or NameExpr
        && this._model.CallBindings.TryGetValue(expr, out var producer)
        && producer is { IsFunction: true, ReturnType: ProcPtrType })
      return expr is CallOrIndexExpr call
        ? this.LowerCallExpr(call)
        : this.LowerNameRead((NameExpr)expr);
    return null;
  }

  /// <summary>
  /// Writes the eight bytes of <paramref name="value"/>'s delegate value into
  /// <paramref name="closure"/>.
  /// </summary>
  private void StoreClosure(Expression value, IrValue closure) {
    // A bind-time rewrite is stored through its DESUGARED form, which is where the meaning is. BIND
    // and COMPOSE are the shapes that need it: each synthesizes a thunk FUNCTION and is bound as
    // CODEPTR32 of it, so by the time the value reaches here it is a bare code pointer and the
    // delegate type it wears says only what may be called through it.
    if (this._model.Desugared.TryGetValue(value, out var rewritten)) {
      this.StoreClosure(rewritten, closure);
      return;
    }
    // another delegate: copy it whole, environment and all, so a reassignment carries the captured
    // frame with it rather than pointing a new closure at nothing
    if (this.ExistingClosureStorage(value) is { } source) {
      this.CopyClosure(closure, source);
      return;
    }

    if (value is LambdaExpr lambda && this._model.LambdaProcs.TryGetValue(lambda, out var lifted)) {
      if (this._procMap is null || !this._procMap.TryGetValue(lifted, out var liftedFn))
        throw new IrLoweringException($"lambda {lifted.Name} has no lowered function");
      this._b.Store(this._b.Cast(IrCastOp.PtrToInt, new IrFarEntry(liftedFn), IrType.U16),
        this.ClosureWord(closure, 0));
      this._b.Store(this.CodeSegment(), this.ClosureWord(closure, 1));
      this.StoreClosureEnvironment(lifted, closure);
      return;
    }

    // ...and everything else is a bare 32-bit code pointer - what CODEPTR32 answers with, and what
    // the binder rewrites a named procedure into when one is assigned to a delegate. It carries no
    // environment, so the closure gets a null one: a non-capturing target never reads it, and a
    // capturing one could not have been named this way.
    var valueType = this._model.TypeOf(value);
    if (valueType is ScalarType { IsFloat: false, ByteSize: 4 }) {
      var pointer = this.Coerce(this.LowerExpr(value), valueType, PbType.Dword);
      this._b.Store(this._b.Trunc(pointer, IrType.I16), this.ClosureWord(closure, 0));
      this._b.Store(
        this._b.Trunc(this._b.Binary(IrBinaryOp.LShr, pointer, new IrConstantInt(IrType.U32, 16)), IrType.I16),
        this.ClosureWord(closure, 1));
      this.StoreNullEnvironment(closure);
      return;
    }
    throw new IrLoweringException($"delegate value of type {valueType}");
  }

  /// <summary>
  /// A lifted lambda as a bare 32-bit far code pointer - its entry thunk's offset in the low word and
  /// the code segment in the high one, the same value <c>CODEPTR32</c> of a named procedure gives.
  /// </summary>
  private IrValue LowerLambdaCodePointer(ProcedureSymbol lifted, PbType wanted) {
    if (lifted.Captures.Count > 0)
      throw new IrLoweringException("a capturing lambda as a bare code pointer (it has no environment here)");
    if (this._procMap is null || !this._procMap.TryGetValue(lifted, out var liftedFn))
      throw new IrLoweringException($"lambda {lifted.Name} has no lowered function");
    var entry = this._b.ZExt(this._b.Cast(IrCastOp.PtrToInt, new IrFarEntry(liftedFn), IrType.U16), IrType.U32);
    var segment = this._b.Shl(this._b.ZExt(this.CodeSegment(), IrType.U32), new IrConstantInt(IrType.U32, 16));
    return this.Coerce(this._b.Or(segment, entry), PbType.Dword, wanted);
  }

  private void StoreNullEnvironment(IrValue closure) {
    this._b.Store(new IrConstantInt(IrType.I16, 0), this.ClosureWord(closure, ClosureEnvWord));
    this._b.Store(new IrConstantInt(IrType.I16, 0), this.ClosureWord(closure, ClosureEnvWord + 1));
  }

  /// <summary>
  /// The closure a delegate call goes through: a private COPY of it, taken before any argument is
  /// evaluated.
  ///
  /// <para>
  /// The copy is not a convenience. An argument expression may assign to the very delegate being
  /// called - <c>f(Reset())</c> where <c>Reset</c> rebinds <c>f</c> - and PB calls the one that was
  /// named, not the one that is there afterwards. The direct emitter takes the same copy for a
  /// different-looking reason (argument evaluation clobbers the registers it staged the closure in),
  /// and the two arrive at the same semantics.
  /// </para>
  /// </summary>
  private IrValue CallableClosure(CallOrIndexExpr call) {
    if (!this._model.VariableBindings.TryGetValue(call, out var symbol) || symbol.Type is not ProcPtrType)
      throw new IrLoweringException($"procedure pointer {call.Name}");
    var copy = this.ClosureStorage(call.Name + ".called");
    this.CopyClosure(copy, this.SlotFor(symbol));
    return copy;
  }

  /// <summary>
  /// Lowers a call THROUGH a delegate. Arguments are passed BYVAL at the pointer's declared parameter
  /// types - that is what the signature is for, and it is also what makes a delegate call safe when
  /// the target's own parameter happens to be declared wider.
  /// </summary>
  private IrValue LowerClosureCall(CallOrIndexExpr call, ProcPtrType signature) {
    var closure = this.CallableClosure(call);
    var arguments = new List<IrValue> {
      this._b.Load(IrType.I16, this.ClosureWord(closure, ClosureEnvWord)),      // environment offset -> BX
      this._b.Load(IrType.I16, this.ClosureWord(closure, ClosureEnvWord + 1)),  // environment segment -> CX
    };
    var supplied = Math.Min(call.Arguments.Count, signature.ParameterTypes.Count);
    for (var i = 0; i < supplied; ++i) {
      var parameterType = signature.ParameterTypes[i];
      if (parameterType is not ScalarType { IsFloat: false } || parameterType.Size > 4)
        throw new IrLoweringException($"delegate parameter of type {parameterType}");
      arguments.Add(this.Coerce(this.LowerExpr(call.Arguments[i]),
        this._model.TypeOf(call.Arguments[i]), parameterType));
    }

    var resultType = signature.ReturnType is null ? IrType.Void : MapType(signature.ReturnType);
    return this._b.Call(resultType, closure, IrCallConvention.BasicClosure, arguments);
  }

  /// <summary>
  /// <c>CALL DWORD p BDECL(args)</c> - a far call through a bare 32-bit code pointer.
  ///
  /// <para>
  /// It is the same transfer a delegate call performs with the environment half left null, so it goes
  /// through the same closure: the pointer's two words as the code half, zeros behind them. Arguments
  /// are pushed BYREF as near pointers - a <c>CODEPTR32</c> target is an ordinary SUB, which is what
  /// it expects - unless written <c>BYVAL</c>, which pushes the value itself.
  /// </para>
  /// <para>
  /// The declared convention is not read, because the direct emitter does not read it either: it
  /// pushes left to right and cleans nothing, whatever the source says, and a routed call that
  /// disagreed would be the one caller in the program using a different stack discipline on the same
  /// target.
  /// </para>
  /// </summary>
  private void LowerCallPtr(CallPtrStmt cp) {
    var pointerType = this._model.TypeOf(cp.Pointer);
    var pointer = this.Coerce(this.LowerExpr(cp.Pointer), pointerType, PbType.Dword);
    var closure = this.ClosureStorage("callptr");
    this._b.Store(this._b.Trunc(pointer, IrType.I16), this.ClosureWord(closure, 0));
    this._b.Store(
      this._b.Trunc(this._b.Binary(IrBinaryOp.LShr, pointer, new IrConstantInt(IrType.U32, 16)), IrType.I16),
      this.ClosureWord(closure, 1));
    this.StoreNullEnvironment(closure);

    var arguments = new List<IrValue> { new IrConstantInt(IrType.I16, 0), new IrConstantInt(IrType.I16, 0) };
    foreach (var argument in cp.Arguments) {
      if (argument is ByValArgExpr byValue) {
        var valueType = this._model.TypeOf(byValue.Value);
        if (valueType is not ScalarType { IsFloat: false } || valueType.Size > 4)
          throw new IrLoweringException($"CALL DWORD BYVAL argument of type {valueType}");
        arguments.Add(this.Coerce(this.LowerExpr(byValue.Value), valueType, valueType));
        continue;
      }
      arguments.Add(this.AddressOfArgument(argument, this._model.TypeOf(argument)));
    }
    this._b.Call(IrType.Void, closure, IrCallConvention.BasicClosure, arguments);
  }

  /// <summary>
  /// The closure a delegate ARGUMENT hands over, as storage: the variable's own when it has one, and
  /// a temporary built from the value when it does not - a lambda written at the call site.
  /// </summary>
  private IrValue ClosureArgumentStorage(Expression argument) {
    if (this.ExistingClosureStorage(argument) is { } storage)
      return storage;
    var temporary = this.ClosureStorage("closure.arg");
    this.StoreClosure(argument, temporary);
    return temporary;
  }

  /// <summary>
  /// Appends the four words a BYVAL delegate argument occupies.
  ///
  /// <para>
  /// They go on in DESCENDING word order because the BASIC convention pushes arguments left to right:
  /// the first listed is pushed first and so lands at the HIGHEST address, and the closure has to read
  /// back as code offset, code segment, environment offset, environment segment ascending - the same
  /// eight bytes the direct emitter pushes, in the same order, so a delegate crossing between the two
  /// paths is the same value on both sides of the call.
  /// </para>
  /// </summary>
  private void AddClosureArgument(Expression argument, List<IrValue> arguments) {
    var closure = this.ClosureArgumentStorage(argument);
    for (var word = ClosureWords - 1; word >= 0; --word)
      arguments.Add(this._b.Load(IrType.I16, this.ClosureWord(closure, word)));
  }

  /// <summary>
  /// Reassembles a BYVAL delegate parameter's four incoming words into the eight contiguous bytes the
  /// body reads it as - the mirror of <see cref="AddClosureArgument"/>.
  /// </summary>
  private void BindClosureParameter(VariableSymbol parameter, IrFunction fn, int firstArgument) {
    var slot = this.SlotFor(parameter);
    for (var word = 0; word < ClosureWords; ++word)
      this._b.Store(fn.Parameters[firstArgument + ClosureWords - 1 - word], this.ClosureWord(slot, word));
  }
}
