using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// Named runtime storage used only by architecture emulation. Keeping names here gives the code
/// generator, unit linker and runtime trimmer one ABI. XMM/YMM/ZMM deliberately alias the low
/// 16/32/64 bytes of one 64-byte slot per architectural register, exactly as the hardware register
/// file does. 32-bit GP registers need only a virtual high word because their low word is the real
/// AX/CX/DX/BX/SP/BP/SI/DI and therefore naturally aliases ordinary 8086 code.
/// </summary>
public static class RuntimeIsaState {
  public const string VectorBank = "rt_isa_vzmm";
  public const string MmxBank = "rt_isa_vmm";
  public const string GpHighBank = "rt_isa_gphi";
  public const string Scratch = "rt_isa_scratch";
  public const string X87State = "rt_isa_x87";

  public const int VectorRegisterBytes = 64;
  public const int VectorRegisterCount = 8;
  public const int VectorBankBytes = VectorRegisterBytes * VectorRegisterCount;
  public const int MmxRegisterBytes = 8;
  public const int MmxBankBytes = MmxRegisterBytes * 8;
  public const int GpHighBankBytes = 2 * 8;
  public const int ScratchBytes = 128;
  public const int X87StateBytes = 96;

  public static int VectorOffset(Reg register) => register.Index() * VectorRegisterBytes;
  public static int MmxOffset(Reg register) => register.Index() * MmxRegisterBytes;
  public static int GpHighOffset(Reg register) => register.Index() * 2;
}

public sealed partial class DosRuntime {
  /// <summary>
  /// Storage for compiler-emulated architectural state. RuntimeTrimmer sees each named provider in
  /// this section and includes it only when generated code references one of these labels. Under BSS
  /// mode the state consumes memory but no EXE payload.
  /// </summary>
  private void EmitIsaEmulationData(Assembler asm) {
    asm.Align(2);
    this.ZeroBlob(asm, RuntimeIsaState.VectorBank, RuntimeIsaState.VectorBankBytes);
    this.ZeroBlob(asm, RuntimeIsaState.MmxBank, RuntimeIsaState.MmxBankBytes);
    this.ZeroBlob(asm, RuntimeIsaState.GpHighBank, RuntimeIsaState.GpHighBankBytes);
    this.ZeroBlob(asm, RuntimeIsaState.Scratch, RuntimeIsaState.ScratchBytes);
    this.ZeroBlob(asm, RuntimeIsaState.X87State, RuntimeIsaState.X87StateBytes);
  }
}
