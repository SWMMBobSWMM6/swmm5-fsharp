// intcheck.fsx - the lines fsharp/test/intcheck.c prints, computed as the generated F# computes them: F#'s int
// operators for / and %, R.D2I / R.D2L for (int) and (long), uint32 (R.D2L x) for (unsigned), and the narrowing
// casts the translator writes. dotnet fsi fsharp/test/intcheck.fsx. Written by Claude (Anthropic) for Robert Dickinson.
#load "../runtime/Crt.fs"
open Swmm5
let g (x: float) = if System.Double.IsNaN x then (if System.Double.IsNegative x then "-nan" else "nan") elif System.Double.IsInfinity x then (if x > 0.0 then "inf" else "-inf") else x.ToString("G6", System.Globalization.CultureInfo.InvariantCulture).Replace("E+", "e+").Replace("E-", "e-")
let a = [| -7; 7; -7; 7; -2147483647; 5 |]
let b = [| 2; -2; -2; 2; 2; -3 |]
for i in 0 .. 5 do printfn "%d / %d = %d, %% = %d" a.[i] b.[i] (a.[i] / b.[i]) (a.[i] % b.[i])
let negnan = System.BitConverter.Int64BitsToDouble(0xfff8000000000000L)
let posnan = System.BitConverter.Int64BitsToDouble(0x7ff8000000000000L)
let d = [| posnan; negnan; infinity; -infinity; 3e9; -3e9; 2147483647.9; -2147483648.9; -2147483649.0; -0.5; 1e19; -1e19; 4e9; 9.3e18 |]
for x in d do printfn "%s: (int) %d (long) %d (unsigned) %d" (g x) (R.D2I x) (R.D2L x) (uint32 (R.D2L x))
let c = [| 300; -129; 255; 65537; -1 |]
for x in c do printfn "%d: (char) %d (unsigned char) %d (short) %d (unsigned) %d (unsigned long) %d" x ((x <<< 24) >>> 24) (x &&& 255) ((x <<< 16) >>> 16) (uint32 x) (uint64 x)
