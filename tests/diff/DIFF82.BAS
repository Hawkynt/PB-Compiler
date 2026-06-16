' Differential battery 82: O16 derived-variable range (first step toward the full lattice).
' When a loop body's FIRST statement assigns a scalar-INTEGER variable a range-known counter
' expression (j = i+1, k = i*2) and the variable is never modified later, it carries that
' range for the body - so a(j) drops its bounds check and k < C folds. Byte-identical to PBC.
$ERROR BOUNDS ON
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1
DIM a%(0 TO 20)

' j = i+1 in [1,10] used as an array index -> a(j) bounds check elided
FOR i% = 0 TO 9
  j% = i% + 1
  a%(j%) = j% * 2
NEXT i%
s% = 0
FOR i% = 0 TO 9
  j% = i% + 1
  s% = s% + a%(j%)
NEXT i%
PRINT #1, "derived"; s%; a%(1); a%(10)

' derived var in a comparison fold: k = i*2 in [2,20], k < 100 always true
u% = 0
FOR i% = 1 TO 10
  k% = i% * 2
  IF k% < 100 THEN u% = u% + 1
NEXT i%
PRINT #1, "cmp"; u%

' a leading run of two derived vars j (=i+1, [1,10]) and k (=i*2, [0,18]), both indexing a()
DIM b%(0 TO 20)
FOR i% = 0 TO 9
  j% = i% + 1
  k% = i% * 2
  b%(j%) = j%
  b%(k%) = b%(k%) + k%
NEXT i%
t% = 0
FOR i% = 0 TO 20
  t% = t% + b%(i%)
NEXT i%
PRINT #1, "chain"; t%

CLOSE #1
END
