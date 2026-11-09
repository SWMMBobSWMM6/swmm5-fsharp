// Program.fs - runswmm for the F# port: the same command line and console messages as EPA's run/main.c.
// Written by Claude (Anthropic) for Robert Dickinson; see fsharp/README.md.
module Swmm5.Program

open System
open Swmm5.Engine

let private p (s: string) =
    let b = Text.Encoding.ASCII.GetBytes(s)
    R.stdout_.S.Write(b, 0, b.Length)
    R.stdout_.S.Flush()

[<EntryPoint>]
let main argv =
    let version = S.swmm_getVersion()
    let vMajor = version / 10000
    let vMinor = (version - 10000 * vMajor) / 1000
    let vRelease = version - 10000 * vMajor - 1000 * vMinor
    let start = DateTime.UtcNow
    if argv.Length = 0 then p "\nNot Enough Arguments (See Help --help)\n\n"
    elif argv.Length = 1 then
        let a = argv.[0]
        if a = "--help" || a = "-h" then
            p "\n\nSTORMWATER MANAGEMENT MODEL (SWMM) HELP\n\nCOMMANDS:\n\t--help (-h)       SWMM Help\n\t--version (-v)    Build Version\n\nRUNNING A SIMULATION:\n\t runswmm <input file> <report file> <optional output file>\n\n"
        elif a = "--version" || a = "-v" then p (sprintf "\n%d.%d.%d\n\n" vMajor vMinor vRelease)
        else p "\nUnknown Argument (See Help --help)\n\n"
    else
        let inp = R.Lit(argv.[0])
        let rpt = R.Lit(argv.[1])
        let outf = R.Lit(if argv.Length > 2 then argv.[2] else "")
        p (sprintf "\n... EPA SWMM %d.%d (Build %d.%d.%d) [F# port]\n" vMajor vMinor vMajor vMinor vRelease)
        S.swmm_run(inp, rpt, outf) |> ignore
        let runTime = (DateTime.UtcNow - start).TotalSeconds
        p ("\n\n... EPA SWMM completed in " + runTime.ToString("0.00", Globalization.CultureInfo.InvariantCulture) + " seconds.")
        let err = R.Buf(128)
        if S.swmm_getError(err, 127) > 0 then p " There are errors.\n"
        elif S.swmm_getWarnings() > 0 then p " There are warnings.\n"
        else p "\n"
    R.stdout_.S.Flush()
    0
