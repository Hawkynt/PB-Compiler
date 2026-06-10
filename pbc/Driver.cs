namespace PowerBasic.Compiler.Cli;

/// <summary>Command-line front end for the PowerBASIC 3.5 compiler.</summary>
public static class Driver {

  public static int Run(string[] args, TextWriter stdout, TextWriter stderr) {
    if (args.Length == 0 || args is ["-h" or "--help" or "/?"]) {
      PrintUsage(stdout);
      return args.Length == 0 ? 1 : 0;
    }

    stderr.WriteLine("pbc: not yet implemented");
    return 1;
  }

  private static void PrintUsage(TextWriter w) {
    w.WriteLine("PB-Compiler - PowerBASIC 3.5 compatible compiler for 16-bit real-mode DOS");
    w.WriteLine();
    w.WriteLine("Usage: pbc [options] <source.BAS>");
    w.WriteLine("       pbc lib build <out.PBL> <unit.PBU>...");
    w.WriteLine();
    w.WriteLine("Options:");
    w.WriteLine("  -G386        allow 80386 instructions ($CPU 80386)");
    w.WriteLine("  -O <file>    output file name");
    w.WriteLine("  -h, --help   show this help");
  }
}
