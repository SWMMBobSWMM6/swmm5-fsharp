#!/bin/bash
# check_binding.sh MODEL.inp - which libm symbol versions the F# engine's P/Invoke calls bind to (lesson 19:
# "links against libm" is not "calls libm"). Runs one model with LD_DEBUG=bindings, lists the libm symbols the
# process looked up by name (dlsym, which takes the default version, marked by objdump without parentheses), and
# prints EPA's imports for comparison. Written by Claude (Anthropic) for Robert Dickinson.
H=$(cd "$(dirname "$0")/.." && pwd)
T=$(mktemp -d "${TMPDIR:-/tmp}/fsbind.XXXXXX"); trap 'rm -rf "$T"' EXIT
cp "$1" "$T/m.inp"
(cd "$T" && LD_DEBUG=bindings LD_DEBUG_OUTPUT="$T/ld" dotnet "$H/bin/runswmm.dll" m.inp m.rpt m.out > /dev/null 2>&1)
echo "== libm symbols bound by the F# engine (lookups from libm.so.6's own handle are the P/Invoke dlsym calls)"
cat "$T"/ld.* | grep -E "symbol \`(exp|log|log10|pow|sin|cos|tan|asin|acos|atan|floor|ceil)'" | grep "binding file /lib/x86_64-linux-gnu/libm.so.6" | sed 's/^ *[0-9]*: *//' | sort -u
echo "== default (unparenthesised) versions of those symbols in libm.so.6"
objdump -T /lib/x86_64-linux-gnu/libm.so.6 | grep -E "\s(exp|log|log10|pow|sin|cos|tan|asin|acos|atan|floor|ceil)$" | grep -v "(GLIBC"
echo "== EPA 5.2.4 (ref/runswmm) imports"
nm -D "$H/../ref/runswmm" | grep -E " U (exp|log|log10|pow|sin|cos|tan|asin|acos|atan|floor|ceil)@"
