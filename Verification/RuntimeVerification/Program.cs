using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using SharedData = ItemDrop.ItemData.SharedData;

internal static class Program
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static string PluginPath;
    private static string[] SearchDirectories = Array.Empty<string>();

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
            {
                PrintUsage();
                return 0;
            }

            ConfigurePaths(args);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            return Verify();
        }
        catch (Exception exception)
        {
            System.Console.Error.WriteLine("[FAIL] " + exception);
            return 1;
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly;
        }
    }

    private static void PrintUsage()
    {
        System.Console.WriteLine("Usage: YNW.RuntimeVerification --repo <checkout> --game <Valheim directory> [--configuration Debug|Release] [--plugin <DLL>]");
        System.Console.WriteLine("Build this verifier against the same game directory. This is managed verification, not a Valheim play test.");
    }

    private static void ConfigurePaths(string[] args)
    {
        Dictionary<string, string> options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index += 2)
        {
            string option = args[index];
            if ((option != "--repo" && option != "--game" && option != "--configuration" && option != "--plugin")
                || index + 1 >= args.Length || options.ContainsKey(option))
            {
                throw new ArgumentException("Unknown, duplicate, or incomplete option: " + option + ". Use --help for usage.");
            }

            options.Add(option, args[index + 1]);
        }

        if (!options.TryGetValue("--repo", out string repoArgument)
            || !options.TryGetValue("--game", out string gameArgument))
        {
            throw new ArgumentException("Both --repo and --game are required. Use --help for usage.");
        }

        string repo = Path.GetFullPath(repoArgument);
        string game = Path.GetFullPath(gameArgument);
        string configuration = options.TryGetValue("--configuration", out string configured) ? configured : "Debug";
        if (configuration != "Debug" && configuration != "Release")
        {
            throw new ArgumentException("--configuration must be Debug or Release.");
        }

        PluginPath = Path.GetFullPath(options.TryGetValue("--plugin", out string plugin)
            ? plugin
            : Path.Combine(repo, "bin", configuration, "YouAreNotWorthy.dll"));
        string managed = Path.Combine(game, "valheim_Data", "Managed");
        if (!Directory.Exists(repo) || !File.Exists(Path.Combine(managed, "assembly_valheim.dll")))
        {
            throw new DirectoryNotFoundException("--repo must exist and --game must contain valheim_Data/Managed/assembly_valheim.dll.");
        }

        if (!File.Exists(PluginPath))
        {
            throw new FileNotFoundException("Build the plugin first or specify --plugin.", PluginPath);
        }

        SearchDirectories = new[]
        {
            Path.Combine(managed, "publicized_assemblies"),
            managed,
            Path.Combine(game, "BepInEx", "core"),
            Path.Combine(repo, "Libs"),
            Path.GetDirectoryName(PluginPath)
        };
        System.Console.WriteLine("[INFO] plugin: " + PluginPath);
        System.Console.WriteLine("[INFO] game dependencies: " + game);
    }

    private static int Verify()
    {
        Assembly plugin = Assembly.LoadFrom(PluginPath);

        MethodInfo spawnUpdate = ExactMethod(typeof(SpawnSystem), "UpdateSpawnList",
            typeof(List<SpawnSystem.SpawnData>), typeof(DateTime), typeof(bool));
        MethodInfo findBase = ExactMethod(typeof(SpawnSystem), "FindBaseSpawnPoint",
            typeof(SpawnSystem.SpawnData), typeof(List<Player>), typeof(Vector3).MakeByRefType(), typeof(Player).MakeByRefType());
        MethodInfo getGlobal = ExactMethod(typeof(ZoneSystem), "GetGlobalKey", typeof(string));
        MethodInfo creatureUpdate = ExactMethod(typeof(CreatureSpawner), "UpdateSpawner");
        MethodInfo creatureCheck = ExactMethod(typeof(CreatureSpawner), "CheckGlobalKeys");
        MethodInfo creatureSpawn = ExactMethod(typeof(CreatureSpawner), "Spawn");
        MethodInfo death = ExactMethod(typeof(Character), "OnDeath");
        MethodInfo range2 = ExactMethod(typeof(Player), "IsPlayerInRange", typeof(Vector3), typeof(float));
        MethodInfo range3 = ExactMethod(typeof(Player), "IsPlayerInRange", typeof(Vector3), typeof(float), typeof(float));

        Type spawnPatch = plugin.GetType("YouAreNotWorthy.SpawnSystem_UpdateSpawnList_Patch", true);
        Type creaturePatch = plugin.GetType("YouAreNotWorthy.CreatureSpawner_UpdateSpawner_Patch", true);
        Type deathPatch = plugin.GetType("YouAreNotWorthy.Character_OnDeath_DefeatKey_Patch", true);
        Type spawnHelper = plugin.GetType("YouAreNotWorthy.SpawnPersonalization", true);
        Type creatureHelper = plugin.GetType("YouAreNotWorthy.CreatureSpawnPersonalization", true);

        MethodInfo spawnTranspiler = ExactMethod(spawnPatch, "Transpiler", typeof(IEnumerable<CodeInstruction>));
        MethodInfo creatureTranspiler = ExactMethod(creaturePatch, "Transpiler", typeof(IEnumerable<CodeInstruction>));
        MethodInfo deathTranspiler = ExactMethod(deathPatch, "Transpiler", typeof(IEnumerable<CodeInstruction>));
        MethodInfo findEligible = ExactMethod(spawnHelper, "FindEligibleBaseSpawnPoint",
            typeof(SpawnSystem), typeof(SpawnSystem.SpawnData), typeof(List<Player>),
            typeof(Vector3).MakeByRefType(), typeof(Player).MakeByRefType());
        MethodInfo hasRequired = ExactMethod(spawnHelper, "HasRequiredSpawnKey", typeof(ZoneSystem), typeof(string), typeof(Player));
        MethodInfo personalRange2 = ExactMethod(creatureHelper, "IsPlayerInRange", typeof(Vector3), typeof(float), typeof(CreatureSpawner));
        MethodInfo personalRange3 = ExactMethod(creatureHelper, "IsPlayerInRange", typeof(Vector3), typeof(float), typeof(float), typeof(CreatureSpawner));
        MethodInfo maybeQueue = ExactMethod(deathPatch, "MaybeQueueVanillaDefeatKey", typeof(List<string>), typeof(string));

        Check("target: SpawnSystem.UpdateSpawnList exact overload", spawnUpdate != null);
        Check("target: SpawnSystem.FindBaseSpawnPoint exact overload", findBase != null);
        Check("target: ZoneSystem.GetGlobalKey(string)", getGlobal != null);
        Check("target: CreatureSpawner.UpdateSpawner", creatureUpdate != null);
        Check("target: CreatureSpawner.CheckGlobalKeys", creatureCheck != null);
        Check("target: CreatureSpawner.Spawn", creatureSpawn != null);
        Check("target: Character.OnDeath", death != null);

        Result spawn = ApplyTranspiler(spawnUpdate, spawnTranspiler);
        Result creature = ApplyTranspiler(creatureUpdate, creatureTranspiler);
        Result character = ApplyTranspiler(death, deathTranspiler);

        Check("pattern Spawn before: FindBaseSpawnPoint = 1", CountCall(spawn.Before, findBase) == 1);
        Check("pattern Spawn before: GetGlobalKey(string) = 1", CountCall(spawn.Before, getGlobal) == 1);
        Check("result Spawn: FindEligibleBaseSpawnPoint = 1", CountCall(spawn.After, findEligible) == 1);
        Check("result Spawn: HasRequiredSpawnKey = 1", CountCall(spawn.After, hasRequired) == 1);
        Check("result Spawn: original FindBaseSpawnPoint = 0", CountCall(spawn.After, findBase) == 0);
        Check("result Spawn: original GetGlobalKey(string) = 0", CountCall(spawn.After, getGlobal) == 0);

        Check("pattern Creature before: range check = 1", CountCall(creature.Before, range2) == 1);
        Check("pattern Creature before: noise check = 1", CountCall(creature.Before, range3) == 1);
        Check("result Creature: personal range check = 1", CountCall(creature.After, personalRange2) == 1);
        Check("result Creature: personal noise check = 1", CountCall(creature.After, personalRange3) == 1);
        Check("result Creature: original range check = 0", CountCall(creature.After, range2) == 0);
        Check("result Creature: original noise check = 0", CountCall(creature.After, range3) == 0);

        FieldInfo queue = typeof(Player).GetField("m_addUniqueKeyQueue", All);
        FieldInfo defeatKey = typeof(Character).GetField("m_defeatSetGlobalKey", All);
        MethodInfo listAdd = typeof(List<string>).GetMethod("Add", new[] { typeof(string) });
        int deathPatternCount = CountDeathQueuePatterns(character.Before, queue, defeatKey, listAdd);
        Check("pattern Character.OnDeath before: defeat queue write = 1", deathPatternCount == 1);
        Check("result Character.OnDeath: personal queue helper = 1", CountCall(character.After, maybeQueue) == 1);
        Check("result Character.OnDeath: matched List<string>.Add = 0", CountDeathQueuePatterns(character.After, queue, defeatKey, listAdd) == 0);

        VerifyTierResolution(plugin);
        ConsoleCommandVerification.Run(plugin, Check);
        System.Console.WriteLine("[PASS] all managed tier, reflection, pattern-count, transpiler-output, and dynamic IL/JIT checks passed");
        return 0;
    }

    private static void VerifyTierResolution(Assembly plugin)
    {
        Type evaluator = plugin.GetType("YouAreNotWorthy.RestrictionEvaluator", true);
        Type stateType = evaluator.GetNestedType("ResolverState", All)
            ?? throw new MissingMemberException(evaluator.FullName, "ResolverState");
        Type tierType = plugin.GetType("YouAreNotWorthy.CompiledItemTier", true);
        MethodInfo effective = ExactMethod(evaluator, "ResolveEffectiveTier", typeof(ItemDrop.ItemData), stateType, tierType);

        // Invoke the real plugin method without reflection argument-array allocations in the measured loop.
        DynamicMethod bridge = new DynamicMethod("YNW_VerifyEffectiveTier", typeof(object),
            new[] { typeof(ItemDrop.ItemData), typeof(object), typeof(object) }, typeof(Program).Module, true);
        ILGenerator il = bridge.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, stateType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, tierType);
        il.Emit(OpCodes.Call, effective);
        il.Emit(OpCodes.Ret);
        var resolve = (Func<ItemDrop.ItemData, object, object, object>)bridge.CreateDelegate(
            typeof(Func<ItemDrop.ItemData, object, object, object>));

        object Tier(int rank) => Activator.CreateInstance(tierType, All, null, new object[] { rank, "test_tier_" + rank }, null);
        object State() => Activator.CreateInstance(stateType, nonPublic: true);
        IDictionary Map(object state, string property) => (IDictionary)stateType.GetProperty(property, All).GetValue(state);
        SharedData Node() => (SharedData)RuntimeHelpers.GetUninitializedObject(typeof(SharedData));
        ItemDrop.ItemData Item(SharedData shared)
        {
            // Only reference identity is used. Avoid SharedData/ItemData constructors and Unity native objects.
            var item = (ItemDrop.ItemData)RuntimeHelpers.GetUninitializedObject(typeof(ItemDrop.ItemData));
            item.m_shared = shared;
            return item;
        }

        void Expect(string label, object state, SharedData node, object direct, object expected)
        {
            ItemDrop.ItemData item = Item(node);
            Check("tier: " + label, ReferenceEquals(resolve(item, state, direct), expected));
            Check("tier repeat: " + label, ReferenceEquals(resolve(item, state, direct), expected));
        }

        object low = Tier(1);
        object middle = Tier(2);
        object high = Tier(3);
        SharedData lowInput = Node();
        SharedData highInput = Node();
        SharedData freeInput = Node();
        SharedData output = Node();
        object ProductionState()
        {
            object state = State();
            Map(state, "DirectTiers")[lowInput] = low;
            Map(state, "DirectTiers")[highInput] = high;
            return state;
        }

        Expect("no runtime keeps direct tier", null, output, low, low);
        Expect("no runtime without direct tier", null, output, null, null);

        object cached = State();
        Map(cached, "ResolvedTiers")[output] = middle;
        Expect("cached tier is stronger than direct", cached, output, low, middle);
        Expect("direct tier is stronger than cached", cached, output, high, high);
        object equalRankDirect = Tier(2);
        Expect("equal ranks keep the direct tier instance", cached, output, equalRankDirect, equalRankDirect);

        object cachedNull = ProductionState();
        Map(cachedNull, "ResolvedTiers")[output] = null;
        Map(cachedNull, "InputPaths")[output] = new List<SharedData[]> { new[] { highInput } };
        Expect("cached null is a resolved result", cachedNull, output, null, null);
        Expect("cached null preserves direct tier", cachedNull, output, low, low);

        object combined = ProductionState();
        Map(combined, "InputPaths")[output] = new List<SharedData[]> { new[] { lowInput, highInput } };
        Expect("one production path requires its strongest ingredient", combined, output, null, high);

        object alternatives = ProductionState();
        Map(alternatives, "InputPaths")[output] = new List<SharedData[]> { new[] { highInput }, new[] { lowInput } };
        Expect("alternative production paths choose the easiest tier", alternatives, output, null, low);
        Expect("direct tier remains a floor over an easier path", alternatives, output, high, high);

        foreach (bool freeFirst in new[] { false, true })
        {
            object freeAlternative = ProductionState();
            Map(freeAlternative, "InputPaths")[output] = freeFirst
                ? new List<SharedData[]> { new[] { freeInput }, new[] { highInput } }
                : new List<SharedData[]> { new[] { highInput }, new[] { freeInput } };
            Expect("unclassified alternative removes inherited tier; freeFirst=" + freeFirst, freeAlternative, output, null, null);
            Expect("unclassified alternative keeps direct floor; freeFirst=" + freeFirst, freeAlternative, output, middle, middle);
        }

        object chained = ProductionState();
        SharedData intermediate = Node();
        Map(chained, "InputPaths")[intermediate] = new List<SharedData[]> { new[] { highInput } };
        Map(chained, "InputPaths")[output] = new List<SharedData[]> { new[] { intermediate } };
        Expect("multiple production steps inherit the requirement", chained, output, null, high);
        Expect("unclassified miss and repeated lookup stay keyless", State(), freeInput, null, null);

        object cycle = State();
        Map(cycle, "InputPaths")[output] = new List<SharedData[]> { new[] { output } };
        Expect("self cycle without a direct requirement terminates", cycle, output, null, null);
        object directCycle = State();
        Map(directCycle, "DirectTiers")[output] = middle;
        Map(directCycle, "InputPaths")[output] = new List<SharedData[]> { new[] { output } };
        Expect("self cycle preserves its direct requirement", directCycle, output, null, middle);

        void ReportCachedAllocations(string label, object state)
        {
            ItemDrop.ItemData item = Item(output);
            for (int index = 0; index < 100; index++) resolve(item, state, null);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 1000; index++) resolve(item, state, null);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            System.Console.WriteLine("[INFO] " + label + ": " + allocated + " managed bytes / 1000 warmed calls; host .NET runtime, not Unity profiling");
        }

        ReportCachedAllocations("cached tier", cached);
        ReportCachedAllocations("cached null tier", cachedNull);
    }

    private static Result ApplyTranspiler(MethodInfo target, MethodInfo transpiler)
    {
        DecodedMethod decoded = IlDecoder.Decode(target);
        List<CodeInstruction> before = decoded.Instructions;
        object output = transpiler.Invoke(null, new object[] { before });
        List<CodeInstruction> after = ((IEnumerable<CodeInstruction>)output).ToList();
        EmitAndJit(decoded, after);
        Check("transformed IL re-emitted and JIT-compiled: " + target.DeclaringType.FullName + "." + target.Name, true);
        return new Result(before, after);
    }

    private static void EmitAndJit(DecodedMethod decoded, IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> verificationInstructions = instructions
            .Select(instruction => new CodeInstruction(instruction))
            .ToList();
        Dictionary<MethodBase, DynamicMethod> ecallStubs = new Dictionary<MethodBase, DynamicMethod>();
        foreach (CodeInstruction instruction in verificationInstructions)
        {
            if ((instruction.opcode == OpCodes.Call
                 || instruction.opcode == OpCodes.Callvirt
                 || instruction.opcode == OpCodes.Newobj)
                && instruction.operand is MethodBase called
                && (called.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0)
            {
                if (!ecallStubs.TryGetValue(called, out DynamicMethod stub))
                {
                    stub = CreateECallStub(called);
                    ecallStubs.Add(called, stub);
                }

                instruction.opcode = OpCodes.Call;
                instruction.operand = stub;
            }
        }

        foreach (CodeInstruction instruction in verificationInstructions)
        {
            foreach (ExceptionBlock block in instruction.blocks.Where(block => block.blockType == ExceptionBlockType.EndExceptionBlock))
            {
                decoded.Generator.EndExceptionBlock();
            }

            foreach (ExceptionBlock block in instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock))
            {
                switch (block.blockType)
                {
                    case ExceptionBlockType.BeginExceptionBlock:
                        decoded.Generator.BeginExceptionBlock();
                        break;
                    case ExceptionBlockType.BeginCatchBlock:
                        decoded.Generator.BeginCatchBlock(block.catchType);
                        break;
                    case ExceptionBlockType.BeginExceptFilterBlock:
                        decoded.Generator.BeginExceptFilterBlock();
                        break;
                    case ExceptionBlockType.BeginFaultBlock:
                        decoded.Generator.BeginFaultBlock();
                        break;
                    case ExceptionBlockType.BeginFinallyBlock:
                        decoded.Generator.BeginFinallyBlock();
                        break;
                    default:
                        throw new NotSupportedException("Unsupported exception block " + block.blockType);
                }
            }

            foreach (Label label in instruction.labels)
            {
                decoded.Generator.MarkLabel(label);
            }

            Emit(decoded.Generator, instruction.opcode, instruction.operand);
        }

        Type[] delegateSignature = decoded.Parameters.Concat(new[] { decoded.Target.ReturnType }).ToArray();
        Delegate compiled = decoded.Method.CreateDelegate(Expression.GetDelegateType(delegateSignature));
        RuntimeHelpers.PrepareDelegate(compiled);
    }

    private static DynamicMethod CreateECallStub(MethodBase called)
    {
        Type returnType;
        Type[] parameters;
        if (called is MethodInfo method)
        {
            returnType = method.ReturnType;
            parameters = (method.IsStatic ? Array.Empty<Type>() : new[] { method.DeclaringType })
                .Concat(method.GetParameters().Select(parameter => parameter.ParameterType))
                .ToArray();
        }
        else if (called is ConstructorInfo constructor)
        {
            returnType = constructor.DeclaringType;
            parameters = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        }
        else
        {
            throw new NotSupportedException("Unsupported ECall member " + called);
        }

        DynamicMethod stub = new DynamicMethod(
            "YNW_ECallStub_" + called.DeclaringType.Name + "_" + called.Name,
            returnType,
            parameters,
            typeof(Program).Module,
            true);
        ILGenerator il = stub.GetILGenerator();
        if (returnType != typeof(void))
        {
            if (returnType.IsValueType)
            {
                LocalBuilder value = il.DeclareLocal(returnType);
                il.Emit(OpCodes.Ldloca, value);
                il.Emit(OpCodes.Initobj, returnType);
                il.Emit(OpCodes.Ldloc, value);
            }
            else
            {
                il.Emit(OpCodes.Ldnull);
            }
        }

        il.Emit(OpCodes.Ret);
        return stub;
    }

    private static void Emit(ILGenerator generator, OpCode opcode, object operand)
    {
        if (operand == null) { generator.Emit(opcode); return; }
        if (operand is Label label) { generator.Emit(opcode, label); return; }
        if (operand is Label[] labels) { generator.Emit(opcode, labels); return; }
        if (operand is LocalBuilder local) { generator.Emit(opcode, local); return; }
        if (operand is string text) { generator.Emit(opcode, text); return; }
        if (operand is sbyte signedByte) { generator.Emit(opcode, signedByte); return; }
        if (operand is byte unsignedByte) { generator.Emit(opcode, unsignedByte); return; }
        if (operand is short shortValue) { generator.Emit(opcode, shortValue); return; }
        if (operand is int intValue) { generator.Emit(opcode, intValue); return; }
        if (operand is long longValue) { generator.Emit(opcode, longValue); return; }
        if (operand is float floatValue) { generator.Emit(opcode, floatValue); return; }
        if (operand is double doubleValue) { generator.Emit(opcode, doubleValue); return; }
        if (operand is FieldInfo field) { generator.Emit(opcode, field); return; }
        if (operand is MethodInfo method) { generator.Emit(opcode, method); return; }
        if (operand is ConstructorInfo constructor) { generator.Emit(opcode, constructor); return; }
        if (operand is Type type) { generator.Emit(opcode, type); return; }
        throw new NotSupportedException("Unsupported operand " + operand.GetType().FullName + " for " + opcode);
    }

    private static int CountCall(IEnumerable<CodeInstruction> instructions, MethodBase method)
    {
        return instructions.Count(instruction => instruction.operand is MethodBase candidate && candidate == method);
    }

    private static int CountDeathQueuePatterns(
        IReadOnlyList<CodeInstruction> codes,
        FieldInfo queue,
        FieldInfo defeatKey,
        MethodInfo listAdd)
    {
        int count = 0;
        for (int i = 0; i <= codes.Count - 4; i++)
        {
            if (codes[i].opcode == OpCodes.Ldsfld
                && Equals(codes[i].operand, queue)
                && codes[i + 1].opcode == OpCodes.Ldarg_0
                && codes[i + 2].opcode == OpCodes.Ldfld
                && Equals(codes[i + 2].operand, defeatKey)
                && codes[i + 3].Calls(listAdd))
            {
                count++;
            }
        }

        return count;
    }

    private static MethodInfo ExactMethod(Type type, string name, params Type[] parameters)
    {
        MethodInfo method = type.GetMethod(name, All, null, parameters, null);
        if (method == null)
        {
            throw new MissingMethodException(type.FullName, name + "(" + string.Join(",", parameters.Select(item => item.FullName)) + ")");
        }

        return method;
    }

    private static void Check(string label, bool passed)
    {
        System.Console.WriteLine((passed ? "[PASS] " : "[FAIL] ") + label);
        if (!passed)
        {
            throw new InvalidOperationException(label);
        }
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        string simpleName = new AssemblyName(args.Name).Name;
        if (simpleName == "0Harmony")
        {
            return typeof(Harmony).Assembly;
        }

        string fileName = simpleName + ".dll";
        foreach (string directory in SearchDirectories)
        {
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return Assembly.LoadFrom(candidate);
            }
        }

        return null;
    }

    private sealed class DecodedMethod
    {
        internal DecodedMethod(
            MethodInfo target,
            DynamicMethod method,
            ILGenerator generator,
            Type[] parameters,
            List<CodeInstruction> instructions)
        {
            Target = target;
            Method = method;
            Generator = generator;
            Parameters = parameters;
            Instructions = instructions;
        }

        internal MethodInfo Target { get; }
        internal DynamicMethod Method { get; }
        internal ILGenerator Generator { get; }
        internal Type[] Parameters { get; }
        internal List<CodeInstruction> Instructions { get; }
    }

    private static class IlDecoder
    {
        private static readonly Dictionary<ushort, OpCode> Opcodes = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null))
            .ToDictionary(opcode => unchecked((ushort)opcode.Value));

        internal static DecodedMethod Decode(MethodInfo target)
        {
            MethodBody body = target.GetMethodBody() ?? throw new InvalidOperationException("No method body for " + target);
            byte[] il = body.GetILAsByteArray();
            Type[] parameters = (target.IsStatic ? Array.Empty<Type>() : new[] { target.DeclaringType })
                .Concat(target.GetParameters().Select(parameter => parameter.ParameterType))
                .ToArray();
            DynamicMethod dynamicMethod = new DynamicMethod(
                "YNW_Verify_" + target.DeclaringType.Name + "_" + target.Name,
                target.ReturnType,
                parameters,
                target.Module,
                true)
            {
                InitLocals = body.InitLocals
            };
            ILGenerator generator = dynamicMethod.GetILGenerator();
            LocalBuilder[] locals = body.LocalVariables
                .Select(local => generator.DeclareLocal(local.LocalType, local.IsPinned))
                .ToArray();

            List<RawInstruction> raw = ReadRaw(target, il, locals);
            HashSet<int> targetOffsets = new HashSet<int>();
            foreach (RawInstruction instruction in raw)
            {
                if (instruction.Operand is BranchTarget branch)
                {
                    targetOffsets.Add(branch.Offset);
                }
                else if (instruction.Operand is SwitchTargets branches)
                {
                    foreach (int offset in branches.Offsets) targetOffsets.Add(offset);
                }
            }

            foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
            {
                targetOffsets.Add(clause.TryOffset);
                targetOffsets.Add(clause.HandlerOffset);
                targetOffsets.Add(clause.HandlerOffset + clause.HandlerLength);
                if (clause.Flags == ExceptionHandlingClauseOptions.Filter)
                {
                    targetOffsets.Add(clause.FilterOffset);
                }
            }

            if ((targetOffsets.Contains(il.Length) || body.ExceptionHandlingClauses.Cast<ExceptionHandlingClause>()
                    .Any(clause => clause.HandlerOffset + clause.HandlerLength == il.Length))
                && raw.All(instruction => instruction.Offset != il.Length))
            {
                raw.Add(new RawInstruction(il.Length, OpCodes.Nop, null));
            }

            Dictionary<int, Label> labels = targetOffsets.ToDictionary(offset => offset, _ => generator.DefineLabel());
            List<CodeInstruction> codes = raw.Select(instruction =>
            {
                object operand = instruction.Operand;
                if (operand is BranchTarget branch) operand = labels[branch.Offset];
                else if (operand is SwitchTargets branches) operand = branches.Offsets.Select(offset => labels[offset]).ToArray();
                CodeInstruction code = new CodeInstruction(instruction.Opcode, operand);
                if (labels.TryGetValue(instruction.Offset, out Label label)) code.labels.Add(label);
                return code;
            }).ToList();
            Dictionary<int, CodeInstruction> byOffset = raw
                .Select((instruction, index) => new { instruction.Offset, Code = codes[index] })
                .ToDictionary(entry => entry.Offset, entry => entry.Code);

            foreach (IGrouping<(int TryOffset, int TryLength), ExceptionHandlingClause> group in
                     body.ExceptionHandlingClauses.Cast<ExceptionHandlingClause>()
                         .GroupBy(clause => (clause.TryOffset, clause.TryLength)))
            {
                byOffset[group.Key.TryOffset].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
                foreach (ExceptionHandlingClause clause in group.OrderBy(clause => clause.HandlerOffset))
                {
                    switch (clause.Flags)
                    {
                        case ExceptionHandlingClauseOptions.Clause:
                            byOffset[clause.HandlerOffset].blocks.Add(
                                new ExceptionBlock(ExceptionBlockType.BeginCatchBlock, clause.CatchType));
                            break;
                        case ExceptionHandlingClauseOptions.Finally:
                            byOffset[clause.HandlerOffset].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginFinallyBlock));
                            break;
                        case ExceptionHandlingClauseOptions.Fault:
                            byOffset[clause.HandlerOffset].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginFaultBlock));
                            break;
                        case ExceptionHandlingClauseOptions.Filter:
                            byOffset[clause.FilterOffset].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptFilterBlock));
                            byOffset[clause.HandlerOffset].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginCatchBlock));
                            break;
                        default:
                            throw new NotSupportedException("Unsupported exception clause " + clause.Flags);
                    }
                }

                int end = group.Max(clause => clause.HandlerOffset + clause.HandlerLength);
                byOffset[end].blocks.Add(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
            }

            return new DecodedMethod(target, dynamicMethod, generator, parameters, codes);
        }

        private static List<RawInstruction> ReadRaw(MethodInfo method, byte[] bytes, LocalBuilder[] locals)
        {
            List<RawInstruction> result = new List<RawInstruction>();
            Module module = method.Module;
            Type[] typeArguments = method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
            Type[] methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            int position = 0;
            while (position < bytes.Length)
            {
                int offset = position;
                byte first = bytes[position++];
                ushort value = first == 0xfe ? (ushort)(0xfe00 | bytes[position++]) : first;
                if (!Opcodes.TryGetValue(value, out OpCode opcode))
                {
                    throw new InvalidOperationException("Unknown opcode 0x" + value.ToString("X4") + " at " + offset);
                }

                object operand;
                switch (opcode.OperandType)
                {
                    case OperandType.InlineNone:
                        operand = null;
                        break;
                    case OperandType.ShortInlineI:
                        operand = opcode == OpCodes.Unaligned
                            ? (object)bytes[position++]
                            : unchecked((sbyte)bytes[position++]);
                        break;
                    case OperandType.InlineI:
                        operand = ReadInt32(bytes, ref position);
                        break;
                    case OperandType.InlineI8:
                        operand = ReadInt64(bytes, ref position);
                        break;
                    case OperandType.ShortInlineR:
                        operand = ReadSingle(bytes, ref position);
                        break;
                    case OperandType.InlineR:
                        operand = ReadDouble(bytes, ref position);
                        break;
                    case OperandType.ShortInlineBrTarget:
                    {
                        int delta = unchecked((sbyte)bytes[position++]);
                        operand = new BranchTarget(position + delta);
                        break;
                    }
                    case OperandType.InlineBrTarget:
                    {
                        int delta = ReadInt32(bytes, ref position);
                        operand = new BranchTarget(position + delta);
                        break;
                    }
                    case OperandType.InlineSwitch:
                    {
                        int count = ReadInt32(bytes, ref position);
                        int[] deltas = new int[count];
                        for (int i = 0; i < count; i++) deltas[i] = ReadInt32(bytes, ref position);
                        int baseOffset = position;
                        operand = new SwitchTargets(deltas.Select(delta => baseOffset + delta).ToArray());
                        break;
                    }
                    case OperandType.InlineString:
                        operand = module.ResolveString(ReadInt32(bytes, ref position));
                        break;
                    case OperandType.InlineField:
                        operand = module.ResolveField(ReadInt32(bytes, ref position), typeArguments, methodArguments);
                        break;
                    case OperandType.InlineMethod:
                        operand = module.ResolveMethod(ReadInt32(bytes, ref position), typeArguments, methodArguments);
                        break;
                    case OperandType.InlineType:
                        operand = module.ResolveType(ReadInt32(bytes, ref position), typeArguments, methodArguments);
                        break;
                    case OperandType.InlineTok:
                        operand = module.ResolveMember(ReadInt32(bytes, ref position), typeArguments, methodArguments);
                        break;
                    case OperandType.InlineSig:
                        throw new NotSupportedException("InlineSig/calli is not supported by the verification decoder");
                    case OperandType.ShortInlineVar:
                    {
                        byte index = bytes[position++];
                        operand = IsLocalOpcode(opcode) ? (object)locals[index] : (short)index;
                        break;
                    }
                    case OperandType.InlineVar:
                    {
                        ushort index = ReadUInt16(bytes, ref position);
                        operand = IsLocalOpcode(opcode) ? (object)locals[index] : unchecked((short)index);
                        break;
                    }
                    default:
                        throw new NotSupportedException("Unsupported operand type " + opcode.OperandType);
                }

                result.Add(new RawInstruction(offset, opcode, operand));
            }

            return result;
        }

        private static bool IsLocalOpcode(OpCode opcode)
        {
            return opcode.Name.Contains("loc");
        }

        private static ushort ReadUInt16(byte[] bytes, ref int position)
        {
            ushort value = BitConverter.ToUInt16(bytes, position);
            position += 2;
            return value;
        }

        private static int ReadInt32(byte[] bytes, ref int position)
        {
            int value = BitConverter.ToInt32(bytes, position);
            position += 4;
            return value;
        }

        private static long ReadInt64(byte[] bytes, ref int position)
        {
            long value = BitConverter.ToInt64(bytes, position);
            position += 8;
            return value;
        }

        private static float ReadSingle(byte[] bytes, ref int position)
        {
            float value = BitConverter.ToSingle(bytes, position);
            position += 4;
            return value;
        }

        private static double ReadDouble(byte[] bytes, ref int position)
        {
            double value = BitConverter.ToDouble(bytes, position);
            position += 8;
            return value;
        }

        private sealed class RawInstruction
        {
            internal RawInstruction(int offset, OpCode opcode, object operand)
            {
                Offset = offset;
                Opcode = opcode;
                Operand = operand;
            }

            internal int Offset { get; }
            internal OpCode Opcode { get; }
            internal object Operand { get; }
        }

        private sealed class BranchTarget
        {
            internal BranchTarget(int offset) { Offset = offset; }
            internal int Offset { get; }
        }

        private sealed class SwitchTargets
        {
            internal SwitchTargets(int[] offsets) { Offsets = offsets; }
            internal int[] Offsets { get; }
        }
    }

    private sealed class Result
    {
        internal Result(List<CodeInstruction> before, List<CodeInstruction> after)
        {
            Before = before;
            After = after;
        }

        internal List<CodeInstruction> Before { get; }
        internal List<CodeInstruction> After { get; }
    }
}
