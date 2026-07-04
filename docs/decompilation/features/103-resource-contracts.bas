' @title: $RESOURCE + contracts
' @desc:  $RESOURCE bakes file bytes into the image as a BYTE array (DATA+READ under pb35); REQUIRE/ENSURE raise error 5 on violation, compiled out under $OPTIMIZE SPEED.
$RESOURCE blob, "103-blob.bin"
DIM n AS INTEGER
n = 1
REQUIRE n > 0, "n must be positive"
PRINT blob(0); blob(n); UBOUND(blob)
