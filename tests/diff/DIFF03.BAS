' Differential battery 3: control flow, procedures, arrays vs genuine PBC 3.5.
DECLARE FUNCTION Fact&(BYVAL n AS INTEGER)
DIM i AS INTEGER, total AS LONG
DIM grid(1 TO 3, 1 TO 3) AS INTEGER
OPEN "RESULT.TXT" FOR OUTPUT AS #1

total = 0
FOR i = 1 TO 10
    total = total + i
NEXT i
PRINT #1, total

i = 0
DO WHILE i < 5
    INCR i
LOOP
PRINT #1, i

SELECT CASE i
    CASE 1 TO 4
        PRINT #1, "LOW"
    CASE 5
        PRINT #1, "FIVE"
    CASE ELSE
        PRINT #1, "HIGH"
END SELECT

FOR i = 1 TO 3
    grid(i, i) = i * 11
NEXT i
PRINT #1, grid(1, 1) + grid(2, 2) + grid(3, 3)

PRINT #1, Fact&(7)

GOSUB tail
PRINT #1, "AFTER"
CLOSE #1
END

tail:
PRINT #1, "SUB"
RETURN

FUNCTION Fact&(BYVAL n AS INTEGER)
    IF n <= 1 THEN
        Fact& = 1
    ELSE
        Fact& = n * Fact&(n - 1)
    END IF
END FUNCTION
