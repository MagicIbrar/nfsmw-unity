// Decompile functions at given addresses, creating them first when Ghidra's auto-analysis missed them.
// args: <outdir> <addrfile> [calleeDepth]   (addrfile: one hex address per line, optional "name" after it)
// @category NFSMW
import java.io.*;
import java.nio.file.*;
import java.util.*;

import ghidra.app.cmd.function.CreateFunctionCmd;
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;

public class DecompileAt extends GhidraScript {
    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        File outDir = new File(args[0]);
        outDir.mkdirs();
        List<String> lines = new ArrayList<>(Files.readAllLines(Paths.get(args[1])));
        int depth = args.length > 2 ? Integer.parseInt(args[2]) : 0;
        Map<String, Integer> level = new HashMap<>();
        Set<Long> done = new HashSet<>();

        DecompInterface ifc = new DecompInterface();
        ifc.openProgram(currentProgram);

        for (int li = 0; li < lines.size(); li++) {
            String line = lines.get(li).trim();
            if (line.isEmpty() || line.startsWith("#")) continue;
            String[] parts = line.split("\\s+");
            Address addr = toAddr(Long.parseLong(parts[0].replace("0x", ""), 16));
            if (!done.add(addr.getOffset())) continue;
            int lv = level.getOrDefault(parts[0], 0);
            Function f = getFunctionAt(addr);
            if (f == null) f = getFunctionContaining(addr);
            if (f == null) {
                disassemble(addr);
                new CreateFunctionCmd(addr).applyTo(currentProgram, monitor);
                f = getFunctionAt(addr);
            }
            if (f == null) {
                println("FAILED " + addr);
                continue;
            }
            if (parts.length > 1 && f.getName().startsWith("FUN_")) {
                f.setName(parts[1], SourceType.USER_DEFINED);
            }
            DecompileResults res = ifc.decompileFunction(f, 120, monitor);
            String c = res.decompileCompleted() ? res.getDecompiledFunction().getC() : "// decompile failed: " + res.getErrorMessage();
            String file = String.format("%08x_%s.c", addr.getOffset(), f.getName());
            try (Writer w = new FileWriter(new File(outDir, file))) {
                w.write(c);
            }
            println("OK " + file);
            if (lv < depth) {
                for (Function callee : f.getCalledFunctions(monitor)) {
                    if (callee.isExternal() || callee.isThunk()) continue;
                    String key = "0x" + Long.toHexString(callee.getEntryPoint().getOffset());
                    if (!level.containsKey(key)) {
                        level.put(key, lv + 1);
                        lines.add(key);
                    }
                }
            }
        }
    }
}
