// ilcmp.fsx A.dll B.dll - are two builds of the engine the same program? Compares the IL of every method body and
// the module version id (dotnet fsi fsharp/test/ilcmp.fsx A B). Written by Claude (Anthropic) for Robert Dickinson.
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
let bodies (p: string) =
    use fs = File.OpenRead p
    use pe = new PEReader(fs)
    let md = pe.GetMetadataReader()
    let mvid = md.GetGuid(md.GetModuleDefinition().Mvid)
    let il = [ for h in md.MethodDefinitions do
                 let m = md.GetMethodDefinition h
                 if m.RelativeVirtualAddress <> 0 then
                     let b = pe.GetMethodBody(m.RelativeVirtualAddress)
                     yield md.GetString(m.Name), b.GetILBytes() ]
    mvid, il
let m1, a = bodies fsi.CommandLineArgs.[1]
let m2, b = bodies fsi.CommandLineArgs.[2]
printfn "methods %d / %d; MVID %O / %O" a.Length b.Length m1 m2
printfn "IL bodies identical: %b" (List.length a = List.length b && List.forall2 (fun (n1, x: byte[]) (n2, y: byte[]) -> n1 = n2 && x = y) a b)
