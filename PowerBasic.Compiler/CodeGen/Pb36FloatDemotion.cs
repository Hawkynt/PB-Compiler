using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 O12 - float demotion ("de-floating", docs/PB36.md). PB defaults bare
/// variables to SINGLE, so DOS-era counters are floats by accident; the
/// integral-promotion semantics additionally route their arithmetic through
/// the x87. When a whole-program proof shows a SINGLE/DOUBLE variable only
/// ever holds integral values exactly representable in BOTH the original
/// float type and the target integer type, and every read sits in a
/// value-exact context (no division/power, no float literals, no USING, no
/// calls over the value), the variable silently re-types to INTEGER/LONG and
/// its FOR loops run on integer registers.
///
/// The proof is deliberately conservative: writes must be FOR headers with
/// foldable integral bounds or assignments of foldable integral constants;
/// any appearance as a call argument (BYREF risk), VARPTR-class escape,
/// INPUT/READ/SWAP target, USING item or unhandled statement blocks the
/// variable; inline asm or indirect control flow anywhere disables the pass.
/// PRINT stays byte-identical because integral floats already print without
/// a decimal point at every precision (oracle-verified, QUIRKS.md).
/// </summary>
public static class Pb36FloatDemotion {

  private const long _SINGLE_EXACT = 1L << 24;       // |v| <= 2^24: SINGLE holds it exactly
  private const long _DOUBLE_EXACT = int.MaxValue;   // LONG range is far below 2^53

  private sealed class Candidate {
    public bool Blocked;
    public bool HasCounterWrite;
    public long Min = long.MaxValue;
    public long Max = long.MinValue;

    public void Observe(long value) {
      this.Min = Math.Min(this.Min, value);
      this.Max = Math.Max(this.Max, value);
    }
  }

  /// <summary>Runs the analysis and re-types the proven variables; returns them (for tests and diagnostics). The caller gates on the optimizer flag; this pass is dialect-agnostic (SINGLE-default counters exist in QB/TB/PB alike).</summary>
  public static IReadOnlyList<VariableSymbol> Apply(SemanticModel model) {
    var pass = new State(model);
    pass.Collect();
    if (pass.Killed || pass.Candidates.Count == 0)
      return [];

    foreach (var statements in pass.AllBodies())
      if (!pass.Walk(statements)) {
        return []; // an unhandled construct - keep everything as bound
      }

    var demoted = new List<VariableSymbol>();
    foreach (var (symbol, info) in pass.Candidates) {
      if (info.Blocked || !info.HasCounterWrite || info.Min > info.Max)
        continue;
      var exactLimit = symbol.Type is ScalarType { ByteSize: 8 } ? _DOUBLE_EXACT : _SINGLE_EXACT;
      if (info.Min < -exactLimit || info.Max > exactLimit)
        continue;
      var target = info.Min >= short.MinValue && info.Max <= short.MaxValue ? PbType.Integer : PbType.Long;
      symbol.Type = target;
      demoted.Add(symbol);
    }
    if (demoted.Count == 0)
      return [];

    // re-type every bound read/write of the demoted variables; consumers keep
    // their own bound types and coerce, which is value-identical for the
    // proven integral range
    foreach (var (expression, symbol) in model.VariableBindings)
      if (demoted.Contains(symbol) && model.ExpressionTypes.TryGetValue(expression, out var t) && t is ScalarType { IsFloat: true })
        model.ExpressionTypes[expression] = symbol.Type;

    return demoted;
  }

  private sealed class State(SemanticModel model) {
    public readonly Dictionary<VariableSymbol, Candidate> Candidates = new(ReferenceEqualityComparer.Instance);
    public bool Killed;
    private readonly ConstantFolder _folder = new(model.Equates);

    public IEnumerable<IReadOnlyList<Statement>> AllBodies() {
      yield return model.MainBody;
      foreach (var proc in model.ProcedureList)
        if (proc.Body is { } body)
          yield return body;
    }

    public void Collect() {
      foreach (var symbol in model.ModuleVariables.Values)
        this.Consider(symbol);
      foreach (var proc in model.ProcedureList) {
        foreach (var symbol in proc.Variables.Values)
          this.Consider(symbol);
        foreach (var parameter in proc.Parameters)
          this.Candidates.Remove(parameter);
      }
    }

    private void Consider(VariableSymbol symbol) {
      if (symbol.Storage == VariableStorage.Parameter)
        return;
      if (symbol.Type is not ScalarType { IsFloat: true, ByteSize: 4 or 8 })
        return; // EXT/FIX/BCD stay; arrays are not VariableSymbols of scalar type
      this.Candidates[symbol] = new();
    }

    private VariableSymbol? BindingOf(Expression e)
      => model.VariableBindings.TryGetValue(e, out var s) ? s : null;

    private Candidate? CandidateOf(Expression e)
      => this.BindingOf(e) is { } s && this.Candidates.TryGetValue(s, out var c) ? c : null;

    private void BlockAll() {
      foreach (var candidate in this.Candidates.Values)
        candidate.Blocked = true;
    }

    /// <summary>Walks one statement list; false = unhandled construct seen (caller aborts the pass).</summary>
    public bool Walk(IReadOnlyList<Statement> statements) {
      foreach (var statement in statements)
        if (!this.WalkStatement(statement))
          return false;
      return true;
    }

    private bool WalkStatement(Statement statement) {
      switch (statement) {
        // ---- no expressions / compile-time only --------------------------------
        case DeclareStmt or DefTypeStmt or EquateStmt or DataStmt or RestoreStmt or MetaStmt
          or LabelStmt or GotoStmt or GosubStmt or ReturnStmt or EndStmt or ExitStmt or ExitFarStmt
          or IterateStmt or OnErrorStmt or ResumeStmt or EventControlStmt or CloseStmt:
          return true;

        // ---- escapes and indirect flow kill the whole pass ---------------------
        case InlineAsmStmt or CallPtrStmt or GotoPtrStmt or GosubPtrStmt or ChainStmt:
          this.Killed = true;
          this.BlockAll();
          return true;

        case AssignStmt a:
          return this.WalkAssign(a);

        case IncrDecrStmt id:
          if (this.CandidateOf(id.Target) is { } incrTarget)
            incrTarget.Blocked = true; // unbounded accumulation - needs range analysis (O16)
          this.Safe(id.Target);
          if (id.Amount is { } amount)
            this.Safe(amount);
          return true;

        case ForStmt f:
          return this.WalkFor(f);

        case DoLoopStmt d:
          if (d.PreCondition is { } pre)
            this.Safe(pre);
          if (d.PostCondition is { } post)
            this.Safe(post);
          return this.Walk(d.Body);

        case IfStmt i: {
          this.Safe(i.Condition);
          if (!this.Walk(i.Then))
            return false;
          foreach (var (condition, body) in i.ElseIfs) {
            this.Safe(condition);
            if (!this.Walk(body))
              return false;
          }
          return i.Else == null || this.Walk(i.Else);
        }

        case SelectStmt s:
          return this.WalkSelect(s);

        case PrintStmt p: {
          if (p.FileNumber is { } fn)
            this.Safe(fn);
          var usingBlocks = p.UsingFormat != null;
          foreach (var item in p.Items)
            if (item.Value is { } value) {
              if (usingBlocks)
                this.BlockContained(value); // USING formats floats differently
              else
                this.Safe(value);
            }
          return true;
        }

        case CallStmt c: {
          foreach (var argument in c.Arguments)
            this.BlockContained(argument); // BYREF risk - the callee may write or escape it
          return true;
        }

        case InputStmt or StdInStmt or ReadStmt:
          foreach (var target in TargetsOf(statement))
            this.BlockContained(target);
          return true;

        case SwapStmt sw:
          this.BlockContained(sw.Left);
          this.BlockContained(sw.Right);
          return true;

        case WriteStmt w: {
          if (w.FileNumber is { } wfn)
            this.Safe(wfn);
          foreach (var item in w.Items)
            this.BlockContained(item); // WRITE # quotes/format rules differ per type
          return true;
        }

        case MidAssignStmt m:
          this.BlockContained(m.Target);
          this.Safe(m.Start);
          if (m.Length is { } len)
            this.Safe(len);
          this.Safe(m.Value);
          return true;

        case AscAssignStmt aa:
          this.BlockContained(aa.Target);
          if (aa.Index is { } aaIndex)
            this.Safe(aaIndex);
          this.Safe(aa.Value);
          return true;

        case LsetRsetStmt lr:
          this.BlockContained(lr.Target);
          this.Safe(lr.Value);
          return true;

        case BitStmt b:
          if (this.CandidateOf(b.Target) is { } bitTarget)
            bitTarget.Blocked = true;
          this.Safe(b.Target);
          this.Safe(b.Bit);
          return true;

        case ReplaceStmt r:
          this.Safe(r.Find);
          this.Safe(r.With);
          this.BlockContained(r.Target);
          return true;

        case DimStmt or RedimStmt or EraseStmt: {
          foreach (var e in ExpressionsOf(statement))
            this.Safe(e);
          return true;
        }

        case ArraySortStmt asrt: {
          foreach (var e in new[] { asrt.Count, asrt.FromPos, asrt.ToPos, asrt.Collate })
            if (e != null)
              this.Safe(e);
          return true;
        }

        case ArrayScanStmt ascn: {
          foreach (var e in new[] { ascn.Count, ascn.FromPos, ascn.ToPos, ascn.Collate, ascn.Match })
            if (e != null)
              this.Safe(e);
          this.BlockContained(ascn.Target);
          return true;
        }

        case OpenStmt o:
          this.Safe(o.FileName);
          this.Safe(o.FileNumber);
          if (o.RecordLength is { } rl)
            this.Safe(rl);
          return true;

        case GetPutFileStmt gp: {
          this.Safe(gp.FileNumber);
          if (gp.RecordNumber is { } gpPos)
            this.Safe(gpPos);
          if (gp.Variable is { } target)
            this.BlockContained(target); // GET into a candidate writes raw bytes
          return true;
        }

        case SeekStmt sk:
          this.Safe(sk.FileNumber);
          this.Safe(sk.Target);
          return true;

        case FieldStmt fl:
          this.Safe(fl.FileNumber);
          foreach (var (width, target) in fl.Fields) {
            this.Safe(width);
            this.BlockContained(target);
          }
          return true;

        case OnGotoStmt og:
          this.Safe(og.Selector);
          return true;

        case ErrorStmt er:
          this.Safe(er.Code);
          return true;

        case OnEventStmt:
          return true;

        case DefSegStmt ds:
          if (ds.Segment is { } seg)
            this.Safe(seg);
          return true;

        case StdOutStmt so:
          if (so.Value is { } soValue)
            this.Safe(soValue);
          return true;

        case CommandStmt cmd: {
          foreach (var argument in cmd.Arguments)
            if (argument != null)
              this.Safe(argument);
          return true;
        }

        case LineStmt ln: {
          if (ln.From is { } lf) {
            this.Safe(lf.X);
            this.Safe(lf.Y);
          }
          this.Safe(ln.To.X);
          this.Safe(ln.To.Y);
          foreach (var e in new[] { ln.Color, ln.Style })
            if (e != null)
              this.Safe(e);
          return true;
        }

        case CircleStmt ci: {
          this.Safe(ci.Center.X);
          this.Safe(ci.Center.Y);
          this.Safe(ci.Radius);
          foreach (var e in new[] { ci.Color, ci.Start, ci.End, ci.Aspect })
            if (e != null)
              this.Safe(e);
          return true;
        }

        case PsetStmt ps: {
          this.Safe(ps.Point.X);
          this.Safe(ps.Point.Y);
          if (ps.Color is { } psc)
            this.Safe(psc);
          return true;
        }

        case GetPutGraphicsStmt gg: {
          this.Safe(gg.From.X);
          this.Safe(gg.From.Y);
          if (gg.To is { } gt) {
            this.Safe(gt.X);
            this.Safe(gt.Y);
          }
          return true;
        }

        default:
          return false; // unknown statement shape - abort the pass entirely
      }
    }

    private bool WalkAssign(AssignStmt a) {
      if (this.CandidateOf(a.Target) is { } candidate) {
        // a write to a candidate must be a foldable integral constant
        if (this._folder.TryFold(a.Value) is { Integer: { } constant })
          candidate.Observe(constant);
        else
          candidate.Blocked = true;
        this.Safe(a.Value);
        return true;
      }
      // writes into arrays/other variables: subscripts and value are reads
      this.Safe(a.Target);
      if (model.TypeOf(a.Target) is StringType or FixedStringType or FlexType or AsciizType)
        this.BlockContained(a.Value); // string expressions over candidates would need STR$-class calls anyway
      else
        this.Safe(a.Value);
      return true;
    }

    private bool WalkFor(ForStmt f) {
      if (this.CandidateOf(f.Variable) is not { } candidate) {
        this.Safe(f.From);
        this.Safe(f.To);
        if (f.Step is { } st)
          this.Safe(st);
        return this.Walk(f.Body);
      }

      candidate.HasCounterWrite = true;
      if (this._folder.TryFold(f.From) is not { Integer: { } from }
          || this._folder.TryFold(f.To) is not { Integer: { } to }
          || (f.Step is { } stepExpr ? this._folder.TryFold(stepExpr) : new ConstantValue(1L, null, null)) is not { Integer: { } step }
          || step == 0) {
        candidate.Blocked = true;
        return this.Walk(f.Body);
      }

      // float counters do not wrap: the loop runs n = max(0, floor((to-from)/step)+1)
      // iterations and the counter ends on the first failing value from + n*step
      var iterations = step > 0
        ? from > to ? 0 : (to - from) / step + 1
        : from < to ? 0 : (from - to) / -step + 1;
      var final = from + iterations * step;
      candidate.Observe(from);
      candidate.Observe(final);
      if (iterations > 0)
        candidate.Observe(from + (iterations - 1) * step);

      this.Safe(f.From);
      this.Safe(f.To);
      if (f.Step is { } s2)
        this.Safe(s2);
      return this.Walk(f.Body);
    }

    private bool WalkSelect(SelectStmt s) {
      var selectorHasCandidate = this.ContainsCandidate(s.Subject);
      this.Safe(s.Subject);
      foreach (var arm in s.Arms) {
        foreach (var selector in arm.Selectors)
          foreach (var e in new[] { selector.Value, selector.RangeUpper })
            if (e != null) {
              if (selectorHasCandidate && this._folder.TryFold(e) is not { Integer: not null })
                this.BlockContained(s.Subject); // candidate compared against a non-integral CASE
              this.Safe(e);
            }
        if (!this.Walk(arm.Body))
          return false;
      }
      return true;
    }

    /// <summary>
    /// Value-exactness check for one expression tree: candidate reads inside
    /// stay observably identical after demotion as long as the tree is built
    /// from integral-friendly operators (no /, no ^, no float literals, no
    /// calls or intrinsics over the candidate); array subscripts recurse as
    /// their own trees. Unsafe trees block the contained candidates.
    /// </summary>
    private bool Safe(Expression e) {
      if (!this.ContainsCandidate(e))
        return true;
      if (this.TreeIsValueExact(e))
        return true;
      this.BlockContained(e);
      return true;
    }

    private bool TreeIsValueExact(Expression e) => e switch {
      IntegerLiteralExpr or NamedConstantExpr => true,
      FloatLiteralExpr => false,
      StringLiteralExpr => true,
      NameExpr => true,
      UnaryExpr u => u.Op is UnaryOp.Negate or UnaryOp.Not && this.TreeIsValueExact(u.Operand),
      BinaryExpr b => b.Op is BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply
          or BinaryOp.IntegerDivide or BinaryOp.Modulo
          or BinaryOp.And or BinaryOp.Or or BinaryOp.Xor or BinaryOp.Eqv or BinaryOp.Imp
          or BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater
          or BinaryOp.LessEqual or BinaryOp.GreaterEqual
          or BinaryOp.ShiftLeft or BinaryOp.ShiftRightArith or BinaryOp.ShiftRightLogical
          or BinaryOp.RotateLeft or BinaryOp.RotateRight
        && this.TreeIsValueExact(b.Left) && this.TreeIsValueExact(b.Right),
      CallOrIndexExpr call when this.BindingOf(call) != null =>
        call.Arguments.All(sub => { this.Safe(sub); return !this.ContainsCandidate(sub) || this.TreeIsValueExact(sub); }),
      ByValArgExpr bv => this.TreeIsValueExact(bv.Value),
      _ => false,
    };

    private bool ContainsCandidate(Expression e) {
      if (this.CandidateOf(e) != null)
        return true;
      return e switch {
        UnaryExpr u => this.ContainsCandidate(u.Operand),
        BinaryExpr b => this.ContainsCandidate(b.Left) || this.ContainsCandidate(b.Right),
        CallOrIndexExpr c => c.Arguments.Any(this.ContainsCandidate),
        ByValArgExpr bv => this.ContainsCandidate(bv.Value),
        // unmodeled node (e.g. a new pb36 operator): recurse its children so a
        // candidate nested inside it is still detected (and then blocked).
        _ => AstQuery.Subexpressions(e).Any(this.ContainsCandidate),
      };
    }

    private void BlockContained(Expression e) {
      if (this.CandidateOf(e) is { } direct)
        direct.Blocked = true;
      switch (e) {
        case UnaryExpr u:
          this.BlockContained(u.Operand);
          break;
        case BinaryExpr b:
          this.BlockContained(b.Left);
          this.BlockContained(b.Right);
          break;
        case CallOrIndexExpr c:
          foreach (var argument in c.Arguments)
            this.BlockContained(argument);
          break;
        case ByValArgExpr bv:
          this.BlockContained(bv.Value);
          break;
        default:
          // unmodeled node: block every candidate nested inside it (conservative).
          foreach (var child in AstQuery.Subexpressions(e))
            this.BlockContained(child);
          break;
      }
    }

    private static IEnumerable<Expression> TargetsOf(Statement statement) => statement switch {
      InputStmt i => i.Targets,
      StdInStmt s => [s.Target],
      ReadStmt r => r.Targets,
      _ => [],
    };

    private static IEnumerable<Expression> ExpressionsOf(Statement statement) => statement switch {
      DimStmt d => DeclBounds(d.Variables),
      RedimStmt r => DeclBounds(r.Variables),
      _ => [],
    };

    private static IEnumerable<Expression> DeclBounds(IReadOnlyList<VariableDecl> variables) {
      foreach (var variable in variables)
        foreach (var (lower, upper) in variable.ArrayBounds ?? [])
          foreach (var e in new[] { lower, upper })
            if (e != null)
              yield return e;
    }
  }
}
