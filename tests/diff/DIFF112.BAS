' Differential battery 112: O16 type narrowing of 32-bit compares and multiplies.
' When the interval lattice proves BOTH operands of a LONG/DWORD comparison (or of an
' integral 32-bit multiply) fit one 16-bit word, the operation runs on the 16-bit ALU -
' one CMP AX,BX instead of the nine-instruction 32-bit sequence, one MUL BX instead of
' the three-MUL rt_lmul call. The high halves are only sign/zero extension there, and
' int16*int16 / uint16*uint16 always fit the 32-bit result, so the narrowed forms are
' bit-identical to the wide ones. Byte-identical output to pb35 and PBC 3.50.
$ERROR NUMERIC ON
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' all six relops over a range-known LONG counter, at the negative int16 boundary
lo& = 0 : hi& = 0 : eq& = 0 : ne& = 0
FOR i& = -32768 TO -32760
  IF i& < -32764& THEN lo& = lo& + 1
  IF i& >= -32764& THEN hi& = hi& + 1
  IF i& = -32768& THEN eq& = eq& + 1
  IF i& <> -32768& THEN ne& = ne& + 1
NEXT i&
PRINT #1, "neg"; lo&; hi&; eq&; ne&

' ... and at the positive int16 boundary
lo& = 0 : hi& = 0
FOR j& = 32760 TO 32767
  IF j& > 32764& THEN hi& = hi& + 1
  IF j& <= 32764& THEN lo& = lo& + 1
NEXT j&
PRINT #1, "pos"; lo&; hi&

' two range-known LONG variables compared against each other (not just a constant)
c& = 0
FOR k& = 1 TO 20
  m& = 21& - k&
  IF k& < m& THEN c& = c& + 1
NEXT k&
PRINT #1, "pair"; c&

' a range that does NOT fit a word keeps the wide compare - same answer either way
w& = 0
FOR n& = 70000 TO 70004
  IF n& > 70002& THEN w& = w& + 1
NEXT n&
PRINT #1, "wide"; w&

' unsigned 32-bit multiply with both operands provably inside a word: the full
' product must appear, including the upper half (65535 * 65535 = 4294836225)
DIM p AS DWORD, t AS DWORD
t = 0
FOR q& = 65530 TO 65535
  a??? = q&
  b??? = 65535
  p = a??? * b???
  t = t + (p AND 1023)
NEXT q&
PRINT #1, "mul"; p; t

CLOSE #1
END
