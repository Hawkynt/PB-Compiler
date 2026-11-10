; Original bare-metal VGA oracle: QEMU's own VGA device + BIOS, no DOS runtime.
; The expected result is derived from VGA Write Mode 2 and four 1-bit planes.
; Assemble to a 1.44 MB floppy boot sector; output via QEMU port E9h.
bits 16
org 0x7c00

start:
    cli
    xor ax, ax
    mov ss, ax
    mov sp, 0x7c00
    sti

    mov ax, 0x0012
    int 0x10
    mov ax, 0xa000
    mov es, ax
    xor bx, bx

    ; Write color 5 to eight pixels, using four-plane broadcast Mode 2.
    mov dx, 0x3c4
    mov ax, 0x0f02              ; sequencer index 2 = all four planes
    out dx, ax
    mov dx, 0x3ce
    mov ax, 0x0003              ; data rotate = replace
    out dx, ax
    mov ax, 0xff08              ; graphics bit mask = all pixels
    out dx, ax
    mov ax, 0x0205              ; write mode 2, read mode 0
    out dx, ax
    mov al, 5
    mov [es:bx], al

    ; Replace only pixels 0,2,4,6 with color 6, preserving 1,3,5,7.
    mov ax, 0xaa08
    out dx, ax
    mov al, [es:bx]             ; mandatory latch preload for partial writes
    mov al, 6
    mov [es:bx], al

    mov ax, 0x0005
    out dx, ax
    mov ax, 0xff08
    out dx, ax

    ; Independent BIOS readback must show colors 6,5,6,5,6,5,6,5.
    xor si, si
pixel:
    mov cx, si
    xor dx, dx
    xor bh, bh
    mov ah, 0x0d
    int 0x10
    and al, 0x0f
    add al, '0'
    out 0xe9, al
    inc si
    cmp si, 8
    jb pixel
    mov al, 10
    out 0xe9, al

    ; Read each hardware plane directly: 55, AA, FF, 00. BIOS pixel
    ; operations may have changed ES/BX, so reestablish these registers.
    mov ax, 0xa000
    mov es, ax
    xor bx, bx
    mov dx, 0x3ce
    mov ax, 0x0005              ; read mode 0
    out dx, ax
    mov ax, 0x0004
    out dx, ax
    mov al, [es:bx]
    call emit_hex
    mov ax, 0x0104
    out dx, ax
    mov al, [es:bx]
    call emit_hex
    mov ax, 0x0204
    out dx, ax
    mov al, [es:bx]
    call emit_hex
    mov ax, 0x0304
    out dx, ax
    mov al, [es:bx]
    call emit_hex
    mov al, 10
    out 0xe9, al

    ; Independent VGA Read Mode 1 test: compare all eight pixels against
    ; color 6 (10101010b) and color 5 (01010101b) in two memory reads.
    ; The PAINT fastpath uses these bit masks to classify eight pixels.
    mov dx, 0x3ce
    mov ax, 0x0f07              ; GC07: all planes significant
    out dx, ax
    mov ax, 0x0805              ; GC05: read mode 1
    out dx, ax
    mov ax, 0x0602              ; GC02: compare color 6
    out dx, ax
    mov al, [es:bx]
    call emit_hex
    mov ax, 0x0502              ; GC02: compare color 5
    out dx, ax
    mov al, [es:bx]
    call emit_hex
    mov al, 10
    out 0xe9, al

    mov dx, 0xf4                ; QEMU isa-debug-exit (exit status 1)
    xor al, al
    out dx, al
halt:
    hlt
    jmp halt

emit_hex:
    push ax
    mov ah, al
    shr al, 4
    call emit_nibble
    mov al, ah
    and al, 0x0f
    call emit_nibble
    pop ax
    ret

emit_nibble:
    cmp al, 10
    jb decimal
    add al, 7
decimal:
    add al, '0'
    out 0xe9, al
    ret

times 510-($-$$) db 0
dw 0xaa55
