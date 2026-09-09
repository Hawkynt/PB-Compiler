using System.Text;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Profiling;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0268: stable profile identities and profile count persistence.</summary>
[TestFixture]
public sealed class IrProfileTests {

  [Test]
  public void BlockIdentity_GivenANewEntryIsInserted_ThenExistingIdsStayStable() {
    var function = new IrFunction("main", IrType.Void);
    var oldEntry = function.CreateBlock("entry");
    var exit = function.CreateBlock("exit");
    var oldEntryId = oldEntry.ProfileId;
    var exitId = exit.ProfileId;

    var newEntry = function.CreateEntryBlock("prologue");

    Assert.Multiple(() => {
      Assert.That(oldEntryId, Is.EqualTo(0));
      Assert.That(exitId, Is.EqualTo(1));
      Assert.That(newEntry.ProfileId, Is.EqualTo(2));
      Assert.That(oldEntry.ProfileId, Is.EqualTo(oldEntryId));
      Assert.That(exit.ProfileId, Is.EqualTo(exitId));
      Assert.That(function.Blocks, Is.EqualTo(new[] { newEntry, oldEntry, exit }));
    });
  }

  [Test]
  public void Counts_GivenConcreteBlocksAndAnEdge_ThenTheyCanBeQueriedByIrObject() {
    var function = new IrFunction("main", IrType.Void);
    var entry = function.CreateBlock("entry");
    var exit = function.CreateBlock("exit");
    entry.Append(new IrBr(exit));
    exit.Append(new IrRet());
    var profile = new IrProfile();

    profile.RecordBlock(entry, 7);
    profile.RecordBlock(exit, 5);
    profile.RecordEdge(entry, exit, 5);

    Assert.Multiple(() => {
      Assert.That(profile.GetFunctionCount(function), Is.EqualTo(7));
      Assert.That(profile.GetBlockCount(exit), Is.EqualTo(5));
      Assert.That(profile.GetEdgeCount(entry, exit), Is.EqualTo(5));
    });
  }

  [Test]
  public void CallIdentity_GivenTwoCallsInOneBlock_ThenTheirOrdinalsDoNotCollide() {
    var callee = new IrFunction("worker", IrType.Void);
    var caller = new IrFunction("main", IrType.Void);
    var entry = caller.CreateBlock("entry");
    var first = entry.Append(new IrCall(IrType.Void, callee, []));
    var second = entry.Append(new IrCall(IrType.Void, callee, []));
    entry.Append(new IrRet());
    var profile = new IrProfile();

    profile.RecordCall(second, 11);

    Assert.Multiple(() => {
      Assert.That(profile.GetCallCount(first), Is.Zero);
      Assert.That(profile.GetCallCount(second), Is.EqualTo(11));
      Assert.That(profile.CallCounts.Keys.Single().CallOrdinal, Is.EqualTo(1));
    });
  }

  [Test]
  public void Persistence_GivenAllCounterKinds_ThenRoundTripIsLosslessAndDeterministic() {
    var profile = new IrProfile();
    profile.AddBlockCount("z", 2, 3);
    profile.AddBlockCount("a", 0, 9);
    profile.AddEdgeCount("a", 0, 1, 7);
    profile.AddCallCount("a", 0, 1, "worker", 6);
    using var first = new MemoryStream();
    using var second = new MemoryStream();

    profile.Save(first);
    profile.Save(second);
    first.Position = 0;
    var loaded = IrProfile.Load(first);

    Assert.Multiple(() => {
      Assert.That(loaded.BlockCounts[new IrProfileBlockKey("a", 0)], Is.EqualTo(9));
      Assert.That(loaded.EdgeCounts[new IrProfileEdgeKey("a", 0, 1)], Is.EqualTo(7));
      Assert.That(loaded.CallCounts[new IrProfileCallKey("a", 0, 1, "worker")], Is.EqualTo(6));
      Assert.That(second.ToArray(), Is.EqualTo(first.ToArray()));
    });
  }

  [Test]
  public void Merge_GivenA64BitCounterWouldOverflow_ThenTheCountSaturates() {
    var profile = new IrProfile();
    var laterRun = new IrProfile();
    profile.AddBlockCount("main", 0, ulong.MaxValue - 2);
    laterRun.AddBlockCount("main", 0, 7);

    profile.Merge(laterRun);

    Assert.That(profile.BlockCounts[new IrProfileBlockKey("main", 0)], Is.EqualTo(ulong.MaxValue));
  }

  [Test]
  public void Load_GivenAnUnsupportedVersion_ThenItIsRejected() {
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
      "{\"format\":\"pb-ir-profile\",\"version\":2,\"blocks\":[],\"edges\":[],\"calls\":[]}"));

    var action = () => IrProfile.Load(stream);

    Assert.That(action, Throws.TypeOf<InvalidDataException>());
  }
}
