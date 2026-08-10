/* ==========================================================================
 * pbc_rt.c - a hosted implementation of the pbc runtime ABI.
 * ==========================================================================
 * Enough of PowerBASIC's observable runtime to let a `pbc --emit-c` program
 * behave like the DOS binary built from the same source: PB's PRINT layout,
 * PB's string semantics (1-based, clamping rather than trapping) and PB's
 * numeric text form.
 *
 * String handles are never freed. PB's DOS runtime owns a compacting string
 * heap; reproducing that here would buy nothing, since the point of this file
 * is observable equivalence, not memory behaviour.
 * ========================================================================== */
#include "pbc_rt.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>

/* --- handles ------------------------------------------------------------ */

static void *rt_xalloc(size_t n) {
  void *p = malloc(n ? n : 1);
  if (!p) {
    fputs("pbc runtime: out of memory\n", stderr);
    exit(1);
  }
  return p;
}

static pb_str *rt_new(int32_t len) {
  pb_str *s = (pb_str *)rt_xalloc(sizeof(pb_str));
  s->len = len < 0 ? 0 : len;
  s->data = (char *)rt_xalloc((size_t)s->len + 1);
  s->data[s->len] = '\0';
  return s;
}

static pb_str *rt_make(const char *bytes, int32_t len) {
  pb_str *s = rt_new(len);
  if (len > 0)
    memcpy(s->data, bytes, (size_t)len);
  return s;
}

/* An absent handle reads as the empty string, exactly like an unassigned PB string. */
static pb_str *rt_of(void *h) {
  static pb_str empty = {0, (char *)""};
  return h ? (pb_str *)h : &empty;
}

void *rt_str_const(void *bytes, int32_t len) { return rt_make((const char *)bytes, len); }

void *rt_str_concat(void *a, void *b) {
  pb_str *x = rt_of(a), *y = rt_of(b);
  pb_str *s = rt_new(x->len + y->len);
  memcpy(s->data, x->data, (size_t)x->len);
  memcpy(s->data + x->len, y->data, (size_t)y->len);
  return s;
}

int32_t rt_str_len(void *s) { return rt_of(s)->len; }

int32_t rt_str_compare(void *a, void *b) {
  pb_str *x = rt_of(a), *y = rt_of(b);
  int32_t n = x->len < y->len ? x->len : y->len;
  int c = n ? memcmp(x->data, y->data, (size_t)n) : 0;
  if (c) return c < 0 ? -1 : 1;
  return x->len == y->len ? 0 : (x->len < y->len ? -1 : 1);
}

/* An owned copy, so the value handed on is a temporary the consuming routines may free. Every
   other producer in the lowering already yields one - a literal from rt_str_const, a concatenation
   from rt_str_concat - and only a read of a variable or array element does not, because the handle
   it finds belongs to that cell. */
void *rt_str_dup(void *s) {
  pb_str *x = rt_of(s);
  return rt_make(x->data, x->len);
}

void *rt_str_left(void *s, int32_t n) {
  pb_str *x = rt_of(s);
  if (n < 0) n = 0;
  if (n > x->len) n = x->len;
  return rt_make(x->data, n);
}

void *rt_str_right(void *s, int32_t n) {
  pb_str *x = rt_of(s);
  if (n < 0) n = 0;
  if (n > x->len) n = x->len;
  return rt_make(x->data + (x->len - n), n);
}

void *rt_str_mid(void *s, int32_t start, int32_t len) {
  pb_str *x = rt_of(s);
  if (start < 1) start = 1;
  if (start > x->len || len <= 0) return rt_new(0);
  if (len > x->len - start + 1) len = x->len - start + 1;
  return rt_make(x->data + start - 1, len);
}

void *rt_str_mid2(void *s, int32_t start) {
  pb_str *x = rt_of(s);
  return rt_str_mid(s, start, x->len);
}

void *rt_str_mid_assign(void *dst, int32_t start, int32_t len, void *src) {
  pb_str *d = rt_of(dst), *v = rt_of(src);
  pb_str *out = rt_make(d->data, d->len);          /* PB replaces in place, never grows */
  int32_t i;
  if (start < 1) start = 1;
  if (len < 0 || len > v->len) len = v->len;
  for (i = 0; i < len && start - 1 + i < out->len; ++i)
    out->data[start - 1 + i] = v->data[i];
  return out;
}

static pb_str *rt_map(void *s, int upper) {
  pb_str *x = rt_of(s);
  pb_str *r = rt_make(x->data, x->len);
  int32_t i;
  for (i = 0; i < r->len; ++i)
    r->data[i] = (char)(upper ? toupper((unsigned char)r->data[i]) : tolower((unsigned char)r->data[i]));
  return r;
}

void *rt_str_ucase(void *s) { return rt_map(s, 1); }
void *rt_str_lcase(void *s) { return rt_map(s, 0); }

void *rt_str_ltrim(void *s) {
  pb_str *x = rt_of(s);
  int32_t i = 0;
  while (i < x->len && x->data[i] == ' ') ++i;
  return rt_make(x->data + i, x->len - i);
}

void *rt_str_rtrim(void *s) {
  pb_str *x = rt_of(s);
  int32_t n = x->len;
  while (n > 0 && x->data[n - 1] == ' ') --n;
  return rt_make(x->data, n);
}

void *rt_str_space(int32_t n) {
  pb_str *s = rt_new(n < 0 ? 0 : n);
  memset(s->data, ' ', (size_t)s->len);
  return s;
}

void *rt_str_string(int32_t n, int32_t ch) {
  pb_str *s = rt_new(n < 0 ? 0 : n);
  memset(s->data, (int)(unsigned char)ch, (size_t)s->len);
  return s;
}

void *rt_str_string_s(int32_t n, void *src) {
  pb_str *x = rt_of(src);
  return rt_str_string(n, x->len ? (unsigned char)x->data[0] : 0);
}

/* REPEAT$(n, s$) - the WHOLE string n times. Not rt_str_string_s, which is STRING$ and repeats only
   the first character; the two differ for every source longer than one byte. */
void *rt_str_repeat(int32_t n, void *src) {
  pb_str *x = rt_of(src);
  int32_t count = n < 0 ? 0 : n, i;
  pb_str *s = rt_new(count * x->len);
  for (i = 0; i < count; ++i)
    memcpy(s->data + (size_t)i * (size_t)x->len, x->data, (size_t)x->len);
  return s;
}

/* ASC(s$, n) = code. Out-of-range positions are IGNORED, matching the DOS rt_ascset, which returns
   early for a zero handle, a zero position or one past the end. */
void *rt_str_asc_set(void *s, int16_t pos, int16_t code) {
  pb_str *x = rt_of(s);
  if (pos >= 1 && pos <= x->len)
    x->data[pos - 1] = (char)(unsigned char)code;
  return s;
}

/* The C runtime allocates with malloc and never compacts, so freeing is optional for correctness
   here - but the IR emits the call and it has to link, and honouring it keeps a long-running C build
   from growing without bound the way the DOS heap would have run out. */
void rt_str_free(void *s) {
  if (s)
    free(s);
}

/* RND, reproducing the DOS runtime's generator exactly rather than reaching for the C library's:
   seed = seed * 1103515245 + 12345, answer = (high word & 0x7FFF) / 32768.

   The +12345 lands on the LOW word only and its carry is NOT propagated - the DOS routine writes
   ADD AX, 12345 with no ADC DX, 0 - so the sequence depends on that omission. Reproducing the
   generator is what makes a program using RND comparable across the two back ends at all; "some
   pseudo-random number" would not be. */
static uint32_t rt_rndseed = 0x12345678u;

static double rt_rnd_next(void) {
  uint32_t product = rt_rndseed * 1103515245u;
  uint16_t high = (uint16_t)(product >> 16);
  uint16_t low = (uint16_t)((product & 0xFFFFu) + 12345u);   /* wraps; no carry into `high` */
  rt_rndseed = ((uint32_t)high << 16) | low;
  return (double)(high & 0x7FFF) / 32768.0;
}

double rt_rnd(void) { return rt_rnd_next(); }

/* RND(a, z): a LONG in [a, z] inclusive - a different answer from the bare RND's fraction. */
int32_t rt_rnd_range(int32_t lower, int32_t upper) {
  double span = (double)upper - (double)lower + 1.0;
  if (span <= 0.0)
    return lower;
  return lower + (int32_t)(rt_rnd_next() * span);
}

void *rt_str_chr(int32_t code) {
  char c = (char)(unsigned char)code;
  return rt_make(&c, 1);
}

int32_t rt_str_asc(void *s) {
  pb_str *x = rt_of(s);
  return x->len ? (unsigned char)x->data[0] : -1;   /* PB: ASC("") is -1 */
}

/* HEX$/OCT$/BIN$, all one routine, matching the DOS rt_radix exactly.
   `packed` is (minimum digits << 8) | bits-per-digit, the single word that routine reads.

   Two things this has to reproduce and the previous version did not. The digit count is a MINIMUM
   that zero-pads, never a width that truncates - a value needing more digits still prints them all.
   And genuine HEX$ renders at SIXTEEN bits whenever the value fits in [-32768, 65535]: a small
   negative arrives sign-extended, so HEX$(-1) is "FFFF" and not "FFFFFFFF". Dividing by a base got
   the digits right and both of those wrong. */
static void *rt_radix_packed(int32_t v, int32_t packed) {
  int bits = packed & 0xFF;                 /* 4 for HEX$, 3 for OCT$, 1 for BIN$ */
  int least = (packed >> 8) & 0xFF;
  uint32_t u = (uint32_t)v;
  uint32_t mask = (1u << bits) - 1u;
  char tmp[48], buf[48];
  int i = 0, j;
  if ((u >> 16) == 0xFFFFu && (u & 0x8000u))
    u &= 0xFFFFu;                           /* the 16-bit fold */
  do {
    int d = (int)(u & mask);
    tmp[i++] = (char)(d < 10 ? '0' + d : 'A' + d - 10);
    u >>= bits;
  } while (u || i < least);
  for (j = 0; j < i; ++j) buf[j] = tmp[i - 1 - j];
  return rt_make(buf, i);
}

void *rt_str_radix(int32_t v, int32_t packed) { return rt_radix_packed(v, packed); }
void *rt_str_hex(int32_t v) { return rt_radix_packed(v, (1 << 8) | 4); }
void *rt_str_oct(int32_t v) { return rt_radix_packed(v, (1 << 8) | 3); }
void *rt_str_bin(int32_t v) { return rt_radix_packed(v, (1 << 8) | 1); }

int32_t rt_str_instr(void *hay, void *needle) { return rt_str_instr_start(1, hay, needle); }

int32_t rt_str_instr_start(int32_t start, void *hay, void *needle) {
  pb_str *h = rt_of(hay), *n = rt_of(needle);
  int32_t i;
  if (start < 1) start = 1;
  if (n->len == 0) return start <= h->len ? start : 0;
  for (i = start - 1; i + n->len <= h->len; ++i)
    if (memcmp(h->data + i, n->data, (size_t)n->len) == 0)
      return i + 1;
  return 0;
}

double rt_str_val(void *s) {
  pb_str *x = rt_of(s);
  char buf[64];
  int32_t n = x->len < (int32_t)sizeof(buf) - 1 ? x->len : (int32_t)sizeof(buf) - 1;
  memcpy(buf, x->data, (size_t)n);
  buf[n] = '\0';
  return strtod(buf, NULL);
}

void rt_str_to_fixed(void *dst, int32_t n, void *src) {
  pb_str *x = rt_of(src);
  int32_t copy = x->len < n ? x->len : n;
  memcpy(dst, x->data, (size_t)copy);
  if (copy < n)
    memset((char *)dst + copy, ' ', (size_t)(n - copy));   /* PB pads with spaces */
}

void *rt_str_from_fixed(void *src, int32_t n) { return rt_make((const char *)src, n); }

/* --- PB numeric text ---------------------------------------------------- */

/* PB prints a fraction without its leading zero (".0001", not "0.0001"). */
static void rt_strip_leading_zero(char *b) {
  if (b[0] == '0' && b[1] == '.')
    memmove(b, b + 1, strlen(b));
  else if (b[0] == '-' && b[1] == '0' && b[2] == '.')
    memmove(b + 1, b + 2, strlen(b + 1));
}

static void rt_fmt_float(char *buf, size_t cap, long double v, int digits) {
  snprintf(buf, cap, "%.*g", digits, (double)v);
  rt_strip_leading_zero(buf);
}

static void *rt_str_num(const char *text) {
  /* STR$ gives a non-negative number a leading space where the sign would go */
  size_t n = strlen(text);
  if (text[0] == '-')
    return rt_make(text, (int32_t)n);
  {
    char buf[80];
    buf[0] = ' ';
    memcpy(buf + 1, text, n);
    return rt_make(buf, (int32_t)n + 1);
  }
}

static void *rt_str_int(long long v) {
  char b[32];
  snprintf(b, sizeof b, "%lld", v);
  return rt_str_num(b);
}

void *rt_str_from_i8(int8_t v) { return rt_str_int(v); }
void *rt_str_from_u8(uint8_t v) { return rt_str_int(v); }
void *rt_str_from_i16(int16_t v) { return rt_str_int(v); }
void *rt_str_from_u16(uint16_t v) { return rt_str_int(v); }
void *rt_str_from_i32(int32_t v) { return rt_str_int(v); }
void *rt_str_from_u32(uint32_t v) { return rt_str_int((long long)v); }
void *rt_str_from_i64(int64_t v) { return rt_str_int((long long)v); }

/* Both take a long double, for the reason rt_print_single does: the value arrives at the x87's own
   width whatever its declared type, and the NAME picks the significant-digit count. */
void *rt_str_from_single(long double v) { char b[64]; rt_fmt_float(b, sizeof b, v, 7); return rt_str_num(b); }
void *rt_str_from_double(long double v) { char b[64]; rt_fmt_float(b, sizeof b, v, 15); return rt_str_num(b); }
void *rt_str_from_ext(long double v) { char b[64]; rt_fmt_float(b, sizeof b, v, 18); return rt_str_num(b); }

/* --- MKx$ / CVx --------------------------------------------------------- */

void *rt_str_mki(int16_t v) { return rt_make((const char *)&v, 2); }
void *rt_str_mkl(int32_t v) { return rt_make((const char *)&v, 4); }
void *rt_str_mkdwd(int32_t v) { return rt_make((const char *)&v, 4); }
void *rt_str_mks(float v) { return rt_make((const char *)&v, 4); }
void *rt_str_mkd(double v) { return rt_make((const char *)&v, 8); }

static void rt_cv(void *s, void *out, int32_t n) {
  pb_str *x = rt_of(s);
  memset(out, 0, (size_t)n);
  memcpy(out, x->data, (size_t)(x->len < n ? x->len : n));
}

int16_t rt_str_cvi(void *s) { int16_t v; rt_cv(s, &v, 2); return v; }
int32_t rt_str_cvl(void *s) { int32_t v; rt_cv(s, &v, 4); return v; }
int32_t rt_str_cvdwd(void *s) { int32_t v; rt_cv(s, &v, 4); return v; }
float rt_str_cvs(void *s) { float v; rt_cv(s, &v, 4); return v; }
double rt_str_cvd(void *s) { double v; rt_cv(s, &v, 8); return v; }

/* --- console ------------------------------------------------------------ */

static int32_t rt_column;                      /* 0-based, for TAB / print zones */

static void rt_out(const char *bytes, int32_t len) {
  int32_t i;
  for (i = 0; i < len; ++i) {
    fputc(bytes[i], stdout);
    rt_column = bytes[i] == '\n' ? 0 : rt_column + 1;
  }
}

/* PB gives every numeric a sign slot in front and a trailing space behind. */
static void rt_out_num(const char *text) {
  char buf[80];
  size_t n = strlen(text);
  size_t at = 0;
  if (text[0] != '-')
    buf[at++] = ' ';
  memcpy(buf + at, text, n);
  at += n;
  buf[at++] = ' ';
  rt_out(buf, (int32_t)at);
}

static void rt_out_int(long long v) {
  char b[32];
  snprintf(b, sizeof b, "%lld", v);
  rt_out_num(b);
}

void rt_print_str(void *bytes, int32_t len) { rt_out((const char *)bytes, len); }
void rt_print_strvar(void *s) { pb_str *x = rt_of(s); rt_out(x->data, x->len); }
void rt_print_nl(void) { rt_out("\n", 1); }

void rt_print_i8(int8_t v) { rt_out_int(v); }
void rt_print_u8(uint8_t v) { rt_out_int(v); }
void rt_print_i16(int16_t v) { rt_out_int(v); }
void rt_print_u16(uint16_t v) { rt_out_int(v); }
void rt_print_i32(int32_t v) { rt_out_int(v); }
void rt_print_u32(uint32_t v) { rt_out_int((long long)v); }
void rt_print_i64(int64_t v) { rt_out_int((long long)v); }

void rt_print_single(long double v) { char b[64]; rt_fmt_float(b, sizeof b, v, 7); rt_out_num(b); }
void rt_print_double(long double v) { char b[64]; rt_fmt_float(b, sizeof b, v, 15); rt_out_num(b); }
void rt_print_ext(long double v) { char b[64]; rt_fmt_float(b, sizeof b, v, 18); rt_out_num(b); }

/* The PRINT comma separator: advance to the next 14-column zone. Sitting exactly on a boundary
   still emits a full zone of spaces, which is what 14 - (column mod 14) says and what the DOS
   rt_print_zone does. */
void rt_print_comma(void) {
  int32_t pad = 14 - (rt_column % 14);
  while (pad-- > 0)
    rt_out(" ", 1);
}

void rt_print_tab(int32_t column) {
  if (column < 1) column = 1;
  while (rt_column > column - 1) rt_print_nl();
  while (rt_column < column - 1) rt_out(" ", 1);
}

void rt_print_spc(int32_t count) {
  while (count-- > 0) rt_out(" ", 1);
}

/* PB's comma separator advances to the next 14-column print zone. */
void rt_print_zone(void) {
  do { rt_out(" ", 1); } while (rt_column % 14);
}

/* --- console input ------------------------------------------------------ */

/* PB reads one comma-separated field per INPUT variable; a LINE INPUT takes the
   whole line verbatim. An INPUT field is trimmed of the spaces around it (PB
   reads "5, abc" as 5 and "abc"), which LINE INPUT must not do. Both stop at end
   of file, which reads as an empty field. */
static int rt_getfield(char *buf, size_t cap, int wholeLine) {
  size_t n = 0;
  int c;
  while ((c = fgetc(stdin)) != EOF) {
    if (c == '\r')
      continue;
    if (c == '\n' || (!wholeLine && c == ','))
      break;
    if (n + 1 < cap)
      buf[n++] = (char)c;
  }
  buf[n] = '\0';
  if (!wholeLine) {
    size_t start = 0;
    while (buf[start] == ' ' || buf[start] == '\t') ++start;
    while (n > start && (buf[n - 1] == ' ' || buf[n - 1] == '\t')) --n;
    buf[n] = '\0';
    if (start)
      memmove(buf, buf + start, n - start + 1);
  }
  return c != EOF || n > 0;
}


static double rt_input_num(void) {
  char buf[128];
  rt_getfield(buf, sizeof buf, 0);
  return strtod(buf, NULL);
}

void rt_input_prompt(void *bytes, int32_t len) { rt_out((const char *)bytes, len); }

int8_t rt_input_i8(void) { return (int8_t)rt_input_num(); }
uint8_t rt_input_u8(void) { return (uint8_t)rt_input_num(); }
int16_t rt_input_i16(void) { return (int16_t)rt_input_num(); }
uint16_t rt_input_u16(void) { return (uint16_t)rt_input_num(); }
int32_t rt_input_i32(void) { return (int32_t)rt_input_num(); }
uint32_t rt_input_u32(void) { return (uint32_t)rt_input_num(); }
int64_t rt_input_i64(void) { return (int64_t)rt_input_num(); }
float rt_input_single(void) { return (float)rt_input_num(); }
double rt_input_double(void) { return rt_input_num(); }
long double rt_input_ext(void) { return (long double)rt_input_num(); }

void *rt_input_str(void) {
  char buf[1024];
  rt_getfield(buf, sizeof buf, 0);
  return rt_make(buf, (int32_t)strlen(buf));
}

void *rt_input_line(void) {
  char buf[1024];
  rt_getfield(buf, sizeof buf, 1);
  return rt_make(buf, (int32_t)strlen(buf));
}

/* --- sequential file I/O -------------------------------------------------

   PB numbers files 1..15 and the runtime keeps the handles itself, so a file number is an index
   here exactly as it is a table slot in the DOS runtime. Only the SEQUENTIAL modes are served:
   INPUT, OUTPUT and APPEND. RANDOM and BINARY need a record layout and FIELD, which the lowering
   declines anyway, so opening one raises rather than pretending.

   The print entries take the file number FIRST and are otherwise the console ones - that is the
   lowering's own naming rule (rt_print_x becomes rt_fprint_x with the number pushed in front), so
   the two stay in step by construction. Each file carries its own column for the same reason the
   DOS runtime keeps rt_colptr: a comma zone on #2 must not be measured against the screen. */

#define RT_FILES 16

static FILE *rt_file[RT_FILES];
static int32_t rt_file_col[RT_FILES];

static FILE *rt_file_of(int32_t n) {
  if (n < 1 || n >= RT_FILES || !rt_file[n])
    rt_error(52);                       /* bad file number / not open */
  return rt_file[n];
}

void rt_file_open(int32_t n, void *name, int32_t mode, int32_t reclen) {
  pb_str *x = rt_of(name);
  char path[260];
  int32_t len = x->len < (int32_t)sizeof path - 1 ? x->len : (int32_t)sizeof path - 1;
  (void)reclen;
  if (n < 1 || n >= RT_FILES)
    rt_error(52);
  if (rt_file[n])
    rt_error(55);                       /* already open */
  memcpy(path, x->data, (size_t)len);
  path[len] = '\0';
  /* FileMode: 0 Input, 1 Output, 2 Append, 3 Random, 4 Binary. RANDOM and BINARY are opened for
     READ AND WRITE and created when absent, which is what PB does with them - "r+b" fails on a file
     that is not there, so the miss falls back to "w+b" rather than raising. */
  if (mode == 3 || mode == 4) {
    rt_file[n] = fopen(path, "r+b");
    if (!rt_file[n])
      rt_file[n] = fopen(path, "w+b");
  } else {
    rt_file[n] = mode == 0 ? fopen(path, "rb")
               : mode == 1 ? fopen(path, "wb")
               : mode == 2 ? fopen(path, "ab")
               : NULL;
  }
  if (!rt_file[n])
    rt_error(mode == 0 ? 53 : 64);      /* file not found / bad file name */
  rt_file_col[n] = 0;
}

void rt_file_close(int32_t n) {
  if (n >= 1 && n < RT_FILES && rt_file[n]) {
    fclose(rt_file[n]);
    rt_file[n] = NULL;
    rt_file_col[n] = 0;
  }
}

void rt_file_close_all(void) {
  int32_t n;
  for (n = 1; n < RT_FILES; ++n)
    rt_file_close(n);
}

int16_t rt_freefile(void) {
  int32_t n;
  for (n = 1; n < RT_FILES; ++n)
    if (!rt_file[n])
      return (int16_t)n;
  rt_error(67);                         /* too many files */
  return 0;
}

/* PB's EOF is TRUE only once the last byte has been read, so it peeks rather than trusting feof,
   which stays false until a read has already failed. */
int16_t rt_eof(int16_t n) {
  FILE *f = rt_file_of(n);
  int ch = fgetc(f);
  if (ch == EOF)
    return -1;
  ungetc(ch, f);
  return 0;
}

void rt_kill(void *name) {
  pb_str *x = rt_of(name);
  char path[260];
  int32_t len = x->len < (int32_t)sizeof path - 1 ? x->len : (int32_t)sizeof path - 1;
  memcpy(path, x->data, (size_t)len);
  path[len] = '\0';
  /* The failure is IGNORED, matching the DOS runtime: its rt_kill issues INT 21h and returns
     without looking at the carry flag, so KILL of a file that is not there is a no-op. Programs
     rely on it - the battery's own FILEIO1 opens with KILL "OUT.TXT" to clear any previous run. */
  (void)remove(path);
}

static void rt_fout(int32_t n, const char *bytes, int32_t len) {
  FILE *f = rt_file_of(n);
  int32_t i;
  for (i = 0; i < len; ++i) {
    fputc(bytes[i], f);
    rt_file_col[n] = bytes[i] == '\n' ? 0 : rt_file_col[n] + 1;
  }
}

static void rt_fout_int(int32_t n, long long v) {
  char b[32];
  sprintf(b, "%lld", v);
  if (v >= 0)
    rt_fout(n, " ", 1);
  rt_fout(n, b, (int32_t)strlen(b));
  rt_fout(n, " ", 1);
}

void rt_fprint_str(int32_t n, void *bytes, int32_t len) { rt_fout(n, (const char *)bytes, len); }
void rt_fprint_strvar(int32_t n, void *s) { pb_str *x = rt_of(s); rt_fout(n, x->data, x->len); }
void rt_fprint_nl(int32_t n) { rt_fout(n, "\n", 1); }
void rt_fprint_comma(int32_t n) {
  int32_t pad = 14 - (rt_file_col[n] % 14);
  while (pad-- > 0)
    rt_fout(n, " ", 1);
}

void rt_fprint_i8(int32_t n, int8_t v) { rt_fout_int(n, v); }
void rt_fprint_u8(int32_t n, uint8_t v) { rt_fout_int(n, v); }
void rt_fprint_i16(int32_t n, int16_t v) { rt_fout_int(n, v); }
void rt_fprint_u16(int32_t n, uint16_t v) { rt_fout_int(n, v); }
void rt_fprint_i32(int32_t n, int32_t v) { rt_fout_int(n, v); }
void rt_fprint_u32(int32_t n, uint32_t v) { rt_fout_int(n, (long long)v); }
void rt_fprint_i64(int32_t n, int64_t v) { rt_fout_int(n, (long long)v); }
void rt_fprint_single(int32_t n, long double v) { char b[64]; rt_fmt_float(b, sizeof b, v, 7); rt_fout(n, b, (int32_t)strlen(b)); rt_fout(n, " ", 1); }
void rt_fprint_double(int32_t n, long double v) { char b[64]; rt_fmt_float(b, sizeof b, v, 15); rt_fout(n, b, (int32_t)strlen(b)); rt_fout(n, " ", 1); }

/* GET #n, rec, var / PUT #n, rec, var - one fixed-size value at a record position. The record
   number is 1-BASED (record 1 is the first) and 0 means "wherever the file already is", which is
   what the lowering passes when the statement names no record. */
static void rt_file_seek_record(int32_t n, int32_t record, int32_t size) {
  FILE *f = rt_file_of(n);
  if (record > 0)
    fseek(f, (long)(record - 1) * (long)size, SEEK_SET);
}

void rt_file_put(int32_t n, int32_t record, void *value, int32_t size) {
  rt_file_seek_record(n, record, size);
  fwrite(value, 1, (size_t)(size < 0 ? 0 : size), rt_file_of(n));
}

void rt_file_get(int32_t n, int32_t record, void *value, int32_t size) {
  size_t want = (size_t)(size < 0 ? 0 : size);
  size_t got;
  rt_file_seek_record(n, record, size);
  got = fread(value, 1, want, rt_file_of(n));
  if (got < want)
    memset((char *)value + got, 0, want - got);      /* a short read leaves zeros, not stale bytes */
}

int32_t rt_file_length(int32_t n) {
  FILE *f = rt_file_of(n);
  long here = ftell(f), end;
  fseek(f, 0, SEEK_END);
  end = ftell(f);
  fseek(f, here, SEEK_SET);
  return (int32_t)end;
}

int32_t rt_file_pos(int32_t n) { return (int32_t)ftell(rt_file_of(n)); }

/* SEEK #n, p. Only the sequential modes are open here, so this is the BINARY reading: a 0-based
   byte offset. RANDOM's 1-based record numbering has no meaning without a record length, and
   opening one already raises. */
void rt_file_seek(int32_t n, int32_t position) {
  FILE *f = rt_file_of(n);
  fseek(f, position < 0 ? 0 : position, SEEK_SET);
}

/* PUT$ / GET$ - raw bytes, no terminator and no record structure. A GET$ that reaches end of file
   early yields what there was, which is what the DOS routine does with a short read. */
void rt_fput_str(int32_t n, void *s) {
  pb_str *x = rt_of(s);
  rt_fout(n, x->data, x->len);
}

void *rt_fget_str(int32_t n, int32_t count) {
  FILE *f = rt_file_of(n);
  char buf[1024];
  int32_t want = count < 0 ? 0 : (count > (int32_t)sizeof buf ? (int32_t)sizeof buf : count);
  size_t got = want ? fread(buf, 1, (size_t)want, f) : 0;
  return rt_make(buf, (int32_t)got);
}

/* LINE INPUT #n: the rest of the line, without its terminator. */
void *rt_finput_line(int32_t n) {
  FILE *f = rt_file_of(n);
  char buf[1024];
  int32_t len = 0;
  int ch;
  while ((ch = fgetc(f)) != EOF && ch != '\n')
    if (ch != '\r' && len < (int32_t)sizeof buf)
      buf[len++] = (char)ch;
  return rt_make(buf, len);
}

/* --- memory / arrays ---------------------------------------------------- */

void *rt_arr_alloc(int32_t count, int32_t elementSize) {
  size_t n = (size_t)(count < 0 ? 0 : count) * (size_t)(elementSize < 0 ? 0 : elementSize);
  void *p = rt_xalloc(n ? n : 1);
  memset(p, 0, n ? n : 1);                    /* PB arrays start zeroed */
  return p;
}

void *rt_arr_alloc_ptr(int32_t count) { return rt_arr_alloc(count, (int32_t)sizeof(void *)); }

void *rt_arr_realloc(void *p, int32_t count, int32_t elementSize) {
  size_t n = (size_t)(count < 0 ? 0 : count) * (size_t)(elementSize < 0 ? 0 : elementSize);
  void *q = realloc(p, n ? n : 1);
  if (!q) { fputs("pbc runtime: out of memory\n", stderr); exit(1); }
  return q;
}

void *rt_arr_realloc_ptr(void *p, int32_t count) { return rt_arr_realloc(p, count, (int32_t)sizeof(void *)); }
void rt_arr_free(void *p) { free(p); }

void rt_mem_copy(void *dst, void *src, int32_t n) { memmove(dst, src, (size_t)(n < 0 ? 0 : n)); }

int32_t rt_mem_compare(void *a, void *b, int32_t n) {
  int c = memcmp(a, b, (size_t)(n < 0 ? 0 : n));
  return c < 0 ? -1 : (c > 0 ? 1 : 0);
}

/* A BASIC run-time error. ON ERROR is not modelled by the C emitter (docs/BACKENDS.md), so there is
   no handler to reach and nothing to resume to: the only honest thing left is to say which error it
   was and stop. Silently continuing would make the program's output a fiction. */
void rt_error(int32_t code) {
  fprintf(stderr, "pbc runtime: BASIC error %ld\n", (long)code);
  exit(3);
}

void rt_unreachable(void) {
  fputs("pbc runtime: reached an unreachable point\n", stderr);
  exit(2);
}

int main(void) {
  pb_main();
  fflush(stdout);
  return 0;
}
