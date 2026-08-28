' A RUNTIME real assigned to an UNSIGNED integer, at the boundaries where rounding
' and truncation disagree.
'
' BASIC ROUNDS a real on its way into an integer variable, and it rounds into a
' BYTE, WORD or DWORD exactly as it rounds into an INTEGER - nearest, ties to even.
' Genuine PBC 3.5 answers 3.5 with 4 for all four types (tests/diff/DIFF115.BAS is
' the oracle gate). The IR had no way to SAY that for the unsigned case, so the
' x86-16 back end's FISTP rounded while a C cast and LLVM's fptoui truncated - a
' disagreement no routed-vs-direct comparison here could see, because both sides of
' one are x86-16.
'
' Every value goes through VAL, so the constant folder cannot answer it and the
' conversion is the one the back end emits rather than one the front end did.
DIM d AS DOUBLE
DIM b AS BYTE
DIM w AS WORD
DIM u AS DWORD
DIM i AS INTEGER

d = VAL("0.5")
b = d : w = d : u = d : i = d
PRINT b; w; u; i
d = VAL("1.5")
b = d : w = d : u = d : i = d
PRINT b; w; u; i
d = VAL("2.5")
b = d : w = d : u = d : i = d
PRINT b; w; u; i
d = VAL("3.5")
b = d : w = d : u = d : i = d
PRINT b; w; u; i
d = VAL("2.7")
b = d : w = d : u = d : i = d
PRINT b; w; u; i
d = VAL("2.2")
b = d : w = d : u = d : i = d
PRINT b; w; u; i
d = VAL("253.5")
b = d : w = d : u = d : i = d
PRINT b; w; u; i
d = VAL("254.5")
b = d : w = d : u = d : i = d
PRINT b; w; u; i
d = VAL("1000.5")
w = d : u = d : i = d
PRINT w; u; i
d = VAL("1001.5")
w = d : u = d : i = d
PRINT w; u; i
d = VAL("100000.5")
u = d
PRINT u
d = VAL("100001.5")
u = d
PRINT u
