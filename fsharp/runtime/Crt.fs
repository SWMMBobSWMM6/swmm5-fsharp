// Crt.fs - C runtime layer for the F# port of EPA SWMM 5.2.4.
//
// Reproduces the glibc and x86-64 behaviour SWMM's output depends on: printf formatting with glibc's
// rounding (round half to even on the exact binary value), strtod/strtol/sscanf parsing as glibc reads
// numbers (decimal, hexadecimal, inf, nan and nan(payload)), NUL-terminated byte strings, stdio files with
// C's positioning and end-of-file rules, and x86-64's float-to-integer conversion. The math functions are
// glibc's own: Libm calls the system libm (libm.so.6) by P/Invoke.
// Written in F# by Claude (Anthropic) for Robert Dickinson, after the C# port's runtime
// (cs/runtime/CRuntime.cs) and the JavaScript port's (js/swmm5/crt.js); see fsharp/README.md.
namespace Swmm5

open System
open System.IO
open System.Text
open System.Numerics
open System.Globalization
open System.Runtime.InteropServices

/// C char*: a byte buffer plus an offset. Reading gives a signed char, as x86 C does.
[<AllowNullLiteral; Sealed>]
type CPtr(b: byte[], o: int) =
    member _.B = b
    member _.O = o
    member _.Item
        with get (i: int) : int = int (sbyte b.[o + i])
        and set (i: int) (v: int) = b.[o + i] <- byte v
    member _.Add(n: int) : CPtr = CPtr(b, o + n)
    override _.ToString() =
        let sb = StringBuilder()
        let mutable i = o
        while i < b.Length && b.[i] <> 0uy do
            sb.Append(char b.[i]) |> ignore
            i <- i + 1
        sb.ToString()

/// boxes for sscanf targets and other C pointers to one number (copied back by the generated code)
[<AllowNullLiteral; Sealed>]
type IBox =
    val mutable v : int
    new(x) = { v = x }
[<AllowNullLiteral; Sealed>]
type LBox =
    val mutable v : int64
    new(x) = { v = x }
[<AllowNullLiteral; Sealed>]
type DBox =
    val mutable v : float
    new(x) = { v = x }
[<AllowNullLiteral; Sealed>]
type FBox =
    val mutable v : float32
    new(x) = { v = x }

/// C FILE*
[<AllowNullLiteral; Sealed>]
type CFile(s: Stream, std: bool) =
    member _.S = s
    member val Eof = false with get, set
    member _.IsStd = std

/// odesolve.c's derivative callback: derivs(x, y[], dydx[])
type OdeDerivs = delegate of float * float[] * float[] -> unit

/// glibc's math functions, called in the system libm (the library EPA's runswmm calls) by P/Invoke.
/// The symbol names bind to libm's default versions (exp@@GLIBC_2.29, ...), the versions EPA imports.
module Libm =
    [<DllImport("libm.so.6", EntryPoint = "exp"); SuppressGCTransition>]
    extern double exp(double x)
    [<DllImport("libm.so.6", EntryPoint = "log"); SuppressGCTransition>]
    extern double log(double x)
    [<DllImport("libm.so.6", EntryPoint = "log10"); SuppressGCTransition>]
    extern double log10(double x)
    [<DllImport("libm.so.6", EntryPoint = "pow"); SuppressGCTransition>]
    extern double pow(double x, double y)
    [<DllImport("libm.so.6", EntryPoint = "sin"); SuppressGCTransition>]
    extern double sin(double x)
    [<DllImport("libm.so.6", EntryPoint = "cos"); SuppressGCTransition>]
    extern double cos(double x)
    [<DllImport("libm.so.6", EntryPoint = "tan"); SuppressGCTransition>]
    extern double tan(double x)
    [<DllImport("libm.so.6", EntryPoint = "asin"); SuppressGCTransition>]
    extern double asin(double x)
    [<DllImport("libm.so.6", EntryPoint = "acos"); SuppressGCTransition>]
    extern double acos(double x)
    [<DllImport("libm.so.6", EntryPoint = "atan"); SuppressGCTransition>]
    extern double atan(double x)
    [<DllImport("libm.so.6", EntryPoint = "floor"); SuppressGCTransition>]
    extern double floor(double x)
    [<DllImport("libm.so.6", EntryPoint = "ceil"); SuppressGCTransition>]
    extern double ceil(double x)

[<AbstractClass; Sealed>]
type R private () =
    static let mutable tokNext : CPtr = null
    static let tmp8 : byte[] = Array.zeroCreate 8
    static let rnd = Random(Environment.ProcessId ^^^ Environment.TickCount)

    // the targets C code passes for NULL pointers to numbers (never read back)
    [<DefaultValue>] static val mutable private dummyD : float
    [<DefaultValue>] static val mutable private dummyI : int
    static member DummyD with get () = R.dummyD and set (v: float) = R.dummyD <- v
    static member DummyI with get () = R.dummyI and set (v: int) = R.dummyI <- v

    // ---------------------------------------------------------------- x86-64 conversions (cvttsd2si)
    /// (int)x: NaN or out of range gives INT_MIN, the "integer indefinite" value
    static member D2I(x: float) : int =
        if not (x > -2147483649.0 && x < 2147483648.0) then Int32.MinValue else int x
    /// (long)x: NaN or out of range gives LONG_MIN
    static member D2L(x: float) : int64 =
        if not (x > -9223372036854775809.0 && x < 9223372036854775808.0) then Int64.MinValue else int64 x

    // ---------------------------------------------------------------- memory
    static member Lit(s: string) : CPtr =
        let b = Array.zeroCreate<byte> (s.Length + 1)
        for i in 0 .. s.Length - 1 do b.[i] <- byte s.[i]
        CPtr(b, 0)
    static member Buf(n: int) : CPtr = CPtr(Array.zeroCreate (max n 1), 0)
    static member BufFrom(lit: CPtr, n: int) : CPtr =
        let p = R.Buf(n)
        let mutable i = 0
        while i < n && lit.B.[lit.O + i] <> 0uy do
            p.B.[i] <- lit.B.[lit.O + i]
            i <- i + 1
        p
    static member BufOf(n: int, vals: int[]) : CPtr =
        let p = R.Buf(n)
        for i in 0 .. vals.Length - 1 do p.B.[i] <- byte vals.[i]
        p
    static member CopyBuf(p: CPtr) : CPtr = if isNull p then p else CPtr(Array.copy p.B, p.O)
    static member NewBufs(n: int, m: int) : CPtr[] = Array.init n (fun _ -> R.Buf(m))
    static member Jagged<'T>(n: int, m: int) : 'T[][] = Array.init n (fun _ -> Array.zeroCreate<'T> m)
    static member CloneJagged<'T>(a: 'T[][]) : 'T[][] = Array.map Array.copy a
    static member CloneBufs(a: CPtr[]) : CPtr[] = Array.map R.CopyBuf a
    static member NewArray<'T>(n: int, mk: unit -> 'T) : 'T[] = Array.init (max n 0) (fun _ -> mk ())
    /// realloc of an array: the old elements, then new ones (made by mk, or zero/null)
    static member Realloc<'T>(a: 'T[], n: int, mk: unit -> 'T) : 'T[] =
        let c = Array.zeroCreate<'T> n
        let m = if isNull a then 0 else min a.Length n
        if m > 0 then Array.blit a 0 c 0 m
        for i in m .. n - 1 do c.[i] <- mk ()
        c
    static member Realloc<'T>(a: 'T[], n: int) : 'T[] =
        let c = Array.zeroCreate<'T> n
        let m = if isNull a then 0 else min a.Length n
        if m > 0 then Array.blit a 0 c 0 m
        c
    static member ReallocBuf(p: CPtr, n: int) : CPtr =
        let q = R.Buf(n)
        if not (isNull p) then Array.blit p.B p.O q.B 0 (min n (p.B.Length - p.O))
        q
    static member memset(a: float[], v: int, n: uint64) : unit =
        let cnt = int (n / 8UL)
        let mutable i = 0
        while i < cnt && i < a.Length do
            a.[i] <- 0.0
            i <- i + 1
    static member memset(a: CPtr, v: int, n: uint64) : unit =
        for i in 0 .. int n - 1 do a.B.[a.O + i] <- byte v

    // ---------------------------------------------------------------- pointers
    static member peq(a: CPtr, b: CPtr) : bool =
        Object.ReferenceEquals(a, b) ||
        (not (isNull a) && not (isNull b) && Object.ReferenceEquals(a.B, b.B) && a.O = b.O)
    static member Diff(a: CPtr, b: CPtr) : int64 = int64 (a.O - b.O)

    // ---------------------------------------------------------------- strings
    static member Str(p: CPtr) : string = p.ToString()
    static member strlen(p: CPtr) : uint64 =
        let mutable i = p.O
        while p.B.[i] <> 0uy do i <- i + 1
        uint64 (i - p.O)
    static member strcpy(d: CPtr, s: CPtr) : CPtr =
        let n = int (R.strlen s)
        Array.Copy(s.B, s.O, d.B, d.O, n + 1)     // Array.Copy handles an overlap as memmove does
        d
    static member strcat(d: CPtr, s: CPtr) : CPtr =
        R.strcpy(d.Add(int (R.strlen d)), s) |> ignore
        d
    static member strcmp(a: CPtr, b: CPtr) : int =
        let mutable i = 0
        let mutable r = 0
        let mutable go = true
        while go do
            let x = int a.B.[a.O + i]
            let y = int b.B.[b.O + i]
            if x <> y then
                r <- x - y
                go <- false
            elif x = 0 then go <- false
            else i <- i + 1
        r
    static member strchr(s: CPtr, c: int) : CPtr =
        let ch = byte c
        let mutable i = s.O
        let mutable r : CPtr = null
        let mutable go = true
        while go do
            if s.B.[i] = ch then
                r <- CPtr(s.B, i)
                go <- false
            elif s.B.[i] = 0uy then go <- false
            else i <- i + 1
        r
    static member strrchr(s: CPtr, c: int) : CPtr =
        let ch = byte c
        let mutable r : CPtr = null
        let mutable i = s.O
        let mutable go = true
        while go do
            if s.B.[i] = ch then r <- CPtr(s.B, i)
            if s.B.[i] = 0uy then go <- false else i <- i + 1
        r
    static member strstr(h: CPtr, n: CPtr) : CPtr =
        let nl = int (R.strlen n)
        if nl = 0 then h
        else
            let mutable r : CPtr = null
            let mutable i = h.O
            while isNull r && h.B.[i] <> 0uy do
                let mutable j = 0
                while j < nl && h.B.[i + j] = n.B.[n.O + j] do j <- j + 1
                if j = nl then r <- CPtr(h.B, i)
                i <- i + 1
            r
    static member private IsIn(c: byte, set: CPtr) : bool =
        let mutable j = set.O
        let mutable r = false
        while not r && set.B.[j] <> 0uy do
            if set.B.[j] = c then r <- true
            j <- j + 1
        r
    static member strcspn(s: CPtr, reject: CPtr) : uint64 =
        let mutable i = 0
        while s.B.[s.O + i] <> 0uy && not (R.IsIn(s.B.[s.O + i], reject)) do i <- i + 1
        uint64 i
    static member strtok(s: CPtr, delim: CPtr) : CPtr =
        if not (isNull s) then tokNext <- s
        if isNull tokNext then null
        else
            let b = tokNext.B
            let mutable i = tokNext.O
            while b.[i] <> 0uy && R.IsIn(b.[i], delim) do i <- i + 1
            if b.[i] = 0uy then
                tokNext <- null
                null
            else
                let start = i
                while b.[i] <> 0uy && not (R.IsIn(b.[i], delim)) do i <- i + 1
                if b.[i] <> 0uy then
                    b.[i] <- 0uy
                    tokNext <- CPtr(b, i + 1)
                else tokNext <- null
                CPtr(b, start)
    static member toupper(c: int) : int = if c >= 97 && c <= 122 then c - 32 else c

    // ---------------------------------------------------------------- numbers from text (glibc strtod/strtol)
    static member private IsSpace(c: int) = c = 32 || (c >= 9 && c <= 13)
    static member private IsDigit(c: int) = c >= 48 && c <= 57
    static member private Low(c: int) = if c >= 65 && c <= 90 then c ||| 0x20 else c
    static member private HexVal(c: int) =
        if c >= 48 && c <= 57 then c - 48
        else
            let l = R.Low c
            if l >= 97 && l <= 102 then l - 87 else -1
    /// the double nearest m * 2^e (m >= 0), ties to even, subnormals and overflow as IEEE
    static member RoundBinary(m: BigInteger, e: int) : float =
        if m.IsZero then 0.0
        else
            let n = int (m.GetBitLength())
            let top = n - 1 + e
            if top > 1023 then Double.PositiveInfinity
            else
                let shift = if top >= -1022 then n - 53 else -1074 - e
                let mutable q = m
                let mutable k = e
                if shift > 0 then
                    q <- m >>> shift
                    let rem = m - (q <<< shift)
                    let half = BigInteger.One <<< (shift - 1)
                    if rem > half || (rem = half && not q.IsEven) then q <- q + BigInteger.One
                    k <- e + shift
                if q.IsZero then 0.0
                elif k + int (q.GetBitLength()) - 1 > 1023 then Double.PositiveInfinity
                else
                    let qd = float q                      // q < 2^54: exact
                    if k >= -1022 then qd * BitConverter.Int64BitsToDouble(int64 (k + 1023) <<< 52)
                    else qd * BitConverter.Int64BitsToDouble(int64 (k + 600 + 1023) <<< 52) * BitConverter.Int64BitsToDouble(int64 (-600 + 1023) <<< 52)
    /// strtoull(b[i..e), &end, 0) when it consumes exactly [i, e) (glibc's strtod_nan); otherwise None
    static member private StrtoullBase0(b: byte[], i: int, e: int) : uint64 option =
        let mutable bas = 10
        let mutable k = i
        if k < e && b.[k] = 48uy then
            if k + 2 < e && R.Low(int b.[k + 1]) = 120 && R.HexVal(int b.[k + 2]) >= 0 then
                bas <- 16
                k <- k + 2
            else bas <- 8
        if k = e then Some 0UL
        else
            let mutable v = BigInteger.Zero
            let mutable ok = true
            while ok && k < e do
                let h = R.HexVal(int b.[k])
                if h < 0 || h >= bas then ok <- false
                else
                    v <- v * BigInteger(bas) + BigInteger(h)
                    k <- k + 1
            if not ok then None
            elif v > BigInteger(UInt64.MaxValue) then Some UInt64.MaxValue
            else Some (uint64 v)
    /// parse a C floating-point number at b[o..] (at most maxw characters after any leading space when
    /// skipSpace); returns the number of characters consumed (0: no number) and the value
    static member ScanDouble(b: byte[], o: int, maxw: int, skipSpace: bool, v: byref<float>) : int =
        v <- 0.0
        let mutable i = o
        let n = b.Length
        if skipSpace then
            while i < n && R.IsSpace(int b.[i]) do i <- i + 1
        let lim = if maxw = Int32.MaxValue then n else min n (i + maxw)
        let g (k: int) = if k < lim then int b.[k] else 0
        let mutable neg = false
        if g i = 43 || g i = 45 then
            neg <- (g i = 45)
            i <- i + 1
        if R.Low(g i) = 105 && R.Low(g (i + 1)) = 110 && R.Low(g (i + 2)) = 102 then
            i <- i + 3
            if R.Low(g i) = 105 && R.Low(g (i + 1)) = 110 && R.Low(g (i + 2)) = 105 && R.Low(g (i + 3)) = 116 && R.Low(g (i + 4)) = 121 then
                i <- i + 5
            v <- (if neg then Double.NegativeInfinity else Double.PositiveInfinity)
            i - o
        elif R.Low(g i) = 110 && R.Low(g (i + 1)) = 97 && R.Low(g (i + 2)) = 110 then
            i <- i + 3
            let mutable bits = 0x7ff8000000000000UL
            if g i = 40 then
                let mutable k = i + 1
                while R.IsDigit(g k) || (R.Low(g k) >= 97 && R.Low(g k) <= 122) || g k = 95 do k <- k + 1
                if g k = 41 then
                    match R.StrtoullBase0(b, i + 1, k) with
                    | Some p -> bits <- bits ||| (p &&& 0x7ffffffffffffUL)
                    | None -> ()
                    i <- k + 1
            if neg then bits <- bits ||| 0x8000000000000000UL
            v <- BitConverter.Int64BitsToDouble(int64 bits)
            i - o
        elif g i = 48 && R.Low(g (i + 1)) = 120 then
            // hexadecimal: 0x h.hhh p+-d, rounded exactly
            let mutable k = i + 2
            let mutable m = BigInteger.Zero
            let mutable nd = 0
            let mutable e = 0
            let mutable seenDot = false
            let mutable go = true
            while go do
                let c = g k
                if c = 46 && not seenDot then
                    seenDot <- true
                    k <- k + 1
                else
                    let h = R.HexVal c
                    if h < 0 then go <- false
                    else
                        m <- m * BigInteger(16) + BigInteger(h)
                        nd <- nd + 1
                        if seenDot then e <- e - 4
                        k <- k + 1
            if nd = 0 then
                v <- (if neg then -0.0 else 0.0)              // "0x" alone: the "0" is the number
                i + 1 - o
            else
                if R.Low(g k) = 112 then
                    let mutable j = k + 1
                    let mutable eneg = false
                    if g j = 43 || g j = 45 then
                        eneg <- (g j = 45)
                        j <- j + 1
                    if R.IsDigit(g j) then
                        let mutable x = 0
                        while R.IsDigit(g j) do
                            if x < 100000 then x <- x * 10 + g j - 48
                            j <- j + 1
                        e <- e + (if eneg then -x else x)
                        k <- j
                let r = R.RoundBinary(m, e)
                v <- (if neg then -r else r)
                k - o
        else
            let ms = i
            let mutable nd = 0
            while R.IsDigit(g i) do
                i <- i + 1
                nd <- nd + 1
            if g i = 46 then
                i <- i + 1
                while R.IsDigit(g i) do
                    i <- i + 1
                    nd <- nd + 1
            if nd = 0 then 0
            else
                if R.Low(g i) = 101 then
                    let mutable k = i + 1
                    if g k = 43 || g k = 45 then k <- k + 1
                    if R.IsDigit(g k) then
                        while R.IsDigit(g k) do k <- k + 1
                        i <- k
                let txt = Encoding.ASCII.GetString(b, ms, i - ms)
                let r = Double.Parse(txt, NumberStyles.Float, CultureInfo.InvariantCulture)   // correctly rounded
                v <- (if neg then -r else r)
                i - o
    static member strtod(s: CPtr, endp: byref<CPtr>) : float =
        let mutable v = 0.0
        let n = R.ScanDouble(s.B, s.O, Int32.MaxValue, true, &v)
        endp <- (if n = 0 then s else CPtr(s.B, s.O + n))
        v
    static member strtod(s: CPtr) : float =
        let mutable e : CPtr = null
        R.strtod(s, &e)
    static member atof(s: CPtr) : float = R.strtod(s)
    /// the long value of the decimal digits b[ds..de) with a sign, saturated as strtol saturates
    static member private StrtolDigits(b: byte[], ds: int, de: int, neg: bool) : int64 =
        let mutable v = BigInteger.Zero
        for k in ds .. de - 1 do v <- v * BigInteger(10) + BigInteger(int b.[k] - 48)
        if neg then v <- -v
        if v > BigInteger(Int64.MaxValue) then Int64.MaxValue
        elif v < BigInteger(Int64.MinValue) then Int64.MinValue
        else int64 v
    static member atoi(s: CPtr) : int =
        let b = s.B
        let mutable i = s.O
        while b.[i] <> 0uy && R.IsSpace(int b.[i]) do i <- i + 1
        let mutable neg = false
        if b.[i] = 43uy || b.[i] = 45uy then
            neg <- (b.[i] = 45uy)
            i <- i + 1
        let ds = i
        while R.IsDigit(int b.[i]) do i <- i + 1
        int (R.StrtolDigits(b, ds, i, neg))       // strtol's long, then (int): the low 32 bits

    // ---------------------------------------------------------------- sscanf (the conversions SWMM uses)
    /// targets: IBox (int), LBox (long), DBox (double), FBox (float) for numbers, CPtr for %s and %c
    static member sscanf(s: CPtr, fmt: CPtr, args: obj[]) : int =
        let b = s.B
        let f = fmt.B
        let n = b.Length
        let ch (j: int) = if j < n then int b.[j] else 0
        let mutable i = s.O
        let mutable k = fmt.O
        let mutable ai = 0
        let mutable count = 0
        let mutable anyInput = false
        let mutable result = Int32.MinValue        // set when the scan stops early
        while result = Int32.MinValue && f.[k] <> 0uy do
            let fc = int f.[k]
            if R.IsSpace fc then
                while R.IsSpace(int f.[k]) do k <- k + 1
                while ch i <> 0 && R.IsSpace(ch i) do i <- i + 1
            elif fc <> 37 then
                if ch i = 0 then result <- (if anyInput || count > 0 then count else -1)
                elif ch i <> fc then result <- count
                else
                    i <- i + 1
                    k <- k + 1
            else
                k <- k + 1
                if f.[k] = 37uy then
                    while ch i <> 0 && R.IsSpace(ch i) do i <- i + 1
                    if ch i <> 37 then result <- count
                    else
                        i <- i + 1
                        k <- k + 1
                else
                    let mutable suppress = false
                    if f.[k] = 42uy then
                        suppress <- true
                        k <- k + 1
                    let mutable width = 0
                    while R.IsDigit(int f.[k]) do
                        width <- width * 10 + int f.[k] - 48
                        k <- k + 1
                    let mutable lmod = 0
                    while f.[k] = 108uy || f.[k] = 104uy || f.[k] = 76uy do
                        if f.[k] = 108uy then lmod <- lmod + 1
                        k <- k + 1
                    let conv = int f.[k]
                    k <- k + 1
                    if conv <> 99 && conv <> 91 && conv <> 110 then
                        while ch i <> 0 && R.IsSpace(ch i) do i <- i + 1
                    if ch i = 0 && conv <> 110 then result <- (if count = 0 && not anyInput then -1 else count)
                    elif conv = 100 || conv = 105 || conv = 117 then          // d i u
                        let mutable j = i
                        let lim = if width = 0 then n else i + width
                        let mutable neg = false
                        if j < lim && (ch j = 43 || ch j = 45) then
                            neg <- (ch j = 45)
                            j <- j + 1
                        let ds = j
                        while j < lim && R.IsDigit(ch j) do j <- j + 1
                        if j = ds then result <- count
                        else
                            let lv = R.StrtolDigits(b, ds, j, neg)
                            i <- j
                            anyInput <- true
                            if not suppress then
                                match args.[ai] with
                                | :? LBox as a -> a.v <- lv
                                | :? IBox as a -> a.v <- int lv
                                | _ -> failwith "sscanf: integer target"
                                ai <- ai + 1
                                count <- count + 1
                    elif conv = 102 || conv = 101 || conv = 103 then           // f e g
                        let mutable v = 0.0
                        let used = R.ScanDouble(b, i, (if width = 0 then Int32.MaxValue else width), false, &v)
                        if used = 0 then result <- count
                        else
                            i <- i + used
                            anyInput <- true
                            if not suppress then
                                match args.[ai] with
                                | :? DBox as a -> a.v <- v
                                | :? FBox as a -> a.v <- float32 v
                                | _ -> failwith "sscanf: floating target"
                                ai <- ai + 1
                                count <- count + 1
                    elif conv = 115 then                                        // s
                        let w = if width = 0 then Int32.MaxValue else width
                        let mutable j = i
                        let mutable m = 0
                        let dst = if suppress then null else (args.[ai] :?> CPtr)
                        while ch j <> 0 && not (R.IsSpace(ch j)) && m < w do
                            if not suppress then dst.B.[dst.O + m] <- byte (ch j)
                            j <- j + 1
                            m <- m + 1
                        if not suppress then
                            dst.B.[dst.O + m] <- 0uy
                            ai <- ai + 1
                            count <- count + 1
                        i <- j
                        anyInput <- true
                    elif conv = 99 then                                         // c
                        let cnt = if width = 0 then 1 else width
                        let a = if suppress then null else args.[ai]
                        let mutable t = 0
                        while t < cnt && ch i <> 0 do
                            if not suppress then
                                match a with
                                | :? CPtr as p -> p.B.[p.O + t] <- byte (ch i)
                                | :? IBox as ib -> ib.v <- int (sbyte (ch i))
                                | _ -> failwith "sscanf: char target"
                            i <- i + 1
                            t <- t + 1
                        anyInput <- true
                        if not suppress then
                            ai <- ai + 1
                            count <- count + 1
                    else failwithf "sscanf %%%c" (char conv)
        if result = Int32.MinValue then count else result

    // ---------------------------------------------------------------- printf family
    static member private ToLong(a: obj) : int64 =
        match a with
        | :? int as i -> int64 i
        | :? int64 as l -> l
        | :? uint32 as u -> int64 u
        | :? uint64 as ul -> int64 ul
        | :? float as d -> int64 d
        | _ -> Convert.ToInt64(a)
    static member private ToULong(a: obj, lmod: bool) : uint64 =
        match a with
        | :? int as i -> if lmod then uint64 (int64 i) else uint64 (uint32 i)
        | :? int64 as l -> if lmod then uint64 l else uint64 (uint32 l)
        | :? uint32 as u -> uint64 u
        | :? uint64 as ul -> if lmod then ul else uint64 (uint32 ul)
        | _ -> Convert.ToUInt64(a)
    static member private ToDouble(a: obj) : float =
        match a with
        | :? float as d -> d
        | :? float32 as f -> float f
        | :? int as i -> float i
        | :? int64 as l -> float l
        | _ -> Convert.ToDouble(a)
    static member private Decompose(a: float, m: byref<BigInteger>, e: byref<int>) : unit =
        let bits = BitConverter.DoubleToInt64Bits(a)
        let ex = int ((bits >>> 52) &&& 0x7FFL)
        let frac = bits &&& 0xFFFFFFFFFFFFFL
        if ex = 0 then
            m <- BigInteger(frac)
            e <- -1074
        else
            m <- BigInteger(frac ||| (1L <<< 52))
            e <- ex - 1075
    /// round_half_even(a * 10^p), exactly, for p of either sign
    static member private ScaledRound(a: float, p: int) : BigInteger =
        let mutable m = BigInteger.Zero
        let mutable e = 0
        R.Decompose(a, &m, &e)
        let mutable num = m
        let mutable den = BigInteger.One
        if e >= 0 then num <- num <<< e else den <- den <<< (-e)
        if p >= 0 then num <- num * BigInteger.Pow(BigInteger(10), p) else den <- den * BigInteger.Pow(BigInteger(10), -p)
        let mutable r = BigInteger.Zero
        let mutable q = BigInteger.DivRem(num, den, &r)
        let c = (r * BigInteger(2)).CompareTo(den)
        if c > 0 || (c = 0 && not q.IsEven) then q <- q + BigInteger.One
        q
    static member private FixedDigits(a: float, prec: int, alt: bool) : string =
        let d = R.ScaledRound(a, prec).ToString(CultureInfo.InvariantCulture)
        if prec = 0 then (if alt then d + "." else d)
        else
            let d = if d.Length <= prec then String('0', prec - d.Length + 1) + d else d
            d.Substring(0, d.Length - prec) + "." + d.Substring(d.Length - prec)
    static member private ExpDigitsRaw(a: float, prec: int, x: byref<int>) : string =
        if a = 0.0 then
            x <- 0
            String('0', prec + 1)
        else
            let mutable E = int (Math.Floor(Math.Log10(a)))
            let mutable res : string = null
            let mutable iter = 0
            while isNull res && iter < 4 do
                let d = R.ScaledRound(a, prec - E).ToString(CultureInfo.InvariantCulture)
                if d.Length = prec + 1 then res <- d
                elif d.Length > prec + 1 then E <- E + 1
                else E <- E - 1
                iter <- iter + 1
            if isNull res then res <- R.ScaledRound(a, prec - E).ToString(CultureInfo.InvariantCulture)
            x <- E
            res
    static member private ExpDigits(a: float, prec: int, alt: bool, upper: bool) : string =
        let mutable X = 0
        let d = R.ExpDigitsRaw(a, prec, &X)
        let mant = d.Substring(0, 1) + (if prec > 0 then "." + d.Substring(1) elif alt then "." else "")
        let ax = abs X
        mant + (if upper then "E" else "e") + (if X < 0 then "-" else "+") + (if ax < 10 then "0" + string ax else string ax)
    /// exact decimal text of a finite non-negative double for %f %e %g, glibc's rounding
    static member FormatFloat(a: float, conv: int, prec: int, alt: bool) : string =
        let lc = conv ||| 0x20
        if lc = 102 then R.FixedDigits(a, prec, alt)
        elif lc = 101 then R.ExpDigits(a, prec, alt, (conv = 69))
        else
            let P = if prec = 0 then 1 else prec
            let mutable X = 0
            if a <> 0.0 then R.ExpDigitsRaw(a, P - 1, &X) |> ignore
            let s = if P > X && X >= -4 then R.FixedDigits(a, P - 1 - X, alt) else R.ExpDigits(a, P - 1, alt, (conv = 71))
            if alt then s
            else
                let epos = s.IndexOfAny([| 'e'; 'E' |])
                let mutable mant = if epos >= 0 then s.Substring(0, epos) else s
                let ex = if epos >= 0 then s.Substring(epos) else ""
                if mant.Contains('.') then
                    mant <- mant.TrimEnd('0')
                    if mant.EndsWith(".") then mant <- mant.Substring(0, mant.Length - 1)
                mant + ex
    /// the bytes printf writes for fmt and its arguments
    static member Format(fmt: CPtr, args: obj[]) : byte[] =
        let o = StringBuilder(128)
        let f = fmt.B
        let mutable k = fmt.O
        let mutable ai = 0
        while f.[k] <> 0uy do
            if f.[k] <> 37uy then
                o.Append(char f.[k]) |> ignore
                k <- k + 1
            else
                k <- k + 1
                if f.[k] = 37uy then
                    o.Append('%') |> ignore
                    k <- k + 1
                else
                    let mutable left = false
                    let mutable plus = false
                    let mutable space = false
                    let mutable alt = false
                    let mutable zero = false
                    let mutable flags = true
                    while flags do
                        match f.[k] with
                        | 45uy -> left <- true; k <- k + 1
                        | 43uy -> plus <- true; k <- k + 1
                        | 32uy -> space <- true; k <- k + 1
                        | 35uy -> alt <- true; k <- k + 1
                        | 48uy -> zero <- true; k <- k + 1
                        | _ -> flags <- false
                    let mutable width = 0
                    let mutable prec = -1
                    if f.[k] = 42uy then
                        width <- int (R.ToLong args.[ai])
                        ai <- ai + 1
                        if width < 0 then
                            left <- true
                            width <- -width
                        k <- k + 1
                    else
                        while R.IsDigit(int f.[k]) do
                            width <- width * 10 + int f.[k] - 48
                            k <- k + 1
                    if f.[k] = 46uy then
                        k <- k + 1
                        prec <- 0
                        if f.[k] = 42uy then
                            prec <- int (R.ToLong args.[ai])
                            ai <- ai + 1
                            if prec < 0 then prec <- -1
                            k <- k + 1
                        else
                            while R.IsDigit(int f.[k]) do
                                prec <- prec * 10 + int f.[k] - 48
                                k <- k + 1
                    let mutable lmod = false
                    while f.[k] = 108uy || f.[k] = 104uy || f.[k] = 76uy || f.[k] = 122uy || f.[k] = 106uy || f.[k] = 116uy || f.[k] = 113uy do
                        if f.[k] = 108uy then lmod <- true
                        k <- k + 1
                    let conv = int f.[k]
                    k <- k + 1
                    let mutable body = ""
                    let mutable sign = ""
                    let mutable numeric = true
                    if conv = 100 || conv = 105 then                        // d i
                        let v = R.ToLong args.[ai]
                        ai <- ai + 1
                        let mutable bv = BigInteger(v)
                        if v < 0L then
                            sign <- "-"
                            bv <- -bv
                        elif plus then sign <- "+"
                        elif space then sign <- " "
                        body <- bv.ToString(CultureInfo.InvariantCulture)
                        if prec >= 0 then
                            zero <- false
                            if prec = 0 && v = 0L then body <- ""
                            body <- body.PadLeft(prec, '0')
                    elif conv = 117 || conv = 120 || conv = 88 || conv = 111 then   // u x X o
                        let v = R.ToULong(args.[ai], lmod)
                        ai <- ai + 1
                        body <-
                            if conv = 117 then v.ToString(CultureInfo.InvariantCulture)
                            elif conv = 111 then Convert.ToString(int64 v, 8)
                            elif conv = 120 then v.ToString("x")
                            else v.ToString("X")
                        if prec >= 0 then
                            zero <- false
                            if prec = 0 && v = 0UL then body <- ""
                            body <- body.PadLeft(prec, '0')
                    elif conv = 99 then                                       // c
                        numeric <- false
                        body <- string (char (byte (R.ToLong args.[ai])))
                        ai <- ai + 1
                    elif conv = 115 then                                      // s
                        numeric <- false
                        let p = args.[ai] :?> CPtr
                        ai <- ai + 1
                        let mutable s = if isNull p then "(null)" else p.ToString()
                        if prec >= 0 && s.Length > prec then s <- s.Substring(0, prec)
                        body <- s
                    elif conv = 102 || conv = 70 || conv = 101 || conv = 69 || conv = 103 || conv = 71 then
                        let v = R.ToDouble args.[ai]
                        ai <- ai + 1
                        if prec < 0 then prec <- 6
                        if Double.IsNegative v then sign <- "-"
                        elif plus then sign <- "+"
                        elif space then sign <- " "
                        if Double.IsNaN v || Double.IsInfinity v then
                            body <- (if Double.IsNaN v then "nan" else "inf")
                            if conv = 70 || conv = 69 || conv = 71 then body <- body.ToUpperInvariant()
                            zero <- false
                        else body <- R.FormatFloat(Math.Abs v, conv, prec, alt)
                    else failwithf "printf %%%c" (char conv)
                    let len = sign.Length + body.Length
                    if len >= width then o.Append(sign).Append(body) |> ignore
                    elif left then o.Append(sign).Append(body).Append(' ', width - len) |> ignore
                    elif zero && numeric then o.Append(sign).Append('0', width - len).Append(body) |> ignore
                    else o.Append(' ', width - len).Append(sign).Append(body) |> ignore
        let r = Array.zeroCreate<byte> o.Length
        for i in 0 .. o.Length - 1 do r.[i] <- byte o.[i]
        r
    static member fprintf(f: CFile, fmt: CPtr, args: obj[]) : int =
        let bytes = R.Format(fmt, args)
        if isNull f then -1
        else
            f.S.Write(bytes, 0, bytes.Length)
            bytes.Length
    static member snprintf(buf: CPtr, n: uint64, fmt: CPtr, args: obj[]) : int =
        let bytes = R.Format(fmt, args)
        if n > 0UL then
            let m = int (min (uint64 bytes.Length) (n - 1UL))
            Array.blit bytes 0 buf.B buf.O m
            buf.B.[buf.O + m] <- 0uy
        bytes.Length

    // ---------------------------------------------------------------- files
    static member val stdout_ : CFile = CFile(Console.OpenStandardOutput(), true) with get
    static member fopen(name: CPtr, mode: CPtr) : CFile =
        let fn = name.ToString()
        let m = mode.ToString()
        try
            let plus = m.Contains('+')
            let fs =
                if m.[0] = 'r' then new FileStream(fn, FileMode.Open, (if plus then FileAccess.ReadWrite else FileAccess.Read), FileShare.ReadWrite)
                elif m.[0] = 'w' then new FileStream(fn, FileMode.Create, (if plus then FileAccess.ReadWrite else FileAccess.Write), FileShare.ReadWrite)
                elif m.[0] = 'a' then new FileStream(fn, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
                else null
            if isNull fs then null else CFile(new BufferedStream(fs, 1 <<< 16), false)
        with _ -> null
    static member fclose(f: CFile) : int =
        if isNull f then -1
        else
            if not f.IsStd then f.S.Dispose() else f.S.Flush()
            0
    static member fflush(f: CFile) : int =
        if not (isNull f) then f.S.Flush()
        0
    static member feof(f: CFile) : int = if f.Eof then 1 else 0
    static member rewind(f: CFile) : unit =
        f.S.Seek(0L, SeekOrigin.Begin) |> ignore
        f.Eof <- false
    static member fseek(f: CFile, off: int64, whence: int) : int =
        try
            f.S.Seek(off, (if whence = 0 then SeekOrigin.Begin elif whence = 1 then SeekOrigin.Current else SeekOrigin.End)) |> ignore
            f.Eof <- false
            0
        with _ -> -1
    static member ftell(f: CFile) : int64 = f.S.Position
    static member fgets(buf: CPtr, n: int, f: CFile) : CPtr =
        let mutable i = 0
        let mutable go = true
        while go && i < n - 1 do
            let c = f.S.ReadByte()
            if c < 0 then
                f.Eof <- true
                go <- false
            else
                buf.B.[buf.O + i] <- byte c
                i <- i + 1
                if c = 10 then go <- false
        if i = 0 then null
        else
            buf.B.[buf.O + i] <- 0uy
            buf
    static member remove(name: CPtr) : int =
        try
            if File.Exists(name.ToString()) then
                File.Delete(name.ToString())
                0
            else -1
        with _ -> -1
    static member mkstemp(template: CPtr) : int =
        let n = int (R.strlen template)
        let chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
        let mutable result = -1
        let mutable attempt = 0
        while result < 0 && attempt < 1000 do
            for i in n - 6 .. n - 1 do template.B.[template.O + i] <- byte chars.[rnd.Next(chars.Length)]
            try
                use fs = new FileStream(template.ToString(), FileMode.CreateNew)
                result <- 3
            with :? IOException -> ()
            attempt <- attempt + 1
        result
    static member realpath(p: string) : string =
        try
            let full = Path.GetFullPath(p)
            let fi = FileInfo(full)
            if fi.Exists && not (isNull fi.LinkTarget) then
                match fi.ResolveLinkTarget(true) with
                | null -> full
                | t -> t.FullName
            else full
        with _ -> null

    static member private ReadExact(f: CFile, b: byte[], n: int) : bool =
        let mutable got = 0
        let mutable ok = true
        while ok && got < n do
            let r = f.S.Read(b, got, n - got)
            if r <= 0 then
                f.Eof <- true
                ok <- false
            else got <- got + r
        ok
    // fwrite/fread of typed values (little-endian, as x86)
    static member fwriteF1(v: float32, f: CFile) : uint64 = f.S.Write(BitConverter.GetBytes(v), 0, 4); 1UL
    static member fwriteD1(v: float, f: CFile) : uint64 = f.S.Write(BitConverter.GetBytes(v), 0, 8); 1UL
    static member fwriteI1(v: int, f: CFile) : uint64 = f.S.Write(BitConverter.GetBytes(v), 0, 4); 1UL
    static member fwriteL1(v: int64, f: CFile) : uint64 = f.S.Write(BitConverter.GetBytes(v), 0, 8); 1UL
    /// fwrite(&wide, n, 1, f) with n smaller than the variable: its low n bytes (output.c's off_t positions)
    static member fwriteN1(v: int64, n: int, f: CFile) : uint64 = f.S.Write(BitConverter.GetBytes(v), 0, n); 1UL
    static member fwriteF(a: float32[], off: int, n: int, f: CFile) : uint64 =
        for i in 0 .. n - 1 do f.S.Write(BitConverter.GetBytes(a.[off + i]), 0, 4)
        uint64 n
    static member fwriteD(a: float[], off: int, n: int, f: CFile) : uint64 =
        for i in 0 .. n - 1 do f.S.Write(BitConverter.GetBytes(a.[off + i]), 0, 8)
        uint64 n
    static member fwriteI(a: int[], off: int, n: int, f: CFile) : uint64 =
        for i in 0 .. n - 1 do f.S.Write(BitConverter.GetBytes(a.[off + i]), 0, 4)
        uint64 n
    static member fwriteC(p: CPtr, off: int, n: int, f: CFile) : uint64 = f.S.Write(p.B, p.O + off, n); uint64 n
    static member freadF1(v: byref<float32>, f: CFile) : uint64 =
        if R.ReadExact(f, tmp8, 4) then
            v <- BitConverter.ToSingle(tmp8, 0)
            1UL
        else 0UL
    static member freadD1(v: byref<float>, f: CFile) : uint64 =
        if R.ReadExact(f, tmp8, 8) then
            v <- BitConverter.ToDouble(tmp8, 0)
            1UL
        else 0UL
    static member freadI1(v: byref<int>, f: CFile) : uint64 =
        if R.ReadExact(f, tmp8, 4) then
            v <- BitConverter.ToInt32(tmp8, 0)
            1UL
        else 0UL
    static member freadL1(v: byref<int64>, f: CFile) : uint64 =
        if R.ReadExact(f, tmp8, 8) then
            v <- BitConverter.ToInt64(tmp8, 0)
            1UL
        else 0UL
    /// fread into the low n bytes of a wider variable; the upper bytes keep their value, as in C
    static member freadN1(v: byref<int64>, n: int, f: CFile) : uint64 =
        let b = BitConverter.GetBytes(v)
        let t = Array.zeroCreate<byte> n
        if R.ReadExact(f, t, n) then
            Array.blit t 0 b 0 n
            v <- BitConverter.ToInt64(b, 0)
            1UL
        else 0UL
    static member freadF(a: float32[], off: int, n: int, f: CFile) : uint64 =
        let mutable i = 0
        while i < n && R.ReadExact(f, tmp8, 4) do
            a.[off + i] <- BitConverter.ToSingle(tmp8, 0)
            i <- i + 1
        uint64 i
    static member freadD(a: float[], off: int, n: int, f: CFile) : uint64 =
        let mutable i = 0
        while i < n && R.ReadExact(f, tmp8, 8) do
            a.[off + i] <- BitConverter.ToDouble(tmp8, 0)
            i <- i + 1
        uint64 i
    static member freadI(a: int[], off: int, n: int, f: CFile) : uint64 =
        let mutable i = 0
        while i < n && R.ReadExact(f, tmp8, 4) do
            a.[off + i] <- BitConverter.ToInt32(tmp8, 0)
            i <- i + 1
        uint64 i
    static member freadC(p: CPtr, off: int, n: int, f: CFile) : uint64 =
        let mutable got = 0
        let mutable go = true
        while go && got < n do
            let r = f.S.Read(p.B, p.O + off + got, n - got)
            if r <= 0 then
                f.Eof <- true
                go <- false
            else got <- got + r
        uint64 got

    // ---------------------------------------------------------------- time
    static member time() : int64 = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    static member ctime(t: int64) : CPtr =
        let d = DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime()
        let s = d.ToString("ddd MMM ", CultureInfo.InvariantCulture) + (string d.Day).PadLeft(2) + d.ToString(" HH:mm:ss yyyy", CultureInfo.InvariantCulture) + "\n"
        R.Lit(s)
