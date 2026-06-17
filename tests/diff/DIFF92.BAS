' Differential battery 92: O16 MOD/AND constant-range bounds-check elimination.
' IndexRangeOf now bounds `x AND m` (m a non-negative constant) to [0,m] for any x, and
' `x MOD k` (k constant) to [-(|k|-1),|k|-1], tightened to [0,|k|-1] when x is provably >= 0.
' So masked / modular wrap indexing a(h AND mask), a(i MOD n) drops its Error-9 check under
' $ERROR BOUNDS ON when the bound lies inside the array. Output is identical either way (the
' check would pass), so this must stay byte-identical to genuine PBC 3.50 under pb35 and pb36.
$ERROR BOUNDS ON
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' AND-mask ring buffer: i% AND 7 is always in [0,7]
DIM r%(0 TO 7)
FOR i% = 1 TO 40
  r%(i% AND 7) = i%
NEXT i%
PRINT #1, "and"; r%(0); r%(1); r%(7)

' MOD wrap over a non-negative counter: i% MOD 5 in [0,4]
DIM m%(0 TO 4)
FOR i% = 0 TO 23
  m%(i% MOD 5) = m%(i% MOD 5) + 1
NEXT i%
PRINT #1, "mod"; m%(0); m%(1); m%(2); m%(3); m%(4)

' masked hash-style index into a power-of-two table
DIM h%(0 TO 15)
FOR i% = 1 TO 100
  h%((i% * 7) AND 15) = i%
NEXT i%
PRINT #1, "hash"; h%(0); h%(7); h%(15)

CLOSE #1
END
