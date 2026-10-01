using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// Structured control flow over <see cref="IrBuilder"/>: locals, <c>if</c> and <c>while</c>, so a
/// runtime routine written in IR reads like the loop it is rather than a list of blocks. Locals are
/// entry-block allocas, which <c>Mem2Reg</c> turns into SSA values afterwards.
/// </summary>
internal sealed class IrWriter {

  private readonly IrBasicBlock _entry;
  private int _blocks;

  public IrWriter(IrFunction function, IrType index) {
    this.Function = function;
    this.Index = index;
    this._entry = function.CreateBlock("entry");
    this.B = new IrBuilder(this._entry);
  }

  public IrFunction Function { get; }
  public IrBuilder B { get; }

  /// <summary>
  /// The runtime's own integer for lengths, sizes, positions and counters: 16 bits where pointers
  /// are 16 bits, 32 elsewhere. The <c>rt_*</c> ABI keeps its declared widths; routines convert at
  /// that edge with <see cref="ToIndex"/> and <see cref="FromIndex"/>.
  /// </summary>
  public IrType Index { get; }

  public IrValue Ix(long value) => IrBuilder.ConstInt(this.Index, value);

  /// <summary>An ABI integer as the runtime's index: truncated or sign-extended.</summary>
  public IrValue ToIndex(IrValue value) => Resize(this.B, value, this.Index);

  /// <summary>The runtime's index as the ABI integer <paramref name="type"/>.</summary>
  public IrValue FromIndex(IrValue value, IrType type) => Resize(this.B, value, type);

  private static IrValue Resize(IrBuilder b, IrValue value, IrType type)
    => value.Type.Bits == type.Bits ? value
      : value.Type.Bits > type.Bits ? b.Trunc(value, type) : b.SExt(value, type);

  /// <summary>A mutable local, optionally initialised where it is declared.</summary>
  public sealed class Local(IrWriter writer, IrAlloca slot, IrType type) {
    public IrAlloca Slot { get; } = slot;
    public IrValue Get() => writer.B.Load(type, this.Slot);
    public void Set(IrValue value) => writer.B.Store(value, this.Slot);
  }

  public Local Variable(IrType type, IrValue? initial = null) {
    var slot = this._entry.InsertAt(0, new IrAlloca(type));
    var local = new Local(this, slot, type);
    if (initial is not null)
      local.Set(initial);
    return local;
  }

  /// <summary>A byte buffer in the frame.</summary>
  public IrValue Buffer(int bytes) => this._entry.InsertAt(0, new IrAlloca(IrType.I8) { Count = bytes });

  public IrBasicBlock Block(string name) => this.Function.CreateBlock($"{name}{this._blocks++}");

  public void If(IrValue condition, Action then, Action? otherwise = null) {
    var thenBlock = this.Block("then");
    var elseBlock = otherwise is null ? null : this.Block("else");
    var join = this.Block("endif");
    this.B.CondBr(condition, thenBlock, elseBlock ?? join);
    this.B.Position(thenBlock);
    then();
    this.B.Br(join);
    if (elseBlock is not null) {
      this.B.Position(elseBlock);
      otherwise!();
      this.B.Br(join);
    }
    this.B.Position(join);
  }

  public void While(Func<IrValue> condition, Action body) {
    var test = this.Block("while");
    var loop = this.Block("do");
    var done = this.Block("wend");
    this.B.Br(test);
    this.B.Position(test);
    this.B.CondBr(condition(), loop, done);
    this.B.Position(loop);
    body();
    this.B.Br(test);
    this.B.Position(done);
  }

  /// <summary>Returns, then continues in a fresh block nothing reaches, so the code after an early exit still has somewhere to go.</summary>
  public void Return(IrValue? value = null) {
    this.B.Ret(value);
    this.B.Position(this.Block("after"));
  }

  public IrValue I8(long value) => IrBuilder.ConstInt(IrType.I8, value);
  public IrValue I32(long value) => IrBuilder.ConstInt(IrType.I32, value);
  public IrValue I64(long value) => IrBuilder.ConstInt(IrType.I64, value);
  public IrValue Extended(double value) => new IrConstantFloat(IrType.F80, value);

  /// <summary>The byte at <paramref name="buffer"/> + <paramref name="index"/>.</summary>
  public IrValue ByteAt(IrValue buffer, IrValue index) => this.B.Load(IrType.I8, this.B.Gep(buffer, index));

  public void SetByte(IrValue buffer, IrValue index, IrValue value) => this.B.Store(value, this.B.Gep(buffer, index));

  public IrValue Cmp(IrCmpPred predicate, IrValue a, IrValue b) => this.B.Cmp(predicate, a, b);
}
