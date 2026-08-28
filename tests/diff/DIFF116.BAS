' Differential battery 116: decimal literals wider than any integer type.
' A decimal literal PB cannot hold as an integer is a FLOAT, not an error - PBC 3.50 prints
' 9223372036854775808 as 9.22337203685478E+18 and 99999999999999999999 as 1E+20. Our lexer
' parsed the digits into a LONG unconditionally and threw out of the tokenizer instead, which
' ended the compilation with a stack trace rather than a program.
' Writes RESULT.TXT; run-diff-tests.sh compares against genuine PBC 3.5 output.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
PRINT #1, 2147483647
PRINT #1, 2147483648
PRINT #1, 9223372036854775807
PRINT #1, 9223372036854775808
PRINT #1, -9223372036854775808
PRINT #1, 99999999999999999999
PRINT #1, 12345678901234567890123
PRINT #1, 9223372036854775808 + 1
PRINT #1, -99999999999999999999
CLOSE #1
END
