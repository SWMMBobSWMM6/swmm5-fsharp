// Overrides.fs - hand-ported pieces of EPA SWMM 5.2.4 that use C pointer arithmetic the translator does not
// map: odesolve.c (whole file), mempool.c (its pool becomes plain byte buffers) and six helpers from swmm5.c
// and project.c. Each keeps the C statement order and floating-point expressions exactly; pow is glibc's.
// Written in F# by Claude (Anthropic) for Robert Dickinson, after the C# port's Overrides.cs; see fsharp/README.md.
namespace Swmm5

open System

[<AbstractClass; Sealed>]
type O private () =
    static let MAXFNAME = 259
    // odesolve.c's state
    static let mutable nmax = 0
    static let mutable oy : float[] = null
    static let mutable yscal : float[] = null
    static let mutable yerr : float[] = null
    static let mutable ytemp : float[] = null
    static let mutable dydx : float[] = null
    static let mutable ak2 : float[] = null
    static let mutable ak3 : float[] = null
    static let mutable ak4 : float[] = null
    static let mutable ak5 : float[] = null
    static let mutable ak6 : float[] = null

    // ------------------------------------------------------------ swmm5.c helpers
    static member sstrncpy(dest: CPtr, src: CPtr, n: uint64) : uint64 =
        let mutable offset = 0
        if n > 0UL then
            while src.[offset] <> 0 && uint64 offset <> n do
                dest.[offset] <- src.[offset]
                offset <- offset + 1
        dest.[offset] <- 0
        R.strlen(dest)

    static member sstrcat(dest: CPtr, src: CPtr, size: uint64) : uint64 =
        let mutable offset = R.strlen(dest)
        let mutable srcIndex = 0
        let mutable go = true
        while go && src.[srcIndex] <> 0 do
            dest.[int offset] <- src.[srcIndex]
            offset <- offset + 1UL
            srcIndex <- srcIndex + 1
            if offset = size - 1UL then go <- false
        dest.[int offset] <- 0
        R.strlen(dest)

    static member private isRelativePath(fname: CPtr) : bool =
        not (not (isNull (R.strchr(fname, 58))) || fname.[0] = 92 || fname.[0] = 47)

    static member getAbsolutePath(fname: CPtr, absPath: CPtr, size: uint64) : unit =
        if not (isNull fname) && R.strlen(fname) <> 0UL then
            let mutable ok = true
            if O.isRelativePath(fname) then
                // realpath(fname, absPath): the current directory joined and symbolic links resolved
                let full = R.realpath(fname.ToString())
                if isNull full then ok <- false
                else
                    let bytes = Text.Encoding.UTF8.GetBytes(full)
                    let m = min bytes.Length (absPath.B.Length - absPath.O - 1)
                    for i in 0 .. m - 1 do absPath.[i] <- int bytes.[i]
                    absPath.[m] <- 0
            else
                O.sstrncpy(absPath, fname, R.strlen(fname)) |> ignore
            if ok then
                let endOfDir = R.strrchr(absPath, 47)
                if not (isNull endOfDir) then endOfDir.[1] <- 0

    static member getTempFileName(fname: CPtr) : CPtr =
        O.sstrncpy(fname, R.Lit("swmmXXXXXX"), uint64 MAXFNAME) |> ignore
        R.mkstemp(fname) |> ignore
        fname

    // ------------------------------------------------------------ project.c matrix helpers
    static member project_createMatrix(nrows: int, ncols: int) : float[][] =
        if nrows <= 0 || ncols <= 0 then null
        else Array.init nrows (fun _ -> Array.zeroCreate<float> ncols)
    static member project_freeMatrix(a: float[][]) : unit = ()

    // ------------------------------------------------------------ mempool.c
    static member AllocInit() : obj = obj ()
    static member Alloc(n: int64) : CPtr = R.Buf(int n)
    static member AllocSetPool(p: obj) : obj = p
    static member AllocReset() : unit = ()
    static member AllocFreePool() : unit = ()

    // ------------------------------------------------------------ odesolve.c (Cash-Karp Runge-Kutta)
    static member odesolve_open(n: int) : int =
        nmax <- 0
        oy <- Array.zeroCreate n; yscal <- Array.zeroCreate n; dydx <- Array.zeroCreate n
        yerr <- Array.zeroCreate n; ytemp <- Array.zeroCreate n
        ak2 <- Array.zeroCreate n; ak3 <- Array.zeroCreate n; ak4 <- Array.zeroCreate n
        ak5 <- Array.zeroCreate n; ak6 <- Array.zeroCreate n
        nmax <- n
        1

    static member odesolve_close() : unit =
        oy <- null; yscal <- null; dydx <- null; yerr <- null; ytemp <- null
        ak2 <- null; ak3 <- null; ak4 <- null; ak5 <- null; ak6 <- null
        nmax <- 0

    static member odesolve_integrate(ystart: float[], n: int, x1: float, x2: float, eps: float, h1: float, derivs: OdeDerivs) : int =
        let MAXSTP = 10000
        let TINY = 1.0e-30
        let mutable hdid = 0.0
        let mutable hnext = 0.0
        let mutable x = x1
        let mutable h = h1
        if nmax < n then 1
        else
            for i in 0 .. n - 1 do oy.[i] <- ystart.[i]
            let mutable result = 3
            let mutable nstp = 1
            let mutable go = true
            while go && nstp <= MAXSTP do
                derivs.Invoke(x, oy, dydx)
                for i in 0 .. n - 1 do yscal.[i] <- Math.Abs(oy.[i]) + Math.Abs(dydx.[i] * h) + TINY
                if (x + h - x2) * (x + h - x1) > 0.0 then h <- x2 - x
                let errcode = O.rkqs(&x, n, h, eps, &hdid, &hnext, derivs)
                if errcode <> 0 then go <- false                       // break: result stays 3
                elif (x - x2) * (x2 - x1) >= 0.0 then
                    for i in 0 .. n - 1 do ystart.[i] <- oy.[i]
                    result <- 0
                    go <- false
                elif Math.Abs(hnext) <= 0.0 then
                    result <- 2
                    go <- false
                else
                    h <- hnext
                    nstp <- nstp + 1
            result

    static member private rkqs(x: byref<float>, n: int, htry: float, eps: float, hdid: byref<float>, hnext: byref<float>, derivs: OdeDerivs) : int =
        let SAFETY = 0.9
        let PGROW = -0.2
        let PSHRNK = -0.25
        let ERRCON = 1.89e-4
        let xold = x
        let mutable h = htry
        let mutable result = -1
        while result < 0 do
            O.rkck(xold, n, h, derivs)
            let mutable errmax = 0.0
            for i in 0 .. n - 1 do
                let err = Math.Abs(yerr.[i] / yscal.[i])
                if err > errmax then errmax <- err
            errmax <- errmax / eps
            if errmax > 1.0 then
                let htemp = SAFETY * h * Libm.pow(errmax, PSHRNK)
                if h >= 0.0 then
                    if htemp > 0.1 * h then h <- htemp
                    else h <- 0.1 * h
                else
                    if htemp < 0.1 * h then h <- htemp
                    else h <- 0.1 * h
                let xnew = xold + h
                if xnew = xold then result <- 2
            else
                if errmax > ERRCON then hnext <- SAFETY * h * Libm.pow(errmax, PGROW)
                else hnext <- 5.0 * h
                hdid <- h
                x <- x + hdid
                for i in 0 .. n - 1 do oy.[i] <- ytemp.[i]
                result <- 0
        result

    static member private rkck(x: float, n: int, h: float, derivs: OdeDerivs) : unit =
        let a2 = 0.2
        let a3 = 0.3
        let a4 = 0.6
        let a5 = 1.0
        let a6 = 0.875
        let b21 = 0.2
        let b31 = 3.0 / 40.0
        let b32 = 9.0 / 40.0
        let b41 = 0.3
        let b42 = -0.9
        let b43 = 1.2
        let b51 = -11.0 / 54.0
        let b52 = 2.5
        let b53 = -70.0 / 27.0
        let b54 = 35.0 / 27.0
        let b61 = 1631.0 / 55296.0
        let b62 = 175.0 / 512.0
        let b63 = 575.0 / 13824.0
        let b64 = 44275.0 / 110592.0
        let b65 = 253.0 / 4096.0
        let c1 = 37.0 / 378.0
        let c3 = 250.0 / 621.0
        let c4 = 125.0 / 594.0
        let c6 = 512.0 / 1771.0
        let dc5 = -277.0 / 14336.0
        let dc1 = c1 - 2825.0 / 27648.0
        let dc3 = c3 - 18575.0 / 48384.0
        let dc4 = c4 - 13525.0 / 55296.0
        let dc6 = c6 - 0.25
        for i in 0 .. n - 1 do ytemp.[i] <- oy.[i] + b21 * h * dydx.[i]
        derivs.Invoke(x + a2 * h, ytemp, ak2)
        for i in 0 .. n - 1 do ytemp.[i] <- oy.[i] + h * (b31 * dydx.[i] + b32 * ak2.[i])
        derivs.Invoke(x + a3 * h, ytemp, ak3)
        for i in 0 .. n - 1 do ytemp.[i] <- oy.[i] + h * (b41 * dydx.[i] + b42 * ak2.[i] + b43 * ak3.[i])
        derivs.Invoke(x + a4 * h, ytemp, ak4)
        for i in 0 .. n - 1 do ytemp.[i] <- oy.[i] + h * (b51 * dydx.[i] + b52 * ak2.[i] + b53 * ak3.[i] + b54 * ak4.[i])
        derivs.Invoke(x + a5 * h, ytemp, ak5)
        for i in 0 .. n - 1 do ytemp.[i] <- oy.[i] + h * (b61 * dydx.[i] + b62 * ak2.[i] + b63 * ak3.[i] + b64 * ak4.[i] + b65 * ak5.[i])
        derivs.Invoke(x + a6 * h, ytemp, ak6)
        for i in 0 .. n - 1 do ytemp.[i] <- oy.[i] + h * (c1 * dydx.[i] + c3 * ak3.[i] + c4 * ak4.[i] + c6 * ak6.[i])
        for i in 0 .. n - 1 do yerr.[i] <- h * (dc1 * dydx.[i] + dc3 * ak3.[i] + dc4 * ak4.[i] + dc5 * ak5.[i] + dc6 * ak6.[i])
