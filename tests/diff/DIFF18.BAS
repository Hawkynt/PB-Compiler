' Differential battery 18: $ERROR defaults (all checking OFF) and the
' $OPTIMIZE SPEED code shapes. With no $ERROR metastatement, BYTE FOR
' counters wrap silently (QUIRK 2.28) and INTEGER arithmetic wraps.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' ---- default: BYTE counter wraps, loop never exits on its own (QUIRK 2.28) --
c% = 0
FOR b? = 1 TO 255
  INCR c%
  IF c% > 300 THEN EXIT FOR    ' wrapped past 255 and kept going
NEXT b?
PRINT #1, c%; b?

' ---- default: INTEGER overflow wraps silently ------------------------------
a% = 30000
o% = a% + a%
PRINT #1, o%

' ---- default: out-of-range index is NOT trapped (no bounds checks) ----------
DIM ar%(1 TO 5)
ar%(2) = 22
PRINT #1, ar%(2)

' ---- $OPTIMIZE SPEED shapes stay observably identical -----------------------
x% = 7
y% = x% * 8          ' power-of-two multiply (SPEED inlines as shifts)
PRINT #1, y%
n% = 5
n% = n% + 3          ' add-into-cell (SPEED folds into one ALU op)
n% = n% - 2
PRINT #1, n%
z% = x% * 256
PRINT #1, z%

' descending unsigned counter underflows the same way (QUIRK 2.29)
c% = 0
FOR w?? = 2 TO 0 STEP -1
  INCR c%
  IF c% > 5 THEN EXIT FOR     ' wrapped below 0 to 65535 and kept going
NEXT w??
PRINT #1, c%

CLOSE #1
END
