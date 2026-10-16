// Focused Ghidra evidence for one Il2CppDumper-mapped ARM64 method.
// Run with analyzeHeadless -import <libil2cpp.so> -noanalysis -postScript InspectCalcCoefficient.java.
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.SourceType;
import ghidra.util.task.TaskMonitor;

public class InspectCalcCoefficient extends ghidra.app.script.GhidraScript {
    @Override
    public void run() throws Exception {
        long imageBase = currentProgram.getImageBase().getOffset();
        long methodRva = 0x1638520L;
        Address entry = toAddr(imageBase + methodRva);
        println("PROGRAM=" + currentProgram.getName());
        println("LANGUAGE=" + currentProgram.getLanguageID());
        println("IMAGE_BASE=" + currentProgram.getImageBase());
        println("ENTRY=" + entry);
        println("RVA=" + Long.toHexString(methodRva));
        println("GHIDRA_ADDRESS=" + entry);
        println("FILE_OFFSET_EXPECTED=" + Long.toHexString(methodRva));
        disassemble(entry);
        Function function = currentProgram.getFunctionManager().getFunctionAt(entry);
        if (function == null) {
            // RVA 0x1638520 ends with a tail branch at RVA 0x1638614 to Mathf.FloorToInt.
            // Restrict the body so Ghidra does not absorb that callee into this function.
            Address functionEnd = entry.add(0xf7);
            function = currentProgram.getFunctionManager().createFunction(
                "Colorful_DiscParameterUtil_CalcCoefficient", entry,
                new AddressSet(entry, functionEnd), SourceType.ANALYSIS);
        }
        if (function == null) throw new IllegalStateException("Could not create function at mapped entry.");
        println("FUNCTION=" + function.getName());
        println("FUNCTION_ENTRY=" + function.getEntryPoint());
        println("FUNCTION_BODY=" + function.getBody());
        InstructionIterator instructions = currentProgram.getListing().getInstructions(function.getBody(), true);
        while (instructions.hasNext()) {
            Instruction ins = instructions.next();
            println(String.format("INSN %s %s %s", ins.getAddress(), ins.getMnemonicString(), ins.getDefaultOperandRepresentation(0)));
        }
        Memory memory = currentProgram.getMemory();
        Address constantAddress = toAddr(imageBase + 0x3226e40L);
        byte[] raw = new byte[4];
        memory.getBytes(constantAddress, raw);
        float divisor = ByteBuffer.wrap(raw).order(ByteOrder.LITTLE_ENDIAN).getFloat();
        println(String.format("DIVISOR_ADDRESS=%s BYTES=%02x%02x%02x%02x FLOAT=%s", constantAddress,
            raw[0] & 255, raw[1] & 255, raw[2] & 255, raw[3] & 255, divisor));

        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        DecompileResults result = decompiler.decompileFunction(function, 60, TaskMonitor.DUMMY);
        println("DECOMPILE_COMPLETED=" + result.decompileCompleted());
        println("DECOMPILE_ERROR=" + result.getErrorMessage());
        if (result.getDecompiledFunction() != null) println("PSEUDOCODE_BEGIN\n" + result.getDecompiledFunction().getC() + "PSEUDOCODE_END");
        decompiler.dispose();
    }
}
