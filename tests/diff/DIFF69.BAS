' Differential battery 69: redundant-load elimination for repeated array reads.
' Under pb36 $OPTIMIZE SPEED a second read of a%(i%) with no intervening write to the
' array or to i% reloads the first read's value instead of re-reading memory; a write to
' the array (any element) or to the index, or a barrier, forces a fresh read. Every
' observed value must be byte-identical to pb35 and to genuine PBC 3.50.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1
DIM a%(10)
DIM b%(10)
FOR i% = 0 TO 10
  a%(i%) = i% * 3 + 1
  b%(i%) = i% * 5
NEXT i%

' two reads, nothing written between -> the second reloads; both are a%(4)=13
i% = 4
c1% = a%(i%)
c2% = a%(i%)
PRINT #1, "reuse"; c1%; c2%

' read, write the SAME element, read -> must observe the new value
q% = a%(i%)
a%(i%) = 99
r% = a%(i%)
PRINT #1, "wsame"; q%; r%

' read, write a DIFFERENT array, read -> a%(4) is untouched, reload is valid
a%(i%) = 13
s% = a%(i%)
b%(2) = 777
t% = a%(i%)
PRINT #1, "wother"; s%; t%

' read, write a different element of the SAME array, read -> conservatively re-read;
' value unchanged because j% <> i%
j% = 6
u% = a%(i%)
a%(j%) = 555
v% = a%(i%)
PRINT #1, "welem"; u%; v%

' the index changes between the two reads -> different elements
i% = 4
w1% = a%(i%)
i% = 6
w2% = a%(i%)
PRINT #1, "idx"; w1%; w2%

CLOSE #1
END
