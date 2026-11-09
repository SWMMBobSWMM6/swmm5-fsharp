/* intcheck.c - integer division, remainder and double-to-integer conversions as gcc -O2 compiles them on x86-64;
   fsharp/test/intcheck.fsx prints the same lines from the F# runtime and F# operators. volatile keeps gcc from
   folding the conversions at compile time. Written by Claude (Anthropic) for Robert Dickinson. */
#include <stdio.h>
#include <math.h>
int main(void) {
    volatile int a[] = {-7, 7, -7, 7, -2147483647, 5};
    volatile int b[] = {2, -2, -2, 2, 2, -3};
    for (int i = 0; i < 6; i++) printf("%d / %d = %d, %% = %d\n", a[i], b[i], a[i] / b[i], a[i] % b[i]);
    volatile double d[] = {NAN, -NAN, INFINITY, -INFINITY, 3e9, -3e9, 2147483647.9, -2147483648.9, -2147483649.0, -0.5, 1e19, -1e19, 4e9, 9.3e18};
    for (int i = 0; i < 14; i++) {
        double x = d[i];
        printf("%g: (int) %d (long) %ld (unsigned) %u\n", x, (int)x, (long)x, (unsigned)x);
    }
    volatile int c[] = {300, -129, 255, 65537, -1};
    for (int i = 0; i < 5; i++) printf("%d: (char) %d (unsigned char) %d (short) %d (unsigned) %u (unsigned long) %lu\n", c[i], (char)c[i], (unsigned char)c[i], (short)c[i], (unsigned)c[i], (unsigned long)c[i]);
    return 0;
}
