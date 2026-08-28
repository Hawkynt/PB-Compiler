' Differential battery 117: a RUNTIME real assigned to an UNSIGNED integer.
'
' BASIC ROUNDS a real on its way into an integer variable, and it rounds into an
' unsigned one exactly as it rounds into a signed one - which is the fact the IR
' had no way to say, so the C and LLVM back ends truncated where the x86-16 one
' rounded. Every value here goes through VAL so the constant folder cannot answer
' it on either side; the .5 rows are the ones a truncating conversion gets wrong.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
DIM d AS DOUBLE
DIM b AS BYTE
DIM w AS WORD
DIM u AS DWORD
DIM i AS INTEGER

d = VAL("0.5")
b = d : w = d : u = d : i = d
PRINT #1, b; w; u; i
d = VAL("1.5")
b = d : w = d : u = d : i = d
PRINT #1, b; w; u; i
d = VAL("2.5")
b = d : w = d : u = d : i = d
PRINT #1, b; w; u; i
d = VAL("3.5")
b = d : w = d : u = d : i = d
PRINT #1, b; w; u; i
d = VAL("2.7")
b = d : w = d : u = d : i = d
PRINT #1, b; w; u; i
d = VAL("2.2")
b = d : w = d : u = d : i = d
PRINT #1, b; w; u; i
d = VAL("253.5")
b = d : w = d : u = d : i = d
PRINT #1, b; w; u; i
d = VAL("254.5")
b = d : w = d : u = d : i = d
PRINT #1, b; w; u; i
d = VAL("1000.5")
w = d : u = d : i = d
PRINT #1, w; u; i
d = VAL("1001.5")
w = d : u = d : i = d
PRINT #1, w; u; i
d = VAL("100000.5")
u = d
PRINT #1, u
d = VAL("100001.5")
u = d
PRINT #1, u
CLOSE #1
