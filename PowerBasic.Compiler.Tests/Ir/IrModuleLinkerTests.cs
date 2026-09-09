using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0277's IR thin-link: separately lowered modules become one call graph before interprocedural
/// optimization, while malformed symbol tables are rejected rather than guessed through.
/// </summary>
[TestFixture]
public sealed class IrModuleLinkerTests {

  [Test]
  public void Link_GivenCrossModuleCall_ThenInterproceduralPassesSeeOneWholeProgram() {
    var mainModule = new IrModule("MAIN", Dialect.Pb36);
    var main = mainModule.AddFunction(new IrFunction("main", IrType.Void));
    var scaleDeclaration = mainModule.AddFunction(new IrFunction("scale", IrType.I16, [
      new IrArgument(IrType.I16, 0, "v"),
      new IrArgument(IrType.I16, 1, "k"),
    ]));
    var mainEntry = main.CreateBlock("entry");
    mainEntry.Append(new IrCall(IrType.I16, scaleDeclaration, [
      new IrConstantInt(IrType.I16, 3),
      new IrConstantInt(IrType.I16, 4),
    ]));
    mainEntry.Append(new IrRet());

    var unit = new IrModule("UNIT", Dialect.Pb36);
    var v = new IrArgument(IrType.I16, 0, "v");
    var k = new IrArgument(IrType.I16, 1, "k");
    var scale = unit.AddFunction(new IrFunction("Scale", IrType.I16, [v, k]));
    var scaleEntry = scale.CreateBlock("entry");
    var product = scaleEntry.Append(new IrBinary(IrBinaryOp.Mul, v, k));
    scaleEntry.Append(new IrRet(product));
    unit.AddFunction(VoidFunction("DeadInUnit"));

    var linked = IrModuleLinker.Link(mainModule, [unit]);
    var linkedMain = linked.FindFunction("main")!;
    var linkedScale = linked.FindFunction("Scale")!;
    var call = linkedMain.AllInstructions.OfType<IrCall>().Single();

    Assert.Multiple(() => {
      Assert.That(call.Callee, Is.SameAs(linkedScale), "the declaration must resolve to the unit definition");
      Assert.That(IrVerifier.Verify(linked), Is.Empty);
      Assert.That(scaleDeclaration.IsDeclaration, Is.True, "linking must not mutate the input module");
      Assert.That(scale.IsDeclaration, Is.False, "linking must not consume the input definition");
    });

    IrPassManager.Standard().RunOnModule(linked);

    var ret = linkedScale.AllInstructions.OfType<IrRet>().Single();
    Assert.Multiple(() => {
      Assert.That(ret.Value, Is.InstanceOf<IrConstantInt>(), "IPCP should expose 3*4 to the local folder");
      Assert.That(((IrConstantInt)ret.Value!).Value, Is.EqualTo(12));
    });

    var removed = GlobalDce.Run(linked);
    Assert.Multiple(() => {
      Assert.That(removed, Is.EqualTo(1));
      Assert.That(linked.FindFunction("DeadInUnit"), Is.Null, "whole-program DCE may now remove dead unit code");
      Assert.That(linked.FindFunction("Scale"), Is.Not.Null, "the unit function called by main stays live");
      Assert.That(IrVerifier.Verify(linked), Is.Empty);
    });
  }

  [Test]
  public void Link_GivenSameNamedModuleGlobals_ThenStorageStaysDistinct() {
    var mainModule = new IrModule("MAIN");
    var mainGlobal = mainModule.AddGlobal(new IrGlobalVariable("scratch", IrType.I16));
    var main = mainModule.AddFunction(new IrFunction("main", IrType.Void));
    var mainEntry = main.CreateBlock("entry");
    mainEntry.Append(new IrLoad(IrType.I16, mainGlobal));
    mainEntry.Append(new IrRet());

    var unit = new IrModule("UNIT");
    var unitGlobal = unit.AddGlobal(new IrGlobalVariable("scratch", IrType.I16));
    var work = unit.AddFunction(new IrFunction("Work", IrType.I16));
    var workEntry = work.CreateBlock("entry");
    var value = workEntry.Append(new IrLoad(IrType.I16, unitGlobal));
    workEntry.Append(new IrRet(value));

    var linked = IrModuleLinker.Link(mainModule, [unit]);
    var mainLoad = linked.FindFunction("main")!.AllInstructions.OfType<IrLoad>().Single();
    var workLoad = linked.FindFunction("Work")!.AllInstructions.OfType<IrLoad>().Single();

    Assert.Multiple(() => {
      Assert.That(linked.Globals, Has.Count.EqualTo(2));
      Assert.That(mainLoad.Pointer, Is.Not.SameAs(workLoad.Pointer));
      Assert.That(mainLoad.Pointer, Is.SameAs(linked.FindGlobal("scratch")), "the main module keeps the unsuffixed name");
      Assert.That(linked.Globals.Select(global => global.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(2));
      Assert.That(IrVerifier.Verify(linked), Is.Empty);
    });
  }

  [Test]
  public void Link_GivenImportedStringLiteralNames_ThenLaterPassesGetFreshNames() {
    var mainModule = new IrModule("MAIN");
    mainModule.AddStringConstant([0x41]);

    var linked = IrModuleLinker.Link(mainModule, Array.Empty<IrModule>());
    var later = linked.AddStringConstant([0x42]);

    Assert.Multiple(() => {
      Assert.That(linked.FindGlobal(".str0"), Is.Not.Null);
      Assert.That(later.Name, Is.EqualTo(".str1"));
      Assert.That(linked.Globals.Select(global => global.Name).Distinct(StringComparer.Ordinal).Count(),
        Is.EqualTo(linked.Globals.Count));
    });
  }

  [Test]
  public void Link_GivenTwoDefinitionsOfOneBasicSymbol_ThenItRejectsTheLink() {
    var mainModule = new IrModule("MAIN");
    mainModule.AddFunction(VoidFunction("Helper"));
    var unit = new IrModule("UNIT");
    unit.AddFunction(VoidFunction("helper"));

    var error = Assert.Throws<IrLinkException>(() => IrModuleLinker.Link(mainModule, [unit]));

    Assert.That(error!.Message, Does.Contain("duplicate IR definition for 'Helper'").IgnoreCase);
  }

  [Test]
  public void Link_GivenMismatchedDeclarationAndDefinition_ThenItRejectsTheLink() {
    var mainModule = new IrModule("MAIN");
    mainModule.AddFunction(new IrFunction("Value", IrType.I16));
    var unit = new IrModule("UNIT");
    var definition = unit.AddFunction(new IrFunction("value", IrType.I32));
    definition.CreateBlock("entry").Append(new IrRet(new IrConstantInt(IrType.I32, 1)));

    var error = Assert.Throws<IrLinkException>(() => IrModuleLinker.Link(mainModule, [unit]));

    Assert.That(error!.Message, Does.Contain("signature mismatch"));
  }

  [Test]
  public void Link_GivenDifferentRuntimeDialects_ThenItRejectsTheLink() {
    var mainModule = new IrModule("MAIN", Dialect.Pb35);
    var unit = new IrModule("UNIT", Dialect.Pb36);

    var error = Assert.Throws<IrLinkException>(() => IrModuleLinker.Link(mainModule, [unit]));

    Assert.That(error!.Message, Does.Contain("runtime dialect"));
  }

  private static IrFunction VoidFunction(string name) {
    var function = new IrFunction(name, IrType.Void);
    function.CreateBlock("entry").Append(new IrRet());
    return function;
  }
}
