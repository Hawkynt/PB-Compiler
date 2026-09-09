using System.Text.Json;

namespace PowerBasic.Compiler.Ir.Profiling;

/// <summary>A stable identity for one basic-block execution counter.</summary>
public readonly record struct IrProfileBlockKey(string FunctionName, int BlockId);

/// <summary>A stable identity for one directed control-flow edge counter.</summary>
public readonly record struct IrProfileEdgeKey(string FunctionName, int SourceBlockId, int TargetBlockId);

/// <summary>
/// A stable identity for one direct-call edge. The ordinal counts only calls in the containing block,
/// so inserting or removing unrelated arithmetic does not churn the profile site.
/// </summary>
public readonly record struct IrProfileCallKey(string CallerName, int BlockId, int CallOrdinal, string CalleeName);

/// <summary>
/// Versioned execution counts consumed by profile-guided IR passes. The profile is intentionally
/// policy-free: it records what ran, while inlining, layout, register allocation and other consumers
/// decide independently what a given count is worth.
/// </summary>
public sealed class IrProfile {

  public const string FormatName = "pb-ir-profile";
  public const int CurrentVersion = 1;

  private readonly Dictionary<IrProfileBlockKey, ulong> _blockCounts = [];
  private readonly Dictionary<IrProfileEdgeKey, ulong> _edgeCounts = [];
  private readonly Dictionary<IrProfileCallKey, ulong> _callCounts = [];

  /// <summary>Raw block counts, keyed by function name plus function-local stable block id.</summary>
  public IReadOnlyDictionary<IrProfileBlockKey, ulong> BlockCounts => this._blockCounts;

  /// <summary>Raw control-flow edge counts.</summary>
  public IReadOnlyDictionary<IrProfileEdgeKey, ulong> EdgeCounts => this._edgeCounts;

  /// <summary>Raw direct-call edge counts.</summary>
  public IReadOnlyDictionary<IrProfileCallKey, ulong> CallCounts => this._callCounts;

  /// <summary>Adds executions to one block counter, saturating instead of silently wrapping.</summary>
  public void AddBlockCount(string functionName, int blockId, ulong count) {
    ValidateFunctionName(functionName, nameof(functionName));
    ValidateBlockId(blockId, nameof(blockId));
    var key = new IrProfileBlockKey(functionName, blockId);
    this._blockCounts[key] = AddSaturating(this._blockCounts.GetValueOrDefault(key), count);
  }

  /// <summary>Adds executions to one directed CFG edge counter.</summary>
  public void AddEdgeCount(string functionName, int sourceBlockId, int targetBlockId, ulong count) {
    ValidateFunctionName(functionName, nameof(functionName));
    ValidateBlockId(sourceBlockId, nameof(sourceBlockId));
    ValidateBlockId(targetBlockId, nameof(targetBlockId));
    var key = new IrProfileEdgeKey(functionName, sourceBlockId, targetBlockId);
    this._edgeCounts[key] = AddSaturating(this._edgeCounts.GetValueOrDefault(key), count);
  }

  /// <summary>Adds executions to one direct-call edge counter.</summary>
  public void AddCallCount(string callerName, int blockId, int callOrdinal, string calleeName, ulong count) {
    ValidateFunctionName(callerName, nameof(callerName));
    ValidateBlockId(blockId, nameof(blockId));
    ValidateBlockId(callOrdinal, nameof(callOrdinal));
    ValidateFunctionName(calleeName, nameof(calleeName));
    var key = new IrProfileCallKey(callerName, blockId, callOrdinal, calleeName);
    this._callCounts[key] = AddSaturating(this._callCounts.GetValueOrDefault(key), count);
  }

  /// <summary>Records executions of a concrete block in the current IR.</summary>
  public void RecordBlock(IrBasicBlock block, ulong count = 1) {
    var (function, blockId) = IdentityOf(block, nameof(block));
    this.AddBlockCount(function.Name, blockId, count);
  }

  /// <summary>Records traversals of a concrete edge in the current IR.</summary>
  public void RecordEdge(IrBasicBlock source, IrBasicBlock target, ulong count = 1) {
    var (function, sourceId) = IdentityOf(source, nameof(source));
    var (targetFunction, targetId) = IdentityOf(target, nameof(target));
    if (!ReferenceEquals(function, targetFunction))
      throw new ArgumentException("profile edges cannot cross function boundaries", nameof(target));
    if (!source.Successors.Any(successor => ReferenceEquals(successor, target)))
      throw new ArgumentException("target is not a successor of source", nameof(target));
    this.AddEdgeCount(function.Name, sourceId, targetId, count);
  }

  /// <summary>Records executions of a direct call site in the current IR.</summary>
  public void RecordCall(IrCall call, ulong count = 1) {
    var key = IdentityOf(call, nameof(call));
    this.AddCallCount(key.CallerName, key.BlockId, key.CallOrdinal, key.CalleeName, count);
  }

  /// <summary>Returns a function's entry count, or zero when the profile has no observation for it.</summary>
  public ulong GetFunctionCount(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    return function.Entry is { } entry ? this.GetBlockCount(entry) : 0;
  }

  /// <summary>Returns a block count, or zero when the site was not observed.</summary>
  public ulong GetBlockCount(IrBasicBlock block) {
    var (function, blockId) = IdentityOf(block, nameof(block));
    return this._blockCounts.GetValueOrDefault(new IrProfileBlockKey(function.Name, blockId));
  }

  /// <summary>Returns a CFG edge count, or zero when the site was not observed.</summary>
  public ulong GetEdgeCount(IrBasicBlock source, IrBasicBlock target) {
    var (function, sourceId) = IdentityOf(source, nameof(source));
    var (targetFunction, targetId) = IdentityOf(target, nameof(target));
    if (!ReferenceEquals(function, targetFunction))
      return 0;
    return this._edgeCounts.GetValueOrDefault(new IrProfileEdgeKey(function.Name, sourceId, targetId));
  }

  /// <summary>Returns a direct-call edge count, or zero when the site was not observed.</summary>
  public ulong GetCallCount(IrCall call) => this._callCounts.GetValueOrDefault(IdentityOf(call, nameof(call)));

  /// <summary>Merges another training run into this one, saturating counters on overflow.</summary>
  public void Merge(IrProfile other) {
    ArgumentNullException.ThrowIfNull(other);
    foreach (var (key, count) in other._blockCounts)
      this.AddBlockCount(key.FunctionName, key.BlockId, count);
    foreach (var (key, count) in other._edgeCounts)
      this.AddEdgeCount(key.FunctionName, key.SourceBlockId, key.TargetBlockId, count);
    foreach (var (key, count) in other._callCounts)
      this.AddCallCount(key.CallerName, key.BlockId, key.CallOrdinal, key.CalleeName, count);
  }

  /// <summary>Writes the deterministic, versioned profile file without closing <paramref name="stream"/>.</summary>
  public void Save(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanWrite)
      throw new ArgumentException("profile stream must be writable", nameof(stream));

    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
    writer.WriteStartObject();
    writer.WriteString("format", FormatName);
    writer.WriteNumber("version", CurrentVersion);

    writer.WriteStartArray("blocks");
    foreach (var (key, count) in this._blockCounts
      .OrderBy(entry => entry.Key.FunctionName, StringComparer.Ordinal)
      .ThenBy(entry => entry.Key.BlockId)) {
      writer.WriteStartObject();
      writer.WriteString("function", key.FunctionName);
      writer.WriteNumber("id", key.BlockId);
      writer.WriteNumber("count", count);
      writer.WriteEndObject();
    }
    writer.WriteEndArray();

    writer.WriteStartArray("edges");
    foreach (var (key, count) in this._edgeCounts
      .OrderBy(entry => entry.Key.FunctionName, StringComparer.Ordinal)
      .ThenBy(entry => entry.Key.SourceBlockId)
      .ThenBy(entry => entry.Key.TargetBlockId)) {
      writer.WriteStartObject();
      writer.WriteString("function", key.FunctionName);
      writer.WriteNumber("source", key.SourceBlockId);
      writer.WriteNumber("target", key.TargetBlockId);
      writer.WriteNumber("count", count);
      writer.WriteEndObject();
    }
    writer.WriteEndArray();

    writer.WriteStartArray("calls");
    foreach (var (key, count) in this._callCounts
      .OrderBy(entry => entry.Key.CallerName, StringComparer.Ordinal)
      .ThenBy(entry => entry.Key.BlockId)
      .ThenBy(entry => entry.Key.CallOrdinal)
      .ThenBy(entry => entry.Key.CalleeName, StringComparer.Ordinal)) {
      writer.WriteStartObject();
      writer.WriteString("caller", key.CallerName);
      writer.WriteNumber("block", key.BlockId);
      writer.WriteNumber("ordinal", key.CallOrdinal);
      writer.WriteString("callee", key.CalleeName);
      writer.WriteNumber("count", count);
      writer.WriteEndObject();
    }
    writer.WriteEndArray();

    writer.WriteEndObject();
    writer.Flush();
  }

  /// <summary>Reads one profile file, rejecting malformed, duplicate and unsupported-version data.</summary>
  public static IrProfile Load(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead)
      throw new ArgumentException("profile stream must be readable", nameof(stream));

    JsonDocument document;
    try {
      document = JsonDocument.Parse(stream);
    } catch (JsonException exception) {
      throw new InvalidDataException("invalid profile JSON", exception);
    }

    using (document) {
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
        throw new InvalidDataException("profile root must be an object");
      if (ReadString(root, "format") != FormatName)
        throw new InvalidDataException("not a PB IR profile");
      var version = ReadInt32(root, "version");
      if (version != CurrentVersion)
        throw new InvalidDataException($"unsupported PB IR profile version {version}");

      var profile = new IrProfile();
      foreach (var entry in ReadArray(root, "blocks")) {
        var key = new IrProfileBlockKey(ReadString(entry, "function"), ReadNonNegativeInt32(entry, "id"));
        var count = ReadUInt64(entry, "count");
        if (!profile._blockCounts.TryAdd(key, count))
          throw new InvalidDataException($"duplicate block profile entry {key.FunctionName}:{key.BlockId}");
      }

      foreach (var entry in ReadArray(root, "edges")) {
        var key = new IrProfileEdgeKey(ReadString(entry, "function"),
          ReadNonNegativeInt32(entry, "source"), ReadNonNegativeInt32(entry, "target"));
        var count = ReadUInt64(entry, "count");
        if (!profile._edgeCounts.TryAdd(key, count))
          throw new InvalidDataException(
            $"duplicate edge profile entry {key.FunctionName}:{key.SourceBlockId}->{key.TargetBlockId}");
      }

      foreach (var entry in ReadArray(root, "calls")) {
        var key = new IrProfileCallKey(ReadString(entry, "caller"), ReadNonNegativeInt32(entry, "block"),
          ReadNonNegativeInt32(entry, "ordinal"), ReadString(entry, "callee"));
        var count = ReadUInt64(entry, "count");
        if (!profile._callCounts.TryAdd(key, count))
          throw new InvalidDataException(
            $"duplicate call profile entry {key.CallerName}:{key.BlockId}/{key.CallOrdinal}->{key.CalleeName}");
      }

      return profile;
    }
  }

  private static (IrFunction Function, int BlockId) IdentityOf(IrBasicBlock block, string paramName) {
    ArgumentNullException.ThrowIfNull(block, paramName);
    var function = block.Parent
      ?? throw new ArgumentException("profiled block is not attached to a function", paramName);
    var blockId = block.ProfileId
      ?? throw new ArgumentException("profiled block has no stable profile id", paramName);
    return (function, blockId);
  }

  private static IrProfileCallKey IdentityOf(IrCall call, string paramName) {
    ArgumentNullException.ThrowIfNull(call, paramName);
    var block = call.Parent
      ?? throw new ArgumentException("profiled call is not attached to a block", paramName);
    var (caller, blockId) = IdentityOf(block, paramName);
    if (call.Callee is not IrFunction callee)
      throw new ArgumentException("only direct calls have a call-edge profile identity", paramName);

    var ordinal = 0;
    foreach (var instruction in block.Instructions) {
      if (instruction is not IrCall)
        continue;
      if (ReferenceEquals(instruction, call))
        return new IrProfileCallKey(caller.Name, blockId, ordinal, callee.Name);
      ++ordinal;
    }

    throw new ArgumentException("profiled call is not present in its parent block", paramName);
  }

  private static ulong AddSaturating(ulong left, ulong right)
    => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

  private static void ValidateFunctionName(string name, string paramName) {
    if (string.IsNullOrWhiteSpace(name))
      throw new ArgumentException("profile function names cannot be empty", paramName);
  }

  private static void ValidateBlockId(int id, string paramName) {
    if (id < 0)
      throw new ArgumentOutOfRangeException(paramName, id, "profile ids cannot be negative");
  }

  private static JsonElement ReadProperty(JsonElement parent, string name) {
    if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
      throw new InvalidDataException($"profile entry is missing '{name}'");
    return value;
  }

  private static string ReadString(JsonElement parent, string name) {
    var value = ReadProperty(parent, name);
    if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
      throw new InvalidDataException($"profile field '{name}' must be a non-empty string");
    return value.GetString()!;
  }

  private static int ReadInt32(JsonElement parent, string name) {
    var value = ReadProperty(parent, name);
    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
      throw new InvalidDataException($"profile field '{name}' must be a 32-bit integer");
    return result;
  }

  private static int ReadNonNegativeInt32(JsonElement parent, string name) {
    var value = ReadInt32(parent, name);
    if (value < 0)
      throw new InvalidDataException($"profile field '{name}' cannot be negative");
    return value;
  }

  private static ulong ReadUInt64(JsonElement parent, string name) {
    var value = ReadProperty(parent, name);
    if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var result))
      throw new InvalidDataException($"profile field '{name}' must be an unsigned 64-bit integer");
    return result;
  }

  private static IEnumerable<JsonElement> ReadArray(JsonElement parent, string name) {
    var value = ReadProperty(parent, name);
    if (value.ValueKind != JsonValueKind.Array)
      throw new InvalidDataException($"profile field '{name}' must be an array");
    return value.EnumerateArray();
  }
}
