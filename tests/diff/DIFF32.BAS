' Differential battery 32: $CPU 80386 - the pb36 32-bit IDIV/DIV long-divide
' fast paths must match the genuine compiler (and the 16-bit loop) bit for bit,
' including the fault edges that fall back to the loop.
$CPU 80386
OPEN "RESULT.TXT" FOR OUTPUT AS #1
a& = 100000
b& = -7
PRINT #1, a& \ b&; a& MOD b&; b& \ 13; b& MOD 13
c& = -2147483647 - 1
PRINT #1, c& \ 2; c& MOD 2; c& MOD -1
PRINT #1, c& \ 3; c& MOD 3
d& = 2147483647
PRINT #1, d& \ -3; d& MOD -3; d& \ 65536; d& MOD 65536
e??? = 4000000001
PRINT #1, e??? \ 7; e??? MOD 7
f& = 360000
g& = 3
PRINT #1, f& * g&; f& \ g&; f& MOD g&
h& = 50000
k& = 90000
PRINT #1, h& * k&; h& * -7; -h& * -k&; 65536 * 65536
m& = -2147483647 - 1
PRINT #1, m& * 1; m& * -1; 46341 * 46341
p% = 7
q% = -3
PRINT #1, (p% > q%); (p% < q%); (p% = 7); (p% <> 7); (p% >= 8); (q% <= -3)
r?? = 60000
s?? = 70
PRINT #1, (r?? > s??); (r?? < s??); (r?? = 60000)
ON ERROR GOTO trap
h& = 0
i& = a& \ h&
j% = 9
k% = 0
l% = j% \ k%
PRINT #1, i&; l%
CLOSE #1
END
trap:
PRINT #1, "err"; ERR
RESUME NEXT
