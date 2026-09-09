using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>O0271 — backend coverage for guarded indirect-call promotion and x86-16 emission.</summary>
[TestFixture]
public sealed class IndirectCallBackendTests {

  [Test]
  public void Promotion_GivenHotTarget_ThenSelectsAndAllocatesDirectAndFallbackCalls() {
    var module = new IrModule("t");
    var targetParameter = new IrArgument(IrType.I16, 0, "x");
    var hot = module.AddFunction(new IrFunction("hot", IrType.I16, [targetParameter]));
    hot.AddBlock(new IrBasicBlock("entry")).Append(new IrRet(targetParameter));

    var handler = new IrArgument(IrType.Ptr, 0, "handler");
    var caller = module.AddFunction(new IrFunction("caller", IrType.I16, [handler]));
    var entry = caller.AddBlock(new IrBasicBlock("entry"));
    var call = entry.Append(new IrCall(IrType.I16, handler, [new IrConstantInt(IrType.I16, 7)]));
    entry.Append(new IrRet(call));
    call.SetIndirectTargetProfile(new IrIndirectCallProfile(100, new IrIndirectCallTarget(hot, 80)));

    Assert.That(IndirectCallPromotion.Run(module), Is.EqualTo(1));

    var machine = InstructionSelector.TrySelect(caller, out var declineReason);
    Assert.That(machine, Is.Not.Null, $"selection declined: {declineReason}");
    var allocation = LinearScanAllocator.Allocate(machine!);
    Assert.That(allocation, Is.Not.Null, "the guarded direct/fallback machine CFG must allocate");

    var calls = machine!.AllInstructions.Where(instruction => instruction.Opcode == MOpcode.Call).ToArray();
    Assert.Multiple(() => {
      Assert.That(calls, Has.Some.Matches<MInstr>(instruction =>
        instruction.Operands is [MOperand.LabelRef { Name: "hot" }]),
        "the promoted arm must remain a direct CALL hot");
      Assert.That(calls, Has.Some.Matches<MInstr>(instruction =>
        instruction.Operands is [MOperand.Register { Reg: { IsVirtual: false, Physical: Reg.SI } }]),
        "the miss arm must stage the original target and CALL SI");
    });
  }

  [Test]
  public void Emit_GivenIndirectCallThroughSi_ThenUses8086FfSlash2Encoding() {
    var function = new MFunction("t");
    var block = new MBlock("entry");
    block.Instructions.Add(new MInstr(MOpcode.Call,
      [new MOperand.Register(MReg.Physical_(Reg.SI, MRegSize.Word))], MInstrEffect.None));
    function.Blocks.Add(block);

    var assembler = new Assembler();
    MachineEmitter.Emit(assembler, function, new Dictionary<int, Reg>());

    // Intel CALL r/m16 is FF /2. For register SI the ModR/M byte is 11 010 110b = D6h.
    Assert.That(assembler.ToArray(), Is.EqualTo(new byte[] { 0xFF, 0xD6 }));
  }
}
