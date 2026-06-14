' Differential battery 50: SSA dead-store elimination (O2) composed with SCCP.
' Once SCCP propagates a constant into every use, the assignment that produced
' it is dead and pb36 drops it (pure literal/copy stores only). The output must
' be unchanged - the removed stores were genuinely unobservable.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' dead constant store: PRINT folds to 5, x% = 5 removed
x% = 5
PRINT #1, x%

' dead copy chain: y% and z% both fold to 10, both stores removed
y% = 10
z% = y%
PRINT #1, z%

' the constant still reaches arithmetic correctly after its store is dead
a% = 3
PRINT #1, a% + 100; a% AND 1; (a% = 3)

' a kept store: g% is read unfolded (opaque source), so it survives
DECLARE FUNCTION Src%()
g% = Src%()
PRINT #1, g%

' a constant feeding a loop body, store dead but value still printed each pass
base2% = 42
FOR i% = 1 TO 3
  PRINT #1, base2%
NEXT i%

CLOSE #1
END

FUNCTION Src%()
  Src% = 77
END FUNCTION
