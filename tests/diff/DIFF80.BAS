' Differential battery 80: O16 divide-by-zero guard elimination.
' Under pb36 $OPTIMIZE SPEED an integer \ or MOD whose divisor is a FOR counter with a
' range that excludes zero can never raise Error 11, so the divide-by-zero guard is dropped.
' The guard never fires for a nonzero divisor, so output is identical to pb35 and PBC 3.50.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' divisor range [1,10] excludes 0 -> guard dropped
s% = 0
FOR i% = 1 TO 10
  s% = s% + (100 \ i%)
NEXT i%
PRINT #1, "div"; s%

m% = 0
FOR i% = 1 TO 10
  m% = m% + (100 MOD i%)
NEXT i%
PRINT #1, "mod"; m%

' negative-only divisor range [-10,-1] also excludes 0
n% = 0
FOR i% = -10 TO -1
  n% = n% + (100 \ i%)
NEXT i%
PRINT #1, "neg"; n%

CLOSE #1
END
