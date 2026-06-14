namespace PowerBasic.Compiler.Ir;

/// <summary>
/// The base of everything that can be used as an operand: constants, function
/// arguments, globals and the results of instructions. Every value carries its
/// <see cref="IrType"/> and an intrusive use-list of the instructions that
/// reference it, which is what makes def-use walking, dead-code elimination and
/// <see cref="ReplaceAllUsesWith"/> cheap and exact (the LLVM design).
/// </summary>
public abstract class IrValue {

  private readonly List<IrInstruction> _users = [];

  protected IrValue(IrType type) => this.Type = type;

  /// <summary>The type this value produces.</summary>
  public IrType Type { get; }

  /// <summary>An optional human-readable name, used only when printing.</summary>
  public string? Name { get; set; }

  /// <summary>The instructions that currently reference this value as an operand.</summary>
  public IReadOnlyList<IrInstruction> Users => this._users;

  /// <summary>True when nothing references this value.</summary>
  public bool HasNoUsers => this._users.Count == 0;

  internal void AddUser(IrInstruction user) => this._users.Add(user);

  internal void RemoveUser(IrInstruction user) => this._users.Remove(user);

  /// <summary>
  /// Rewrites every operand that points at this value so it points at
  /// <paramref name="replacement"/> instead, keeping all use-lists consistent.
  /// This is the workhorse of constant folding, GVN and instruction simplification.
  /// </summary>
  public void ReplaceAllUsesWith(IrValue replacement) {
    if (ReferenceEquals(this, replacement))
      return;

    // snapshot: ReplaceOperand mutates this._users as it rewires each user
    foreach (var user in this._users.ToArray())
      user.ReplaceOperand(this, replacement);
  }
}
