' Differential battery 86: ARRAY SORT / ARRAY SCAN / TAGARRAY on numeric arrays.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
' INTEGER ascending, descending, and SCAN
DIM a%(1 TO 6)
a%(1)=30 : a%(2)=10 : a%(3)=50 : a%(4)=20 : a%(5)=40 : a%(6)=5
ARRAY SORT a%(1)
FOR i%=1 TO 6 : PRINT #1, a%(i%); : NEXT : PRINT #1,""
ARRAY SORT a%(1) FOR 6, DESCEND
FOR i%=1 TO 6 : PRINT #1, a%(i%); : NEXT : PRINT #1,""
ARRAY SCAN a%(1), = 20, TO s%
PRINT #1, "=20 "; s%
ARRAY SCAN a%(1) FOR 6, > 25, TO s%
PRINT #1, ">25 "; s%
ARRAY SCAN a%(1), <= 5, TO s%
PRINT #1, "<=5 "; s%
ARRAY SCAN a%(1), <> 50, TO s%
PRINT #1, "<>50 "; s%
' TAGARRAY: sort a key, drag a parallel LONG array along
DIM kk%(1 TO 4)
DIM tt&(1 TO 4)
kk%(1)=30 : kk%(2)=10 : kk%(3)=20 : kk%(4)=40
tt&(1)=300 : tt&(2)=100 : tt&(3)=200 : tt&(4)=400
ARRAY SORT kk%(1), TAGARRAY tt&()
FOR i%=1 TO 4 : PRINT #1, kk%(i%); : NEXT : PRINT #1,""
FOR i%=1 TO 4 : PRINT #1, tt&(i%); : NEXT : PRINT #1,""
' LONG, SINGLE, DOUBLE, QUAD, unsigned WORD/DWORD, BYTE
DIM b&(1 TO 4)
b&(1)=100000 : b&(2)=-5 : b&(3)=99999 : b&(4)=0
ARRAY SORT b&(1)
FOR i%=1 TO 4 : PRINT #1, b&(i%); : NEXT : PRINT #1,""
DIM c!(1 TO 3)
c!(1)=2.5 : c!(2)=-1.5 : c!(3)=0.25
ARRAY SORT c!(1)
FOR i%=1 TO 3 : PRINT #1, c!(i%); : NEXT : PRINT #1,""
DIM dd#(1 TO 4)
dd#(1)=3.5 : dd#(2)=-2.25 : dd#(3)=100.125 : dd#(4)=0
ARRAY SORT dd#(1)
FOR i%=1 TO 4 : PRINT #1, dd#(i%); : NEXT : PRINT #1,""
DIM qq&&(1 TO 4)
qq&&(1)=5000000000 : qq&&(2)=-9000000000 : qq&&(3)=1 : qq&&(4)=5000000000
ARRAY SORT qq&&(1)
FOR i%=1 TO 4 : PRINT #1, qq&&(i%); : NEXT : PRINT #1,""
DIM ww??(1 TO 4)
ww??(1)=50000 : ww??(2)=100 : ww??(3)=65535 : ww??(4)=0
ARRAY SORT ww??(1)
FOR i%=1 TO 4 : PRINT #1, ww??(i%); : NEXT : PRINT #1,""
DIM nn???(1 TO 3)
nn???(1)=4000000000 : nn???(2)=10 : nn???(3)=2000000000
ARRAY SORT nn???(1)
FOR i%=1 TO 3 : PRINT #1, nn???(i%); : NEXT : PRINT #1,""
DIM yy?(1 TO 4)
yy?(1)=200 : yy?(2)=5 : yy?(3)=255 : yy?(4)=100
ARRAY SORT yy?(1)
FOR i%=1 TO 4 : PRINT #1, yy?(i%); : NEXT : PRINT #1,""
CLOSE #1
END
