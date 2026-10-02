namespace PowerBasic.Compiler.CodeGen;

/// <summary>The DOS file format a program is written in; see <see cref="CodeGenerator.Container"/>.</summary>
public enum DosContainer {
  /// <summary>A flat COM when the build is optimized and self-contained, otherwise an MZ EXE.</summary>
  Auto,
  /// <summary>Always an MZ EXE.</summary>
  Exe,
}
