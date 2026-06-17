' Differential battery 91: O5 register residency over a clean IF body.
' Under pb36 $OPTIMIZE SPEED a FOR loop whose body is a clean IF - an SI-clean condition with
' scalar-assign arms - now keeps its counter in SI (and an accumulator in DI). The branch
' touches no index register, so SI/DI survive across it. Every observable result must stay
' byte-identical to genuine PBC 3.50 under pb35 and pb36.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' single-line IF conditional accumulate (counter in SI, s% the conditionally-written DI acc)
s% = 0
FOR i% = 1 TO 20
  IF i% > 10 THEN s% = s% + i%
NEXT i%
PRINT #1, "big"; s%

' IF / ELSE both arms write the accumulator
e% = 0
o% = 0
FOR i% = 1 TO 15
  IF (i% AND 1) = 0 THEN
    e% = e% + i%
  ELSE
    o% = o% + i%
  END IF
NEXT i%
PRINT #1, "evenodd"; e%; o%

' ELSEIF chain
c1% = 0
c2% = 0
c3% = 0
FOR i% = 1 TO 30
  IF i% MOD 3 = 0 THEN
    c1% = c1% + 1
  ELSEIF i% MOD 3 = 1 THEN
    c2% = c2% + 1
  ELSE
    c3% = c3% + 1
  END IF
NEXT i%
PRINT #1, "mod3"; c1%; c2%; c3%

' nested IF inside the loop body
n% = 0
FOR i% = 1 TO 12
  IF i% > 3 THEN
    IF i% < 9 THEN
      n% = n% + i%
    END IF
  END IF
NEXT i%
PRINT #1, "window"; n%

CLOSE #1
END
