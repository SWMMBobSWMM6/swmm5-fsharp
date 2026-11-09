#!/bin/bash
# run_checks.sh [N] - the F# runtime's checks against the system glibc (see Checks.fs): the 1.39 million printf,
# strtod and sscanf cases of tools/crt/test/mkfmt.c, and the math functions on N random arguments per function
# (tools/libm/test/mkvectors.c, default 1,000,000), the edge cases of mkedge.c and the exact functions of
# mkexact.c. Vectors go to a temporary folder (TMPDIR) and are deleted.
# Written by Claude (Anthropic) for Robert Dickinson; see fsharp/README.md.
set -e
HERE=$(cd "$(dirname "$0")" && pwd); ROOT=$(cd "$HERE/../.." && pwd)
N=${1:-1000000}
T=$(mktemp -d "${TMPDIR:-/tmp}/fscheck.XXXXXX"); trap 'rm -rf "$T"' EXIT
SDK=$(ls -d /usr/lib/dotnet/sdk/8.* | head -1)
FW=$(ls -d /usr/lib/dotnet/shared/Microsoft.NETCore.App/8.* | head -1)
REFS=$(for f in $FW/*.dll; do case $(basename $f) in Microsoft.VisualBasic*|mscorlib.dll|netstandard.dll|System.Private.*) ;; *) echo -n "-r:$f ";; esac; done)
[ -f "$ROOT/fsharp/obj/FSharp.Core.dll" ] || unzip -o -q -j "$SDK/FSharp/library-packs/FSharp.Core.8.0.102.nupkg" lib/netstandard2.1/FSharp.Core.dll -d "$ROOT/fsharp/obj"
dotnet "$SDK/FSharp/fsc.dll" --nologo --noframework --optimize+ --target:exe --out:"$T/checks.dll" --nowarn:3370 \
  $REFS -r:$FW/netstandard.dll -r:$FW/System.Private.CoreLib.dll -r:$FW/mscorlib.dll -r:"$ROOT/fsharp/obj/FSharp.Core.dll" \
  "$ROOT/fsharp/runtime/Crt.fs" "$HERE/Checks.fs"
cp "$ROOT/fsharp/obj/FSharp.Core.dll" "$T/"
printf '{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"%s"}}}' $(basename $FW) > "$T/checks.runtimeconfig.json"
gcc -O2 -o "$T/mkfmt" "$ROOT/tools/crt/test/mkfmt.c" -lm
gcc -O2 -o "$T/mkvectors" "$ROOT/tools/libm/test/mkvectors.c" -lm
gcc -O2 -o "$T/mkedge" "$ROOT/tools/libm/test/mkedge.c" -lm
gcc -O2 -fno-builtin -o "$T/mkexact" "$HERE/mkexact.c" -lm
echo "== printf, strtod, sscanf (tools/crt/test/mkfmt.c)"
"$T/mkfmt" > "$T/fmt.txt"
dotnet "$T/checks.dll" crt "$T/fmt.txt"
rm -f "$T/fmt.txt"
echo "== math: random arguments over SWMM's ranges ($N per function)"
"$T/mkvectors" "$N" "$T/v.bin" 2>/dev/null
dotnet "$T/checks.dll" libm "$T/v.bin"
echo "== math: edge cases"
"$T/mkedge" 100000 "$T/e.bin" 2>/dev/null
dotnet "$T/checks.dll" libm "$T/e.bin"
echo "== sqrt, floor, ceil, fabs (mkexact.c)"
"$T/mkexact" 1000000 "$T/x.bin" 2>/dev/null
dotnet "$T/checks.dll" libm "$T/x.bin"
