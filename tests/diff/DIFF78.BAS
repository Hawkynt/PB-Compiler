' Differential battery 78: O16 general branch folding via FOR-counter ranges.
' Under pb36 $OPTIMIZE SPEED a comparison of a FOR counter (or counter +/- const) against a
' constant whose result is invariant over the counter's [From,To] range folds to the constant
' boolean (-1/0). The folded value must equal the runtime comparison, so output is identical
' to pb35 and genuine PBC 3.50. Fires in ordinary code (no $ERROR needed).
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' i in [3,7]: i<10 always true, i>100 always false (printed booleans)
FOR i% = 3 TO 7
  PRINT #1, "b"; (i% < 10); (i% > 100); (i% >= 3); (i% <= 7); (i% > 2)
NEXT i%

' folded always-true guard sums all; a varying guard hits once
s1% = 0 : s2% = 0
FOR i% = 3 TO 7
  IF i% >= 3 THEN s1% = s1% + i%
  IF i% = 5 THEN s2% = s2% + 100
NEXT i%
PRINT #1, "sums"; s1%; s2%

' affine always-true: (i+2) > 4 over i in [3,7] -> i+2 in [5,9], all > 4
t% = 0
FOR i% = 3 TO 7
  IF i% + 2 > 4 THEN t% = t% + 1
NEXT i%
PRINT #1, "aff"; t%

' affine varying: (i+2) > 8 over [5,9] -> only i+2=9 -> not folded, still correct
v% = 0
FOR i% = 3 TO 7
  IF i% + 2 > 8 THEN v% = v% + 1
NEXT i%
PRINT #1, "var"; v%

' const on the left: 10 > i over [3,7] -> always true (swapped to i < 10)
u% = 0
FOR i% = 3 TO 7
  IF 10 > i% THEN u% = u% + 1
NEXT i%
PRINT #1, "left"; u%

' equality that varies and a not-equal that is always true (5 outside [10,14])
e% = 0 : ne% = 0
FOR i% = 10 TO 14
  IF i% = 12 THEN e% = e% + 1
  IF i% <> 5 THEN ne% = ne% + 1
NEXT i%
PRINT #1, "eq"; e%; ne%

CLOSE #1
END
