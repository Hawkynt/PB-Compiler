a$ = "the cat sat on the mat"
PRINT TALLY(a$, "at"); TALLY(a$, ANY "aeiou"); TALLY(a$, "")
PRINT "["; EXTRACT$(a$, "sat"); "]["; EXTRACT$(a$, ANY "xyz"); "]["; EXTRACT$(a$, ANY "c"); "]"
PRINT "["; REMOVE$(a$, "the "); "]"
b$ = a$
REPLACE "at" WITH "og" IN b$
PRINT b$
REPLACE "the" WITH "" IN b$
PRINT b$
PRINT INSTR(a$, ANY "ms"); INSTR(10, a$, ANY "t"); VERIFY(a$, "theca "); VERIFY("aaa", "a")
PRINT MIN$("pear", "apple"); " "; MAX$("pear", "apple"); " "; MIN$("x", "x")
m$ = MKI$(-2) + MKL$(305419896) + MKS$(1.5) + MKD$(-2.25)
PRINT LEN(m$); CVI(m$); CVL(MID$(m$, 3)); CVS(MID$(m$, 7)); CVD(MID$(m$, 11)); CVI("A")
c$ = "hello"
ASC(c$, 1) = 74
ASC(c$, 9) = 75
PRINT c$
d$ = "      "
LSET d$ = "ab"
PRINT "["; d$; "]";
RSET d$ = "cd"
PRINT "["; d$; "]";
RSET d$ = "abcdefghij"
PRINT "["; d$; "]"
DIM f AS STRING * 5
RSET f = "xy"
PRINT "["; f; "]"
DIM z AS ASCIIZ * 6
z = "abcdefgh"
PRINT "["; z; "]"; LEN(z)
