// Checks.fs - checks of the F# port's C runtime (runtime/Crt.fs) against the system glibc.
//   crt  FILE : replay tools/crt/test/mkfmt.c's cases (printf %f %d %ld %s, strtod, sscanf) through R.snprintf,
//               R.strtod and R.sscanf and count the differences
//   libm FILE : compare Libm.* (P/Invoke into libm.so.6) and Math.Sqrt / Math.Abs, as the engine calls them,
//               bit for bit with vectors computed by C (tools/libm/test/mkvectors.c, mkedge.c, mkexact.c);
//               NaN results must agree in sign
// Written by Claude (Anthropic) for Robert Dickinson; see fsharp/README.md.
module Swmm5.Checks

open System
open System.IO
open Swmm5

let unhex (s: string) = Convert.FromHexString(s)
let hexs (b: byte[]) = Convert.ToHexString(b).ToLowerInvariant()
let cstr (b: byte[]) = CPtr(Array.append b [| 0uy |], 0)
let bytesOf (p: CPtr) = Array.sub p.B p.O (int (R.strlen p))
let bits (x: float) = (BitConverter.DoubleToInt64Bits x |> uint64).ToString("x16")

let crt (file: string) =
    let names = [| "printf %f"; "printf %d"; "printf %ld"; "printf %s"; "strtod"; "sscanf" |]
    let total = Array.zeroCreate<int> 6
    let bad = Array.zeroCreate<int> 6
    let mutable shown = 0
    let out = R.Buf(1024)
    let cache = Collections.Generic.Dictionary<string, CPtr>()
    for line in File.ReadLines(file) do
        if line <> "" then
            let p = line.Split('\t')
            let kind, fm, arg, want = p.[0], p.[1], p.[2], p.[3]
            let f =
                match cache.TryGetValue fm with
                | true, v -> v
                | _ ->
                    let v = R.Lit(fm)
                    cache.[fm] <- v
                    v
            let k, got =
                match kind with
                | "f" ->
                    let x = BitConverter.Int64BitsToDouble(Convert.ToInt64(arg, 16))
                    if fm = "%*.*f" then R.snprintf(out, 1024UL, f, [| box 9; box 3; box x |]) |> ignore
                    else R.snprintf(out, 1024UL, f, [| box x |]) |> ignore
                    0, hexs (bytesOf out)
                | "i" ->
                    R.snprintf(out, 1024UL, f, [| box (int (Convert.ToUInt32(arg, 16))) |]) |> ignore
                    1, hexs (bytesOf out)
                | "l" ->
                    R.snprintf(out, 1024UL, f, [| box (Convert.ToInt64(arg, 16)) |]) |> ignore
                    2, hexs (bytesOf out)
                | "s" ->
                    R.snprintf(out, 1024UL, f, [| box (cstr (unhex arg)) |]) |> ignore
                    3, hexs (bytesOf out)
                | "d" ->
                    let s = cstr (unhex arg)
                    let mutable e : CPtr = null
                    let v = R.strtod(s, &e)
                    4, sprintf "%s:%d" (bits v) (e.O - s.O)
                | "n" ->
                    let s = cstr (unhex arg)
                    let i1 = IBox(-77)
                    let d1 = DBox(-77.0)
                    let b = R.Buf(32)
                    b.[0] <- 63
                    let n = R.sscanf(s, R.Lit("%d %lf %s"), [| box i1; box d1; box b |])
                    5, sprintf "%d:%d:%s:%s" n i1.v (bits d1.v) (hexs (bytesOf b))
                | _ -> -1, ""
            if k >= 0 then
                total.[k] <- total.[k] + 1
                if got <> want then
                    bad.[k] <- bad.[k] + 1
                    if shown < 25 then
                        shown <- shown + 1
                        eprintfn "  %s %s arg=%s  glibc=%s  fsharp=%s" kind fm arg want got
    for k in 0 .. 5 do printfn "%s: %d cases, %d differ" names.[k] total.[k] bad.[k]
    printfn "%d of %d cases differ" (Array.sum bad) (Array.sum total)

let libm (file: string) =
    let buf = File.ReadAllBytes(file)
    let mutable p = 0
    let mutable totalBad = 0
    let mutable total = 0
    let f1 (name: string) : (float -> float) option =
        match name with
        | "exp" -> Some (fun x -> Libm.exp x) | "log" -> Some (fun x -> Libm.log x) | "log10" -> Some (fun x -> Libm.log10 x)
        | "sin" -> Some (fun x -> Libm.sin x) | "cos" -> Some (fun x -> Libm.cos x) | "tan" -> Some (fun x -> Libm.tan x)
        | "asin" -> Some (fun x -> Libm.asin x) | "acos" -> Some (fun x -> Libm.acos x) | "atan" -> Some (fun x -> Libm.atan x)
        | "floor" -> Some (fun x -> Libm.floor x) | "ceil" -> Some (fun x -> Libm.ceil x)
        | "sqrt" -> Some (fun x -> Math.Sqrt x) | "fabs" -> Some (fun x -> Math.Abs x)
        | _ -> None
    while p < buf.Length do
        let mutable e = p
        while buf.[e] <> 0uy do e <- e + 1
        let name = Text.Encoding.ASCII.GetString(buf, p, e - p)
        p <- e + 1
        let n = int (BitConverter.ToUInt32(buf, p))
        p <- p + 4
        let na = if name = "pow" then 2 elif name = "fma" then 3 else 1
        if name = "fma" then p <- p + n * 8 * (na + 1)
        else
            let mutable bad = 0
            let mutable nanSign = 0
            let mutable shown = 0
            let g = f1 name
            for i in 0 .. n - 1 do
                let a = BitConverter.ToDouble(buf, p)
                let b = if na > 1 then BitConverter.ToDouble(buf, p + 8) else 0.0
                let want = BitConverter.ToDouble(buf, p + 8 * na)
                p <- p + 8 * (na + 1)
                let got = if na = 2 then Libm.pow(a, b) else g.Value a
                if Double.IsNaN got && Double.IsNaN want then
                    if Double.IsNegative got <> Double.IsNegative want then nanSign <- nanSign + 1
                elif BitConverter.DoubleToInt64Bits got <> BitConverter.DoubleToInt64Bits want then
                    bad <- bad + 1
                    if shown < 4 then
                        shown <- shown + 1
                        printfn "  %s(%.17g, %.17g) = %.17g, libm %.17g" name a b got want
            total <- total + n
            totalBad <- totalBad + bad + nanSign
            printfn "%-6s %d of %d differ, %d NaN sign" name bad n nanSign
    printfn "all: %d of %d differ" totalBad total

[<EntryPoint>]
let main argv =
    match argv.[0] with
    | "crt" -> crt argv.[1]
    | "libm" -> libm argv.[1]
    | _ -> failwith "crt FILE | libm FILE"
    0
