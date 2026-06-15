' Differential battery 71: C1 ($CPU 80386) 32-bit integer divide/modulo by a constant.
' Under pb36 $OPTIMIZE SPEED + $CPU 80386 a LONG \ or MOD by a compile-time-constant
' divisor of magnitude >= 2 uses the exact hardware IDIV (truncate toward zero, remainder
' takes the dividend's sign - exactly PB semantics) instead of the LongDiv/LongMod runtime
' call. The dividends flow through SUB parameters (differing call args) so they stay runtime
' values and genuinely exercise the IDIV path. Byte-identical to the runtime path and PBC.
$CPU 80386
$OPTIMIZE SPEED
DECLARE SUB report(BYVAL tag AS STRING, BYVAL n AS LONG)
OPEN "RESULT.TXT" FOR OUTPUT AS #1

report "a", 100000007
report "b", -100000007
report "c", -7
report "d", -8
report "e", 100
report "f", -100
report "g", -9
report "h", 2000000000
report "i", -2000000000
report "j", -1

CLOSE #1
END

SUB report(BYVAL tag AS STRING, BYVAL n AS LONG)
  ' positive and negative constant divisors of magnitude >= 2 (the 386 IDIV path);
  ' \ truncates toward zero, MOD takes the dividend's sign
  PRINT #1, tag; n \ 7; n MOD 7; n \ -7; n MOD -7; n \ 4; n MOD 4; n \ 2; n MOD 2
END SUB
