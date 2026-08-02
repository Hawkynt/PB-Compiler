' Differential battery 113: O13 32-bit LONG arithmetic (verified against genuine PBC 3.50).
' The oracle-confirmed model: PB promotes integer +,-,* to floating point only where that
' keeps the result exact - a 2-byte sum fits SINGLE, and a 4-byte PRODUCT needs the FPU
' (over 32 bits). But a 4-byte ADD or SUBTRACT runs in the native 32-bit ALU and WRAPS:
' PBC 3.50 prints 2147483000+1000 as -2147483296 (2147484000 mod 2^32), not the x87
' sentinel a Double store would give. So the optimizer's modular 32-bit path for a LONG
' +/- is not merely faster - it is the faithful semantics; a promoted FISTP would diverge.
' (LONG-MULTIPLY overflow is a separate, unsettled quirk - genuine PBC halts on it without a
'  handler - so those cases are covered elsewhere, not here; see docs/QUIRKS.md.)
$OPTIMIZE SPEED
DECLARE SUB Opaque(v&)
DIM a AS LONG, b AS LONG, r AS LONG, acc AS LONG
DIM i AS INTEGER, j AS INTEGER
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' narrow leaves widened into a LONG - provably in range, so no guard is needed
i = 300 : j = -7
Opaque a
r = i * j : PRINT #1, "i16mul"; r
r = i + j : PRINT #1, "i16add"; r
r = i * j + i : PRINT #1, "i16tree"; r
i = 32000 : j = 32000
r = i * j : PRINT #1, "i16big"; r

' full-width operands: exact when it fits, the x87 sentinel when it does not
a = 100000 : b = 7
Opaque a : Opaque b
r = a + b : PRINT #1, "add"; r
r = a - b : PRINT #1, "sub"; r
r = a * b : PRINT #1, "mul"; r
r = -a : PRINT #1, "neg"; r

a = 2147483000 : b = 1000
Opaque a : Opaque b
r = a + b : PRINT #1, "posovf"; r
a = -2147483000
Opaque a
r = a - b : PRINT #1, "negovf"; r
a = 2147483646 : b = 1
Opaque a : Opaque b
r = a + b : PRINT #1, "edge"; r
r = a + b + b : PRINT #1, "chain"; r

' the same shapes with COMPILE-TIME operands: the folder must reproduce the store's
' saturation too, not wrap - SCCP sees through these to a single constant
a = 2147483647
r = a + a : PRINT #1, "foldadd"; r
a = 2147483647
r = a + 1 : PRINT #1, "foldedge"; r
i = 300 : j = 300
r = i * j : PRINT #1, "foldnarrow"; r

' an accumulator loop - the shape this pass exists for
acc = 0
FOR i = 1 TO 50
  acc = acc + i
NEXT i
PRINT #1, "accum"; acc

CLOSE #1
END

SUB Opaque(v&)
END SUB
