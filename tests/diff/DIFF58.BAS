' Differential battery 58: data types vs genuine PBC 3.5.
' Scalar widths, silent wrap on store, signed/unsigned ranges, cross-type
' conversions and MKx$/CVx round-trips - every printed value must be byte-exact.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' silent wrap on store (no $ERROR)
i% = 32767 : i% = i% + 1
PRINT #1, i%
b? = 255 : b? = b? + 1
PRINT #1, b?
w?? = 65535 : w?? = w?? + 1
PRINT #1, w??
l& = 2147483647 : l& = l& + 1
PRINT #1, l&

' unsigned vs signed printing of the same bits
u?? = 40000
PRINT #1, u??; -u??
dw??? = 3000000000???
PRINT #1, dw???

' in-range conversions narrow / widen
PRINT #1, CINT(100.5); CINT(-100.5); CLNG(40000); CDBL(7)
n& = 300
PRINT #1, n& AND 255; n& \ 256

' MKx$/CVx round-trips (binary string encodings)
mi$ = MKI$(12345)
PRINT #1, CVI(mi$); LEN(mi$)
ml$ = MKL$(1000000)
PRINT #1, CVL(ml$); LEN(ml$)
ms$ = MKS$(3.5)
PRINT #1, CVS(ms$); LEN(ms$)
md$ = MKD$(2.71828)
PRINT #1, CVD(md$); LEN(md$)

' QUAD
q&& = 100000 * 100000
PRINT #1, q&&

CLOSE #1
END
