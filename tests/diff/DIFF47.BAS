' Differential battery 47: O8 int32 (LONG/DWORD) constant-operand folding.
' AND/OR/XOR/ADD/SUB against a compile-time constant fold into immediate pair
' ops (low word in AX, high word in DX) instead of loading the constant into
' CX:BX. Results - including high/low word interplay and wrap - must match the
' genuine register-operand path. Unchecked (no overflow trap here).
OPEN "RESULT.TXT" FOR OUTPUT AS #1

a& = &H12345678
' bitwise masks, constant on the right
PRINT #1, a& AND &H0000FFFF&; a& AND &HFFFF0000&; a& AND &HFF00FF00&
PRINT #1, a& OR &H000000FF&; a& OR &HFF000000&; a& XOR &HFFFFFFFF&
' constant on the left (commutative)
PRINT #1, &H0000FFFF& AND a&; 255 OR a&; &H0F0F0F0F& XOR a&

' add / subtract with a 32-bit constant
b& = 1000000
PRINT #1, b& + 2000000; b& - 500000; b& + 100; b& - 100
PRINT #1, 2000000 + b&; b& + 0; b& - 1000000

' negative LONG operand and wrap
n& = -2000000000
PRINT #1, n& - 500000000; n& AND &H7FFFFFFF&; n& OR 1

' DWORD (unsigned 32-bit) masks (DWORD constants keep the result DWORD)
u??? = &H80000000???
PRINT #1, u??? AND &HF0000000???; u??? OR 15???; u??? XOR &H0FFFFFFF???

CLOSE #1
END
