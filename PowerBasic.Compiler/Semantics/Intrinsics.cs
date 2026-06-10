namespace PowerBasic.Compiler.Semantics;

/// <summary>Return-type rule of an intrinsic function.</summary>
public enum IntrinsicReturn { Integer, Word, Dword, Long, Single, Double, Ext, String, MatchArg }

/// <summary>One built-in function signature.</summary>
public sealed record IntrinsicInfo(string Name, int MinArgs, int MaxArgs, IntrinsicReturn Returns);

/// <summary>
/// Catalog of PB 3.5 built-in functions, used by the binder to tell intrinsic
/// calls apart from array indexing and user FUNCTION calls. Statement-form
/// keywords (SHIFT, BIT SET, POKE, ...) are not listed here - they are parsed
/// as statements.
/// </summary>
public static class Intrinsics {

  private static readonly Dictionary<string, IntrinsicInfo> _byName = new(StringComparer.OrdinalIgnoreCase);

  public static bool TryGet(string name, out IntrinsicInfo info) => _byName.TryGetValue(name, out info!);

  public static bool IsIntrinsic(string name) => _byName.ContainsKey(name);

  private static void Add(string name, int minArgs, int maxArgs, IntrinsicReturn returns)
    => _byName[name] = new(name, minArgs, maxArgs, returns);

  static Intrinsics() {
    // --- string producing -------------------------------------------------
    Add("CHR$", 1, 1, IntrinsicReturn.String);
    Add("STR$", 1, 2, IntrinsicReturn.String);
    Add("STRING$", 2, 2, IntrinsicReturn.String);
    Add("SPACE$", 1, 1, IntrinsicReturn.String);
    Add("LEFT$", 2, 2, IntrinsicReturn.String);
    Add("RIGHT$", 2, 2, IntrinsicReturn.String);
    Add("MID$", 2, 3, IntrinsicReturn.String);
    Add("UCASE$", 1, 1, IntrinsicReturn.String);
    Add("LCASE$", 1, 1, IntrinsicReturn.String);
    Add("LTRIM$", 1, 2, IntrinsicReturn.String);
    Add("RTRIM$", 1, 2, IntrinsicReturn.String);
    Add("HEX$", 1, 2, IntrinsicReturn.String);
    Add("OCT$", 1, 2, IntrinsicReturn.String);
    Add("BIN$", 1, 2, IntrinsicReturn.String);
    Add("REPEAT$", 2, 2, IntrinsicReturn.String);
    Add("EXTRACT$", 2, 3, IntrinsicReturn.String);
    Add("REMOVE$", 2, 2, IntrinsicReturn.String);
    Add("INKEY$", 0, 0, IntrinsicReturn.String);
    Add("COMMAND$", 0, 0, IntrinsicReturn.String);
    Add("ENVIRON$", 1, 1, IntrinsicReturn.String);
    Add("TIME$", 0, 0, IntrinsicReturn.String);
    Add("DATE$", 0, 0, IntrinsicReturn.String);
    Add("ERDEV$", 0, 0, IntrinsicReturn.String);
    Add("PEEK$", 2, 2, IntrinsicReturn.String);
    Add("MIN$", 2, 2, IntrinsicReturn.String);
    Add("MAX$", 2, 2, IntrinsicReturn.String);
    Add("MKI$", 1, 1, IntrinsicReturn.String);
    Add("MKL$", 1, 1, IntrinsicReturn.String);
    Add("MKS$", 1, 1, IntrinsicReturn.String);
    Add("MKD$", 1, 1, IntrinsicReturn.String);
    Add("MKE$", 1, 1, IntrinsicReturn.String);
    Add("MKWRD$", 1, 1, IntrinsicReturn.String);
    Add("MKDWD$", 1, 1, IntrinsicReturn.String);
    Add("MKBYT$", 1, 1, IntrinsicReturn.String);

    // --- numeric: conversions & packed decode ------------------------------
    Add("CINT", 1, 1, IntrinsicReturn.Integer);
    Add("CLNG", 1, 1, IntrinsicReturn.Long);
    Add("CSNG", 1, 1, IntrinsicReturn.Single);
    Add("CDBL", 1, 1, IntrinsicReturn.Double);
    Add("CEXT", 1, 1, IntrinsicReturn.Ext);
    Add("CBYT", 1, 1, IntrinsicReturn.Word);
    Add("CWRD", 1, 1, IntrinsicReturn.Word);
    Add("CDWD", 1, 1, IntrinsicReturn.Dword);
    Add("CVI", 1, 1, IntrinsicReturn.Integer);
    Add("CVL", 1, 1, IntrinsicReturn.Long);
    Add("CVS", 1, 1, IntrinsicReturn.Single);
    Add("CVD", 1, 1, IntrinsicReturn.Double);
    Add("CVE", 1, 1, IntrinsicReturn.Ext);
    Add("CVWRD", 1, 1, IntrinsicReturn.Word);
    Add("CVDWD", 1, 1, IntrinsicReturn.Dword);
    Add("CVBYT", 1, 1, IntrinsicReturn.Word);
    Add("VAL", 1, 1, IntrinsicReturn.Ext);

    // --- numeric: math ------------------------------------------------------
    Add("ABS", 1, 1, IntrinsicReturn.MatchArg);
    Add("SGN", 1, 1, IntrinsicReturn.Integer);
    Add("INT", 1, 1, IntrinsicReturn.MatchArg);
    Add("FIX", 1, 1, IntrinsicReturn.MatchArg);
    Add("CEIL", 1, 1, IntrinsicReturn.MatchArg);
    Add("FRAC", 1, 1, IntrinsicReturn.MatchArg);
    Add("ROUND", 1, 2, IntrinsicReturn.MatchArg);
    Add("MIN", 2, 16, IntrinsicReturn.MatchArg);
    Add("MAX", 2, 16, IntrinsicReturn.MatchArg);
    Add("MIN%", 2, 16, IntrinsicReturn.Integer);
    Add("MAX%", 2, 16, IntrinsicReturn.Integer);
    Add("SQR", 1, 1, IntrinsicReturn.Ext);
    Add("SIN", 1, 1, IntrinsicReturn.Ext);
    Add("COS", 1, 1, IntrinsicReturn.Ext);
    Add("TAN", 1, 1, IntrinsicReturn.Ext);
    Add("ATN", 1, 1, IntrinsicReturn.Ext);
    Add("EXP", 1, 1, IntrinsicReturn.Ext);
    Add("EXP2", 1, 1, IntrinsicReturn.Ext);
    Add("EXP10", 1, 1, IntrinsicReturn.Ext);
    Add("LOG", 1, 1, IntrinsicReturn.Ext);
    Add("LOG2", 1, 1, IntrinsicReturn.Ext);
    Add("LOG10", 1, 1, IntrinsicReturn.Ext);
    Add("RND", 0, 1, IntrinsicReturn.Single);

    // --- string inspection ---------------------------------------------------
    Add("LEN", 1, 1, IntrinsicReturn.Long);
    Add("ASC", 1, 2, IntrinsicReturn.Integer);
    Add("ASCII", 1, 2, IntrinsicReturn.Integer);
    Add("INSTR", 2, 3, IntrinsicReturn.Long);
    Add("VERIFY", 2, 3, IntrinsicReturn.Long);
    Add("TALLY", 2, 2, IntrinsicReturn.Long);

    // --- system / runtime state ---------------------------------------------
    Add("ERR", 0, 0, IntrinsicReturn.Integer);
    Add("ERL", 0, 0, IntrinsicReturn.Long);
    Add("ERDEV", 0, 0, IntrinsicReturn.Integer);
    Add("FRE", 0, 1, IntrinsicReturn.Long);
    Add("FREEFILE", 0, 0, IntrinsicReturn.Integer);
    Add("EOF", 1, 1, IntrinsicReturn.Integer);
    Add("LOF", 1, 1, IntrinsicReturn.Long);
    Add("LOC", 1, 1, IntrinsicReturn.Long);
    Add("SEEK", 1, 1, IntrinsicReturn.Long);
    Add("TIMER", 0, 0, IntrinsicReturn.Single);
    Add("CSRLIN", 0, 0, IntrinsicReturn.Integer);
    Add("POS", 0, 1, IntrinsicReturn.Integer);
    Add("LPOS", 0, 1, IntrinsicReturn.Integer);
    Add("SCREEN", 2, 3, IntrinsicReturn.Integer);
    Add("POINT", 2, 2, IntrinsicReturn.Long);
    Add("PLAY", 1, 1, IntrinsicReturn.Integer);
    Add("ISTRUE", 1, 1, IntrinsicReturn.Integer);
    Add("ISFALSE", 1, 1, IntrinsicReturn.Integer);

    // --- low level ------------------------------------------------------------
    Add("PEEK", 1, 2, IntrinsicReturn.Integer);
    Add("PEEKI", 1, 2, IntrinsicReturn.Integer);
    Add("PEEKL", 1, 2, IntrinsicReturn.Long);
    Add("INP", 1, 1, IntrinsicReturn.Integer);
    Add("VARPTR", 1, 1, IntrinsicReturn.Word);
    Add("VARSEG", 1, 1, IntrinsicReturn.Word);
    Add("STRPTR", 1, 1, IntrinsicReturn.Word);
    Add("STRSEG", 1, 1, IntrinsicReturn.Word);
    Add("CODEPTR", 1, 1, IntrinsicReturn.Word);
    Add("CODESEG", 1, 1, IntrinsicReturn.Word);
    Add("BIT", 2, 2, IntrinsicReturn.Integer);
    Add("BITS", 3, 3, IntrinsicReturn.Long);
    Add("UBOUND", 1, 2, IntrinsicReturn.Long);
    Add("LBOUND", 1, 2, IntrinsicReturn.Long);
    Add("REG", 1, 1, IntrinsicReturn.Integer);

    // --- print-list helpers (only valid inside PRINT) ---------------------------
    Add("SPC", 1, 1, IntrinsicReturn.String);
    Add("TAB", 1, 1, IntrinsicReturn.String);
    Add("USING$", 2, 16, IntrinsicReturn.String);
  }
}
