#!/bin/bash
# build.sh - build the F# port (bin/runswmm.dll) with the F# compiler that ships in the .NET 8 SDK (fsc.dll),
# against the shared framework's reference assemblies and FSharp.Core 8.0.102 from the SDK's offline package
# (FSharp/library-packs); no NuGet restore is needed. Written by Claude (Anthropic) for Robert Dickinson.
set -e
cd "$(dirname "$0")"
SDK=$(ls -d /usr/lib/dotnet/sdk/8.* | head -1)
FW=$(ls -d /usr/lib/dotnet/shared/Microsoft.NETCore.App/8.* | head -1)
mkdir -p bin obj
if [ ! -f obj/FSharp.Core.dll ]; then
  unzip -o -q -j "$SDK/FSharp/library-packs/FSharp.Core.8.0.102.nupkg" lib/netstandard2.1/FSharp.Core.dll -d obj
fi
REFS=$(for f in $FW/*.dll; do case $(basename $f) in Microsoft.VisualBasic*|mscorlib.dll|netstandard.dll|System.Private.*) ;; *) echo -n "-r:$f ";; esac; done)
dotnet "$SDK/FSharp/fsc.dll" --nologo --noframework --optimize+ --debug:portable --crossoptimize+ --deterministic+ \
  --pathmap:"$PWD"=/_/fsharp --target:exe --out:bin/runswmm.dll --nowarn:25,26,64,1182,3370,0020,40,3391 \
  $REFS -r:$FW/netstandard.dll -r:$FW/System.Private.CoreLib.dll -r:$FW/mscorlib.dll -r:obj/FSharp.Core.dll \
  runtime/Crt.fs runtime/Overrides.fs gen/Engine.fs runtime/Program.fs "$@"
cp obj/FSharp.Core.dll bin/
printf '{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"%s"},"configProperties":{"System.Globalization.Invariant":true,"System.Runtime.TieredPGO":true}}}' $(basename $FW) > bin/runswmm.runtimeconfig.json
