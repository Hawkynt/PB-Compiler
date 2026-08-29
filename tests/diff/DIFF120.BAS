' Differential battery 120: RANDOMIZE, RND and TIMER.
'
' The generator itself is NOT compared against the oracle, and deliberately so: genuine PBC 3.50
' draws .7670898 where this compiler draws .5970459 from the same seed, so printing a drawn value
' would pin our LCG against theirs and fail for a reason that has nothing to do with the back end.
' What IS the contract, and what every line below asserts, is the property a caller can rely on:
' a seeded sequence is a FUNCTION of its seed - reseeding replays it exactly - every draw lands in
' its declared range, and TIMER answers a plausible time of day. Those hold for the oracle and for
' both of our back ends, which is what makes them worth diffing.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' the same seed replays the same sequence, and a different seed does not
RANDOMIZE 7
a! = RND
b! = RND
RANDOMIZE 7
c! = RND
d! = RND
RANDOMIZE 8
e! = RND
PRINT #1, "replay"; (a! = c!); (b! = d!); (a! <> b!); (a! <> e!)

' every draw is in [0, 1)
PRINT #1, "range"; (a! >= 0); (a! < 1); (b! >= 0); (b! < 1); (e! >= 0); (e! < 1)

' RND(a, z) is a LONG in [a, z], and it is seeded from the same cell the bare form reads
RANDOMIZE 12345
lo& = 32767
hi& = -32768
FOR i% = 1 TO 40
  r& = RND(1, 6)
  IF r& < lo& THEN lo& = r&
  IF r& > hi& THEN hi& = r&
NEXT i%
PRINT #1, "bounds"; (lo& >= 1); (hi& <= 6)

RANDOMIZE 12345
p& = RND(1, 1000)
q& = RND(1, 1000)
RANDOMIZE 12345
PRINT #1, "rangeseq"; (p& = RND(1, 1000)); (q& = RND(1, 1000))

' RANDOMIZE with no argument is deliberately absent: genuine PBC 3.50 PROMPTS for the seed and
' waits, so a battery program containing one never finishes and measures nothing. It is covered by
' execution instead (BackendRandomTests), where both of our back ends can be compared to each other.

' TIMER is seconds since midnight: a real number below the length of a day. Its VALUE is not
' comparable across two runs, so the shape is the whole assertion.
t! = TIMER
PRINT #1, "timer"; (t! >= 0); (t! < 86400!)

CLOSE #1
END
