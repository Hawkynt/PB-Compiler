' Differential battery 49: SSA + SCCP across loops (cyclic CFG). pb36 now builds
' SSA for FOR and pre-test/infinite DO loops; a loop-invariant constant folds
' inside the body, but anything the loop mutates (accumulators, the counter,
' read-before-write values) must stay unfolded - the cyclic constant lattice has
' to converge to the values the program really computes, byte-identical to PBC.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' loop-invariant constant folds inside the loop (printed each pass)
bse% = 100
FOR m% = 1 TO 3
  PRINT #1, bse%
NEXT m%

' accumulator is loop-mutated - must not fold
t% = 0
k% = 7
FOR i% = 1 TO 5
  t% = t% + k%
NEXT i%
PRINT #1, t%; k%; i%

' DO WHILE with a mutated counter
c% = 0
n% = 3
DO WHILE c% < n%
  c% = c% + 1
LOOP
PRINT #1, c%; n%

' read-before-write inside a loop: the value differs each pass (0,1,2)
v% = 0
FOR p% = 1 TO 3
  PRINT #1, v%
  v% = p%
NEXT p%

' nested loops, inner invariant constant
q% = 9
s% = 0
FOR a% = 1 TO 2
  FOR b% = 1 TO 2
    s% = s% + q%
  NEXT b%
NEXT a%
PRINT #1, s%; q%

CLOSE #1
END
