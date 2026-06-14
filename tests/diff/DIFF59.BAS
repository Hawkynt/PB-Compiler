' Differential battery 59: radix string functions vs genuine PBC 3.5.
' HEX$/OCT$/BIN$ render at a 16-bit width whenever the value fits in
' [-32768, 65535] and at 32 bits otherwise - a value-based window that is
' independent of the argument's declared type or signedness. Every emitted
' string must be byte-identical to genuine PBC.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' small negatives collapse to a 16-bit window (FFFF, not FFFFFFFF)
i% = -1
PRINT #1, HEX$(i%); "|"; OCT$(i%); "|"; BIN$(i%)
l& = -1
PRINT #1, HEX$(l&); "|"; OCT$(l&)
PRINT #1, HEX$(-2&); "|"; HEX$(-255); "|"; HEX$(-255&)

' the signed-16 boundary: -32768 fits (8000), -40000 / -70000 spill to 32 bits
PRINT #1, HEX$(-32768); "|"; HEX$(-40000&); "|"; HEX$(-70000&)

' positives: <=65535 stay 16-bit, larger magnitudes render full width
PRINT #1, HEX$(255); "|"; HEX$(65535); "|"; HEX$(65536&)
PRINT #1, HEX$(100000&); "|"; OCT$(100000&); "|"; HEX$(74565&)

' unsigned DWORD bit patterns collapse on the same value-based rule
dw??? = 4294967295???
PRINT #1, HEX$(dw???); "|"; OCT$(dw???)
dw??? = 4294934528???
PRINT #1, HEX$(dw???)
dw??? = 3000000000???
PRINT #1, HEX$(dw???)
w?? = 65535
PRINT #1, HEX$(w??); "|"; HEX$(32768)

CLOSE #1
END
