using System.Text;

namespace PowerBasic.Compiler.Tests.Exec;

/// <summary>
/// The KERNAL's channel I/O and a 1541 on device 8, modelled closely enough for a program's files:
/// <c>SETLFS</c>, <c>SETNAM</c>, <c>OPEN</c>, <c>CLOSE</c>, <c>CHKIN</c>, <c>CHKOUT</c>,
/// <c>CLRCHN</c>, <c>CHRIN</c>, <c>CHROUT</c> and <c>READST</c>, over a disk that is a dictionary of
/// sequential files by their PETSCII name.
///
/// <para>
/// The drive behaves as CBM DOS does, not as a program might hope: <c>OPEN</c> itself succeeds even
/// for a file that is not there, and the failure is in the status the command channel (15) reads
/// back - <c>62,FILE NOT FOUND</c>, <c>63,FILE EXISTS</c>. <c>,S,R</c>, <c>,S,W</c> and
/// <c>,S,A</c> read, write and append; <c>@0:</c> in front of a write replaces; <c>S0:name</c>
/// printed to channel 15 scratches, when the channel is released. The last byte of a file reads with
/// status <c>$40</c>, end of file; a read with nothing left gets a carriage return and <c>$42</c>.
/// A file being written reaches the disk when it is closed, and not before: a 1541 file never
/// closed is a splat file, which nothing can read, so a program must close what it writes.
/// </para>
/// </summary>
public sealed partial class Cpu6502 {

  private const ushort Readst = 0xFFB7, Setlfs = 0xFFBA, Setnam = 0xFFBD, Open = 0xFFC0, Close = 0xFFC3,
    Chkin = 0xFFC6, Chkout = 0xFFC9, Clrchn = 0xFFCC;
  private const int Drive = 8, CommandChannel = 15;

  private sealed class Channel {
    public required int Secondary;
    public string? Name;
    public List<byte>? Data;
    public int Position;
    public bool Writing;
  }

  private Dictionary<string, List<byte>> _disk = [];
  private readonly Dictionary<int, Channel> _channels = [];
  private int _pendingFile, _pendingDevice, _pendingSecondary;
  private byte[] _pendingName = [];
  private int _inputFile, _outputFile;
  private byte _status;
  private string _driveStatus = "00, OK,00,00";
  private int _driveStatusAt;
  private readonly List<byte> _command = [];

  /// <summary>Answers a call into the KERNAL at the program counter, if it is one of ours; false for anything else.</summary>
  private bool Kernal() {
    switch (this._pc) {
      case Chrout: this.CharacterOut(this._a); break;
      case Chrin: this._a = this.CharacterIn(); break;
      // GETIN: the next key typed, or 0 when nothing is waiting - a test's input, a key at a time
      // PLOT: the cursor moves, which a teletype stream does not record
      case 0xFFF0: this._carry = false; break;
      case 0xFFE4: this._a = this._keyboardAt < this._keyboard.Length ? this.NextInput() : (byte)0; this.Nz(this._a); this._carry = false; break;
      case Readst: this._a = this._status; this.Nz(this._a); break;
      case Setlfs: (this._pendingFile, this._pendingDevice, this._pendingSecondary) = (this._a, this._x, this._y); break;
      case Setnam: this._pendingName = this._memory.AsSpan(this.Word2(this._x, this._y), this._a).ToArray(); break;
      case Open: this.OpenFile(); break;
      case Close: this.CloseFile(this._a); break;
      case Chkin: this._carry = !this._channels.ContainsKey(this._x); if (!this._carry) this._inputFile = this._x; this._status = 0; break;
      case Chkout: this._carry = !this._channels.ContainsKey(this._x); if (!this._carry) this._outputFile = this._x; this._status = 0; break;
      case Clrchn: this.ReleaseChannels(); break;
      default: return false;
    }
    this.Return();
    return true;
  }

  private int Word2(byte low, byte high) => low | (high << 8);

  private void OpenFile() {
    this._carry = false;
    if (this._pendingDevice != Drive) {
      this._carry = true;                     // 5, device not present
      this._a = 5;
      return;
    }
    var channel = new Channel { Secondary = this._pendingSecondary };
    this._channels[this._pendingFile] = channel;
    this._status = 0;
    if (this._pendingSecondary == CommandChannel) {
      if (this._pendingName.Length > 0)
        this.Execute(this._pendingName);
      return;
    }
    var text = Encoding.Latin1.GetString(this._pendingName);
    var replace = text.StartsWith('@');
    if (replace)
      text = text[1..];
    if (text.Length > 1 && text[1] == ':')
      text = text[2..];
    else if (text.StartsWith(':'))
      text = text[1..];
    // "$" on secondary address 0: the directory, as the BASIC listing LOAD "$",8 reads
    if (text.StartsWith('$') && this._pendingSecondary == 0) {
      channel.Name = "$";
      channel.Data = this.DirectoryListing();
      this.Report("00, OK,00,00");
      return;
    }
    var parts = text.Split(',');
    var name = parts[0];
    var mode = parts.Skip(1).FirstOrDefault(part => part is "R" or "W" or "A") ?? "R";
    channel.Name = name;
    switch (mode) {
      case "R" when this._disk.TryGetValue(name, out var existing):
        channel.Data = existing;
        break;
      case "A" when this._disk.TryGetValue(name, out var existing):
        channel.Data = [.. existing];
        channel.Writing = true;
        break;
      case "W" when !replace && this._disk.ContainsKey(name):
        this.Report("63,FILE EXISTS,00,00");
        return;
      case "W":
        channel.Data = [];
        channel.Writing = true;
        break;
      default:
        this.Report("62, FILE NOT FOUND,00,00");
        return;
    }
    this.Report("00, OK,00,00");
  }

  /// <summary>
  /// The 1541's directory as a BASIC program: load address $0401, a header line naming the disk, a
  /// line per file - its size in blocks as the line number, its quoted name and its type - and the
  /// blocks free. Each line is a link, a number, the text and a zero; two zero bytes end it.
  /// </summary>
  private List<byte> DirectoryListing() {
    var listing = new List<byte> { 0x01, 0x04 };
    void Line(int number, string text) {
      listing.AddRange([0x01, 0x01, (byte)number, (byte)(number >> 8)]);
      listing.AddRange(Encoding.Latin1.GetBytes(text));
      listing.Add(0);
    }
    Line(0, "\u0012\"PBC TEST DISK    \" 00 2A");
    foreach (var (name, data) in this._disk.OrderBy(entry => entry.Key, StringComparer.Ordinal)) {
      var blocks = Math.Max(1, (data.Count + 253) / 254);
      var quoted = $"\"{name}\"";
      Line(blocks, $"{new string(' ', blocks < 10 ? 3 : blocks < 100 ? 2 : 1)}{quoted.PadRight(18)} SEQ");
    }
    Line(664, "BLOCKS FREE.");
    listing.AddRange([0, 0]);
    return listing;
  }

  private void Report(string status) => (this._driveStatus, this._driveStatusAt) = (status, 0);

  /// <summary>A DOS command, from <c>OPEN</c>'s name or printed to channel 15: scratch (<c>S0:name</c>) and rename (<c>R0:new=old</c>).</summary>
  private void Execute(IReadOnlyList<byte> command) {
    var text = Encoding.Latin1.GetString(command.ToArray()).TrimEnd('\r');
    if (text.Length == 0)
      return;
    if (text[0] == 'R') {
      var names = text[(text.IndexOf(':') + 1)..].Split('=');
      if (names.Length != 2 || !this._disk.TryGetValue(names[1], out var data)) {
        this.Report("62,FILE NOT FOUND,00,00");
        return;
      }
      if (this._disk.ContainsKey(names[0])) {
        this.Report("63,FILE EXISTS,00,00");
        return;
      }
      this._disk.Remove(names[1]);
      this._disk[names[0]] = data;
      this.Report("00, OK,00,00");
      return;
    }
    if (text[0] != 'S') {
      this.Report("31,SYNTAX ERROR,00,00");
      return;
    }
    var colon = text.IndexOf(':');
    var name = colon < 0 ? "" : text[(colon + 1)..];
    var removed = this._disk.Remove(name) ? 1 : 0;
    this.Report($"01, FILES SCRATCHED,{removed:00},00");
  }

  private void CloseFile(int file) {
    if (this._channels.Remove(file, out var channel) && channel is { Writing: true, Name: { } name, Data: { } data })
      this._disk[name] = data;
    if (this._inputFile == file)
      this._inputFile = 0;
    if (this._outputFile == file)
      this._outputFile = 0;
    this._carry = false;
  }

  private void ReleaseChannels() {
    if (this._command.Count > 0) {
      this.Execute(this._command);
      this._command.Clear();
    }
    (this._inputFile, this._outputFile) = (0, 0);
  }

  private void CharacterOut(byte character) {
    if (this._outputFile == 0) {
      this.Capture(character);
      return;
    }
    var channel = this._channels[this._outputFile];
    this._carry = false;
    if (channel.Secondary == CommandChannel)
      this._command.Add(character);
    else if (channel.Writing && channel.Data is { } data)
      data.Add(character);
    else
      this._status = 0x80;                    // not a file open for writing
  }

  private byte CharacterIn() {
    if (this._inputFile == 0)
      return this.NextInput();
    var channel = this._channels[this._inputFile];
    this._carry = false;
    if (channel.Secondary == CommandChannel) {
      var status = this._driveStatus + "\r";
      var character = (byte)status[this._driveStatusAt++];
      if (this._driveStatusAt == status.Length) {
        this._status = 0x40;
        this.Report("00, OK,00,00");
      }
      return character;
    }
    if (channel.Writing || channel.Data is not { } data || channel.Position >= data.Count) {
      this._status = 0x42;
      return 13;
    }
    var next = data[channel.Position++];
    this._status = channel.Position == data.Count ? (byte)0x40 : (byte)0;
    return next;
  }
}
