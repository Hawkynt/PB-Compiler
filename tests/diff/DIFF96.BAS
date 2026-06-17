' Differential battery 96: O5 DO/WHILE loop accumulator register residency.
' Under pb36 $OPTIMIZE SPEED an SI/DI-clean DO/LOOP keeps its hot INTEGER accumulator in SI for
' the loop (SI is free - no FOR counter), flushing it to the cell on exit. Generalizes register
' residency beyond the FOR-loop shape. All observable results must stay byte-identical to genuine
' PBC 3.50 under pb35 and pb36.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' post-test DO ... LOOP UNTIL: accumulate 1+2+..+10
s% = 0
i% = 1
DO
  s% = s% + i%
  i% = i% + 1
LOOP UNTIL i% > 10
PRINT #1, "post"; s%; i%

' pre-test DO WHILE ... LOOP
t% = 0
j% = 1
DO WHILE j% <= 8
  t% = t% + j% * j%
  j% = j% + 1
LOOP
PRINT #1, "pre"; t%; j%

' DO ... LOOP WHILE (post-test, While sense)
u% = 0
k% = 0
DO
  k% = k% + 1
  u% = u% + k%
LOOP WHILE k% < 6
PRINT #1, "lw"; u%; k%

' accumulator referenced in the loop condition (read from SI), and a clean IF in the body
a% = 0
n% = 0
DO
  n% = n% + 1
  IF n% MOD 2 = 0 THEN
    a% = a% + n%
  END IF
LOOP UNTIL a% >= 20
PRINT #1, "cond"; a%; n%

' EXIT DO out of the loop
b% = 0
m% = 0
DO
  m% = m% + 1
  b% = b% + m%
  IF b% > 15 THEN EXIT DO
LOOP
PRINT #1, "exit"; b%; m%

' two accumulators: SI and DI both free in a DO loop (sum in SI, product-ish in DI)
sum% = 0
prod% = 1
g% = 1
DO
  sum% = sum% + g%
  prod% = prod% + g% * 2
  g% = g% + 1
LOOP UNTIL g% > 7
PRINT #1, "two"; sum%; prod%; g%

CLOSE #1
END
