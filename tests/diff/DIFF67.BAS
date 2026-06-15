' Differential battery 67: broader GVN - flow a common subexpression PAST an IF merge.
' Under pb36 $OPTIMIZE SPEED a value computed before the IF and reused after it is
' kept live across the merge when no branch writes its inputs (a call-free, flat,
' nested-control-free branch). When a branch DOES write an input the value must be
' recomputed. Output must be byte-identical to pb35 and to genuine PBC 3.50.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

a% = 6 : b% = 7 : c% = 5

' (a%*b%+c%) computed before the IF; the branches touch only q%, so it survives
p% = a% * b% + c%
IF p% > 40 THEN
  q% = 1
  PRINT #1, "hi"; q%
ELSE
  q% = 2
  PRINT #1, "lo"; q%
END IF
' reuse a%*b%+c% after the merge - inputs untouched, retained value still valid
r% = a% * b% + c%
PRINT #1, "reuse"; r%

' contrast: a branch overwrites an input, so the reuse must recompute
s% = a% * b%
IF b% > 3 THEN
  a% = 100
END IF
t% = a% * b%
PRINT #1, "input"; s%; t%

' nested control inside the branch forces the conservative clear; still correct
d% = a% + b%
IF a% > 50 THEN
  IF b% < 10 THEN q% = 3
END IF
e% = a% + b%
PRINT #1, "nest"; d%; e%

' modular *const subtree (y%*320): a branch that overwrites y% must invalidate it,
' a branch that leaves y% alone may keep it live across the merge
y% = 4 : z% = 9
m% = y% * 320 + z%
IF z% > 5 THEN
  y% = 11
END IF
n% = y% * 320 + z%
PRINT #1, "modw"; m%; n%

y% = 4 : z% = 9
g% = y% * 320 + z%
IF z% > 5 THEN
  z% = 2
END IF
h% = y% * 320 + z%
PRINT #1, "modk"; g%; h%

CLOSE #1
END
