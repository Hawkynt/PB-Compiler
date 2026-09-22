namespace PowerBasic.Compiler.Backend;

/// <summary>Output target selected after the shared IR middle end.</summary>
public enum IrBackendTarget {
  Mos6502,
  X86_16,
  X86_32,
  X86_64,
  C,
  PowerBasic35,
}
