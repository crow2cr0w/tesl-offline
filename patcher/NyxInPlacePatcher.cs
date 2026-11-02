using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace NyxTESLOffline.Patcher
{
    internal static class Program
    {
        private const string BootstrapTypeName = "NyxTESLOffline.InPlaceBootstrap";
        private const string PayloadResource = "Nyx.InPlace.Payload.dll";
        private const string HarmonyResource = "Nyx.InPlace.0Harmony.dll";
        private const string CecilResource = "Nyx.InPlace.Mono.Cecil.dll";
        private const string CecilRocksResource = "Nyx.InPlace.Mono.Cecil.Rocks.dll";
        private const string UtilsResource = "Nyx.InPlace.MonoMod.Utils.dll";
        private const string DetourResource = "Nyx.InPlace.MonoMod.RuntimeDetour.dll";

        private static int Main(string[] args)
        {
            if (args.Length != 8)
            {
                Console.Error.WriteLine(
                    "usage: NyxInPlacePatcher <Assembly-CSharp.dll> <output.dll> " +
                    "<payload.dll> <0Harmony.dll> <Mono.Cecil.dll> <Mono.Cecil.Rocks.dll> " +
                    "<MonoMod.Utils.dll> <MonoMod.RuntimeDetour.dll>");
                return 2;
            }

            string input = Path.GetFullPath(args[0]);
            string output = Path.GetFullPath(args[1]);
            string payload = Path.GetFullPath(args[2]);
            string harmony = Path.GetFullPath(args[3]);
            string cecil = Path.GetFullPath(args[4]);
            string cecilRocks = Path.GetFullPath(args[5]);
            string utils = Path.GetFullPath(args[6]);
            string detour = Path.GetFullPath(args[7]);

            RequireFile(input);
            RequireFile(payload);
            RequireFile(harmony);
            RequireFile(cecil);
            RequireFile(cecilRocks);
            RequireFile(utils);
            RequireFile(detour);

            string inputDir = Path.GetDirectoryName(input);
            DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(inputDir);

            ReaderParameters rp = new ReaderParameters();
            rp.ReadSymbols = false;
            rp.AssemblyResolver = resolver;

            AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(input, rp);
            ModuleDefinition module = asm.MainModule;

            if (FindType(module, BootstrapTypeName) != null ||
                FindResource(module, PayloadResource) != null)
            {
                Console.Error.WriteLine("error: input already contains the Nyx in-place bootstrap");
                asm.Dispose();
                return 3;
            }

            ReaderParameters payloadRp = new ReaderParameters();
            payloadRp.ReadSymbols = false;
            payloadRp.AssemblyResolver = resolver;
            AssemblyDefinition payloadTemplate =
                AssemblyDefinition.ReadAssembly(payload, payloadRp);
            string payloadAssemblyName = payloadTemplate.Name.Name;
            PatchRetailCardTransfer(module, payloadTemplate);
            AssertNoPayloadAssemblyReference(module, payloadAssemblyName);
            payloadTemplate.Dispose();

            AddResource(module, CecilResource, cecil);
            AddResource(module, CecilRocksResource, cecilRocks);
            AddResource(module, UtilsResource, utils);
            AddResource(module, DetourResource, detour);
            AddResource(module, HarmonyResource, harmony);
            AddResource(module, PayloadResource, payload);

            MethodDefinition ensureStarted = AddBootstrapType(module);
            MethodDefinition init = FindServicesInit(module);
            if (init == null || !init.HasBody || init.Body.Instructions.Count == 0)
            {
                Console.Error.WriteLine("error: SparkleServices.ServicesManager.Init was not found");
                asm.Dispose();
                return 4;
            }

            ILProcessor initIl = init.Body.GetILProcessor();
            Instruction first = init.Body.Instructions[0];
            initIl.InsertBefore(first, initIl.Create(OpCodes.Call, ensureStarted));
            if (init.Body.MaxStackSize < 8)
                init.Body.MaxStackSize = 8;

            string outDir = Path.GetDirectoryName(output);
            if (!Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            WriterParameters wp = new WriterParameters();
            wp.WriteSymbols = false;
            asm.Write(output, wp);
            asm.Dispose();

            Console.WriteLine("patched: " + output);
            Console.WriteLine("bootstrap target: SparkleServices.ServicesManager.Init");
            Console.WriteLine("direct IL patch: SPTESL.ServerCardCatalog.TransferCardToZone");
            Console.WriteLine("embedded payload: " + PayloadResource);
            return 0;
        }

        /*
         * Replace the retail hard stub directly in Assembly-CSharp.  The
         * source implementation is compiled as part of the payload only so
         * Cecil can consume verified CIL generated by the same compiler used
         * for the rest of Nyx.  Its method body is cloned into the retail
         * method; there is no AssemblyRef or runtime call to the payload here.
         */
        private static void PatchRetailCardTransfer(
            ModuleDefinition module,
            AssemblyDefinition payload)
        {
            TypeDefinition catalog = FindType(module, "SPTESL.ServerCardCatalog");
            if (catalog == null)
                throw new InvalidOperationException(
                    "SPTESL.ServerCardCatalog not found in retail assembly");

            MethodDefinition target = null;
            int i;
            for (i = 0; i < catalog.Methods.Count; ++i)
            {
                MethodDefinition m = catalog.Methods[i];
                if (m.Name == "TransferCardToZone" &&
                    m.IsStatic &&
                    m.Parameters.Count == 8 &&
                    m.ReturnType.FullName == "System.Boolean")
                {
                    if (target != null)
                        throw new InvalidOperationException(
                            "ambiguous retail TransferCardToZone signature");
                    target = m;
                }
            }
            if (target == null)
                throw new InvalidOperationException(
                    "retail TransferCardToZone signature not found");
            if (!IsFalseStub(target))
                throw new InvalidOperationException(
                    "retail TransferCardToZone is not the expected 'return false' stub; refusing rewrite");

            TypeDefinition templateType = FindType(
                payload.MainModule,
                "NyxTESLOffline.Patches.CardTransferPatch");
            if (templateType == null)
                throw new InvalidOperationException(
                    "card transfer IL template type missing from payload");

            MethodDefinition transferTemplate = null;
            MethodDefinition findPlayerTemplate = null;
            MethodDefinition laneAtTemplate = null;
            MethodDefinition laneCountTemplate = null;
            for (i = 0; i < templateType.Methods.Count; ++i)
            {
                MethodDefinition m = templateType.Methods[i];
                if (m.Name == "TransferDirect" && m.IsStatic &&
                    m.Parameters.Count == 8)
                    transferTemplate = m;
                else if (m.Name == "FindPlayer" && m.IsStatic &&
                    m.Parameters.Count == 2)
                    findPlayerTemplate = m;
                else if (m.Name == "LaneAt" && m.IsStatic &&
                    m.Parameters.Count == 2)
                    laneAtTemplate = m;
                else if (m.Name == "LaneCount" && m.IsStatic &&
                    m.Parameters.Count == 1)
                    laneCountTemplate = m;
            }
            if (transferTemplate == null || findPlayerTemplate == null ||
                laneAtTemplate == null || laneCountTemplate == null)
                throw new InvalidOperationException(
                    "card transfer IL templates are incomplete (need TransferDirect, FindPlayer, LaneAt, LaneCount)");

            MethodDefinition findPlayer = CloneHelperMethod(
                module, catalog, findPlayerTemplate, "Nyx_FindPlayerForTransfer");
            MethodDefinition laneAt = CloneHelperMethod(
                module, catalog, laneAtTemplate, "Nyx_LaneAtForTransfer");
            MethodDefinition laneCount = CloneHelperMethod(
                module, catalog, laneCountTemplate, "Nyx_LaneCountForTransfer");

            Dictionary<string, MethodReference> localMethodMap =
                new Dictionary<string, MethodReference>();
            localMethodMap[templateType.FullName + "::FindPlayer"] = findPlayer;
            localMethodMap[templateType.FullName + "::LaneAt"] = laneAt;
            localMethodMap[templateType.FullName + "::LaneCount"] = laneCount;

            /* Helpers first so TransferDirect call sites resolve via localMethodMap. */
            CloneMethodBody(
                findPlayerTemplate, findPlayer, module, localMethodMap);
            CloneMethodBody(
                laneAtTemplate, laneAt, module, localMethodMap);
            CloneMethodBody(
                laneCountTemplate, laneCount, module, localMethodMap);
            CloneMethodBody(
                transferTemplate, target, module, localMethodMap);

            target.Body.MaxStackSize = Math.Max(target.Body.MaxStackSize, 8);
        }

        private static MethodDefinition CloneHelperMethod(
            ModuleDefinition module,
            TypeDefinition catalog,
            MethodDefinition template,
            string retailName)
        {
            MethodDefinition helper = new MethodDefinition(
                retailName,
                Mono.Cecil.MethodAttributes.Private |
                Mono.Cecil.MethodAttributes.Static |
                Mono.Cecil.MethodAttributes.HideBySig,
                ImportTemplateType(module, template.ReturnType));
            int i;
            for (i = 0; i < template.Parameters.Count; ++i)
            {
                helper.Parameters.Add(new ParameterDefinition(
                    template.Parameters[i].Name,
                    template.Parameters[i].Attributes,
                    ImportTemplateType(
                        module, template.Parameters[i].ParameterType)));
            }
            catalog.Methods.Add(helper);
            return helper;
        }

        private static void AssertNoPayloadAssemblyReference(
            ModuleDefinition module,
            string payloadAssemblyName)
        {
            int i;
            for (i = 0; i < module.AssemblyReferences.Count; ++i)
            {
                if (module.AssemblyReferences[i].Name == payloadAssemblyName)
                {
                    throw new InvalidOperationException(
                        "direct card-transfer IL leaked a runtime AssemblyRef to payload: " +
                        payloadAssemblyName);
                }
            }
        }

        private static bool IsFalseStub(MethodDefinition method)
        {
            if (!method.HasBody)
                return false;
            List<Instruction> code = new List<Instruction>();
            int i;
            for (i = 0; i < method.Body.Instructions.Count; ++i)
            {
                Instruction ins = method.Body.Instructions[i];
                if (ins.OpCode != OpCodes.Nop)
                    code.Add(ins);
            }
            return code.Count == 2 &&
                code[0].OpCode == OpCodes.Ldc_I4_0 &&
                code[1].OpCode == OpCodes.Ret;
        }

        private static TypeReference ImportTemplateType(
            ModuleDefinition module,
            TypeReference type)
        {
            if (type == null)
                return null;

            ByReferenceType byRef = type as ByReferenceType;
            if (byRef != null)
                return new ByReferenceType(
                    ImportTemplateType(module, byRef.ElementType));

            ArrayType array = type as ArrayType;
            if (array != null)
                return new ArrayType(
                    ImportTemplateType(module, array.ElementType), array.Rank);

            PointerType pointer = type as PointerType;
            if (pointer != null)
                return new PointerType(
                    ImportTemplateType(module, pointer.ElementType));

            GenericInstanceType generic = type as GenericInstanceType;
            if (generic != null)
            {
                GenericInstanceType imported = new GenericInstanceType(
                    ImportTemplateType(module, generic.ElementType));
                int i;
                for (i = 0; i < generic.GenericArguments.Count; ++i)
                    imported.GenericArguments.Add(
                        ImportTemplateType(module, generic.GenericArguments[i]));
                return imported;
            }

            /* A bare GenericParameter has no usable import context here.
             * Callers cloning a MethodReference must route signature types
             * through ImportMethodSignatureType() so !0/!!0 can be rebound. */
            GenericParameter unboundGeneric = type as GenericParameter;
            if (unboundGeneric != null)
            {
                throw new InvalidOperationException(
                    "attempted to import unbound generic parameter without context: " +
                    unboundGeneric.FullName);
            }

            TypeDefinition resolved = null;
            try { resolved = type.Resolve(); }
            catch { resolved = null; }
            if (resolved != null &&
                resolved.Module != null &&
                resolved.Module.Assembly != null &&
                resolved.Module.Assembly.Name.Name == module.Assembly.Name.Name)
            {
                TypeDefinition local = FindType(module, resolved.FullName);
                if (local == null)
                    throw new InvalidOperationException(
                        "unable to map retail type while cloning IL: " +
                        resolved.FullName);
                return local;
            }

            return module.ImportReference(type);
        }

        private static MethodReference ImportTemplateMethod(
            ModuleDefinition module,
            MethodReference method,
            Dictionary<string, MethodReference> localMethodMap)
        {
            string localKey = method.DeclaringType.FullName + "::" + method.Name;
            MethodReference mapped;
            if (localMethodMap.TryGetValue(localKey, out mapped))
                return mapped;

            MethodDefinition resolved = null;
            try { resolved = method.Resolve(); }
            catch { resolved = null; }
            if (resolved != null &&
                resolved.Module != null &&
                resolved.Module.Assembly != null &&
                resolved.Module.Assembly.Name.Name == module.Assembly.Name.Name)
            {
                TypeDefinition owner = FindType(module, resolved.DeclaringType.FullName);
                if (owner == null)
                    throw new InvalidOperationException(
                        "unable to map retail method owner: " +
                        resolved.DeclaringType.FullName);
                int i;
                for (i = 0; i < owner.Methods.Count; ++i)
                {
                    if (owner.Methods[i].FullName == resolved.FullName)
                        return owner.Methods[i];
                }
                throw new InvalidOperationException(
                    "unable to map retail method while cloning IL: " +
                    resolved.FullName);
            }

            /*
             * Framework methods on a CLOSED generic declaring type
             * (e.g. List<ICardLane>.get_Item, List<PlayerState>.get_Count).
             *
             * alpha7 runtime failure: a hand-built MethodReference whose
             * declaring type was List<ICardLane> but whose metadata did not
             * bind to mscorlib's open List`1.get_Item produced
             * MissingMethodException at CreatePlayerCards.  Import the open
             * method from mscorlib first, then rebind DeclaringType to the
             * closed GenericInstanceType.
             */
            GenericInstanceType closedDeclaring =
                method.DeclaringType as GenericInstanceType;
            if (resolved != null &&
                closedDeclaring != null &&
                resolved.Module != null &&
                resolved.Module.Assembly != null &&
                IsFrameworkAssembly(resolved.Module.Assembly.Name.Name))
            {
                MethodReference openImported = module.ImportReference(resolved);
                TypeReference importedClosedDeclaring =
                    ImportTemplateType(module, closedDeclaring);

                MethodReference closed = new MethodReference(
                    openImported.Name,
                    module.TypeSystem.Void,
                    importedClosedDeclaring);
                closed.HasThis = openImported.HasThis;
                closed.ExplicitThis = openImported.ExplicitThis;
                closed.CallingConvention = openImported.CallingConvention;

                int g;
                for (g = 0; g < openImported.GenericParameters.Count; ++g)
                {
                    closed.GenericParameters.Add(new GenericParameter(
                        openImported.GenericParameters[g].Name, closed));
                }

                /* Prefer the CLOSED source MethodReference's signature so
                 * !0 is rebound to ICardLane / PlayerState / etc. */
                closed.ReturnType = ImportMethodSignatureType(
                    module, method.ReturnType, method, closed);

                int p;
                for (p = 0; p < method.Parameters.Count; ++p)
                {
                    closed.Parameters.Add(new ParameterDefinition(
                        ImportMethodSignatureType(
                            module,
                            method.Parameters[p].ParameterType,
                            method,
                            closed)));
                }
                return closed;
            }

            /* Non-generic framework / interface methods (IList.get_Item,
             * IList.get_Count, Object.ReferenceEquals, etc.). */
            if (resolved != null &&
                resolved.Module != null &&
                resolved.Module.Assembly != null &&
                IsFrameworkAssembly(resolved.Module.Assembly.Name.Name) &&
                closedDeclaring == null)
            {
                return module.ImportReference(resolved);
            }

            GenericInstanceMethod genericMethod = method as GenericInstanceMethod;
            if (genericMethod != null)
            {
                MethodReference element = ImportTemplateMethod(
                    module, genericMethod.ElementMethod, localMethodMap);
                GenericInstanceMethod imported = new GenericInstanceMethod(element);
                int gi;
                for (gi = 0; gi < genericMethod.GenericArguments.Count; ++gi)
                {
                    imported.GenericArguments.Add(ImportTemplateType(
                        module, genericMethod.GenericArguments[gi]));
                }
                return imported;
            }

            /* Do not use ImportReference(method) blindly here.  A framework
             * method can have a closed generic declaring type such as
             * List<PlayerState>.  In Cecil the return type of get_Item on
             * that reference is still represented as the generic parameter
             * !0.  ImportReference(!0) without an owning generic context
             * crashes older Cecil releases in ImportGenericContext.
             *
             * Rebuild the MethodReference first, create its method-generic
             * context, and then substitute type/method generic parameters
             * against the CLOSED source reference before importing the
             * signature. */
            TypeReference importedDeclaringType =
                ImportTemplateType(module, method.DeclaringType);
            MethodReference rebuilt = new MethodReference(
                method.Name,
                module.TypeSystem.Void,
                importedDeclaringType);
            rebuilt.HasThis = method.HasThis;
            rebuilt.ExplicitThis = method.ExplicitThis;
            rebuilt.CallingConvention = method.CallingConvention;

            int gg;
            for (gg = 0; gg < method.GenericParameters.Count; ++gg)
            {
                rebuilt.GenericParameters.Add(new GenericParameter(
                    method.GenericParameters[gg].Name, rebuilt));
            }

            rebuilt.ReturnType = ImportMethodSignatureType(
                module, method.ReturnType, method, rebuilt);

            int pp;
            for (pp = 0; pp < method.Parameters.Count; ++pp)
            {
                rebuilt.Parameters.Add(new ParameterDefinition(
                    ImportMethodSignatureType(
                        module,
                        method.Parameters[pp].ParameterType,
                        method,
                        rebuilt)));
            }
            return rebuilt;
        }

        private static bool IsFrameworkAssembly(string name)
        {
            return name == "mscorlib" ||
                   name == "netstandard" ||
                   name == "System.Private.CoreLib" ||
                   name == "System.Collections" ||
                   name == "System.Runtime";
        }

        /* Import a type that appears in a MethodReference signature.
         * Generic parameters need the method/declaring-type context; importing
         * them through ModuleDefinition.ImportReference(TypeReference) alone
         * is invalid in Cecil 0.10.x and is exactly what caused alpha4 to die
         * while cloning List<PlayerState>.get_Item(Int32). */
        private static TypeReference ImportMethodSignatureType(
            ModuleDefinition module,
            TypeReference type,
            MethodReference sourceMethod,
            MethodReference rebuiltMethod)
        {
            if (type == null)
                return null;

            GenericParameter genericParameter = type as GenericParameter;
            if (genericParameter != null)
            {
                if (genericParameter.Owner is TypeReference)
                {
                    GenericInstanceType sourceDeclaring =
                        sourceMethod.DeclaringType as GenericInstanceType;
                    if (sourceDeclaring != null &&
                        genericParameter.Position >= 0 &&
                        genericParameter.Position < sourceDeclaring.GenericArguments.Count)
                    {
                        return ImportTemplateType(
                            module,
                            sourceDeclaring.GenericArguments[genericParameter.Position]);
                    }

                    if (rebuiltMethod.DeclaringType != null &&
                        genericParameter.Position >= 0 &&
                        genericParameter.Position < rebuiltMethod.DeclaringType.GenericParameters.Count)
                    {
                        return rebuiltMethod.DeclaringType.GenericParameters[
                            genericParameter.Position];
                    }

                    throw new InvalidOperationException(
                        "unable to bind declaring-type generic parameter " +
                        genericParameter.FullName + " in " + sourceMethod.FullName);
                }

                if (genericParameter.Owner is MethodReference)
                {
                    if (genericParameter.Position >= 0 &&
                        genericParameter.Position < rebuiltMethod.GenericParameters.Count)
                    {
                        return rebuiltMethod.GenericParameters[
                            genericParameter.Position];
                    }

                    throw new InvalidOperationException(
                        "unable to bind method generic parameter " +
                        genericParameter.FullName + " in " + sourceMethod.FullName);
                }

                throw new InvalidOperationException(
                    "generic parameter has unsupported owner while cloning IL: " +
                    genericParameter.FullName);
            }

            ByReferenceType byRef = type as ByReferenceType;
            if (byRef != null)
                return new ByReferenceType(ImportMethodSignatureType(
                    module, byRef.ElementType, sourceMethod, rebuiltMethod));

            ArrayType array = type as ArrayType;
            if (array != null)
                return new ArrayType(ImportMethodSignatureType(
                    module, array.ElementType, sourceMethod, rebuiltMethod), array.Rank);

            PointerType pointer = type as PointerType;
            if (pointer != null)
                return new PointerType(ImportMethodSignatureType(
                    module, pointer.ElementType, sourceMethod, rebuiltMethod));

            GenericInstanceType instance = type as GenericInstanceType;
            if (instance != null)
            {
                GenericInstanceType imported = new GenericInstanceType(
                    ImportMethodSignatureType(
                        module, instance.ElementType, sourceMethod, rebuiltMethod));
                int i;
                for (i = 0; i < instance.GenericArguments.Count; ++i)
                {
                    imported.GenericArguments.Add(ImportMethodSignatureType(
                        module,
                        instance.GenericArguments[i],
                        sourceMethod,
                        rebuiltMethod));
                }
                return imported;
            }

            return ImportTemplateType(module, type);
        }

        private static FieldReference ImportTemplateField(
            ModuleDefinition module,
            FieldReference field)
        {
            FieldDefinition resolved = null;
            try { resolved = field.Resolve(); }
            catch { resolved = null; }
            if (resolved != null &&
                resolved.Module != null &&
                resolved.Module.Assembly != null &&
                resolved.Module.Assembly.Name.Name == module.Assembly.Name.Name)
            {
                TypeDefinition owner = FindType(module, resolved.DeclaringType.FullName);
                if (owner == null)
                    throw new InvalidOperationException(
                        "unable to map retail field owner: " +
                        resolved.DeclaringType.FullName);
                int i;
                for (i = 0; i < owner.Fields.Count; ++i)
                {
                    if (owner.Fields[i].FullName == resolved.FullName)
                        return owner.Fields[i];
                }
                throw new InvalidOperationException(
                    "unable to map retail field while cloning IL: " +
                    resolved.FullName);
            }
            return new FieldReference(
                field.Name,
                ImportTemplateType(module, field.FieldType),
                ImportTemplateType(module, field.DeclaringType));
        }

        private static void CloneMethodBody(
            MethodDefinition source,
            MethodDefinition destination,
            ModuleDefinition module,
            Dictionary<string, MethodReference> localMethodMap)
        {
            if (!source.HasBody)
                throw new InvalidOperationException(
                    "IL template has no body: " + source.FullName);
            if (source.Body.ExceptionHandlers.Count != 0)
                throw new InvalidOperationException(
                    "card transfer template unexpectedly contains exception handlers");

            destination.Body = new Mono.Cecil.Cil.MethodBody(destination);
            destination.Body.InitLocals = source.Body.InitLocals;
            destination.Body.MaxStackSize = source.Body.MaxStackSize;

            Dictionary<VariableDefinition, VariableDefinition> variables =
                new Dictionary<VariableDefinition, VariableDefinition>();
            int i;
            for (i = 0; i < source.Body.Variables.Count; ++i)
            {
                VariableDefinition v = new VariableDefinition(
                    ImportTemplateType(module, source.Body.Variables[i].VariableType));
                destination.Body.Variables.Add(v);
                variables[source.Body.Variables[i]] = v;
            }

            Dictionary<Instruction, Instruction> instructions =
                new Dictionary<Instruction, Instruction>();
            ILProcessor il = destination.Body.GetILProcessor();

            for (i = 0; i < source.Body.Instructions.Count; ++i)
            {
                Instruction src = source.Body.Instructions[i];
                Instruction dst = CloneInstructionFirstPass(
                    src, source, destination, module, variables, localMethodMap);
                instructions[src] = dst;
                il.Append(dst);
            }

            for (i = 0; i < source.Body.Instructions.Count; ++i)
            {
                Instruction src = source.Body.Instructions[i];
                Instruction dst = instructions[src];
                Instruction branch = src.Operand as Instruction;
                if (branch != null)
                {
                    dst.Operand = instructions[branch];
                    continue;
                }
                Instruction[] branches = src.Operand as Instruction[];
                if (branches != null)
                {
                    Instruction[] mapped = new Instruction[branches.Length];
                    int j;
                    for (j = 0; j < branches.Length; ++j)
                        mapped[j] = instructions[branches[j]];
                    dst.Operand = mapped;
                }
            }
        }

        private static Instruction CloneInstructionFirstPass(
            Instruction sourceInstruction,
            MethodDefinition sourceMethod,
            MethodDefinition destinationMethod,
            ModuleDefinition module,
            Dictionary<VariableDefinition, VariableDefinition> variables,
            Dictionary<string, MethodReference> localMethodMap)
        {
            OpCode op = sourceInstruction.OpCode;
            object operand = sourceInstruction.Operand;
            if (operand == null)
                return Instruction.Create(op);
            if (operand is string)
                return Instruction.Create(op, (string)operand);
            if (operand is sbyte)
                return Instruction.Create(op, (sbyte)operand);
            if (operand is byte)
                return Instruction.Create(op, (sbyte)(byte)operand);
            if (operand is int)
                return Instruction.Create(op, (int)operand);
            if (operand is long)
                return Instruction.Create(op, (long)operand);
            if (operand is float)
                return Instruction.Create(op, (float)operand);
            if (operand is double)
                return Instruction.Create(op, (double)operand);

            ParameterDefinition parameter = operand as ParameterDefinition;
            if (parameter != null)
            {
                if (parameter.Index < 0 ||
                    parameter.Index >= destinationMethod.Parameters.Count)
                    throw new InvalidOperationException(
                        "invalid template parameter index while cloning IL");
                return Instruction.Create(
                    op, destinationMethod.Parameters[parameter.Index]);
            }

            VariableDefinition variable = operand as VariableDefinition;
            if (variable != null)
                return Instruction.Create(op, variables[variable]);

            MethodReference method = operand as MethodReference;
            if (method != null)
                return Instruction.Create(
                    op, ImportTemplateMethod(module, method, localMethodMap));

            FieldReference field = operand as FieldReference;
            if (field != null)
                return Instruction.Create(
                    op, ImportTemplateField(module, field));

            TypeReference type = operand as TypeReference;
            if (type != null)
                return Instruction.Create(
                    op, ImportTemplateType(module, type));

            Instruction branch = operand as Instruction;
            if (branch != null)
                return Instruction.Create(op, Instruction.Create(OpCodes.Nop));

            Instruction[] branches = operand as Instruction[];
            if (branches != null)
            {
                Instruction[] placeholders = new Instruction[branches.Length];
                int i;
                for (i = 0; i < placeholders.Length; ++i)
                    placeholders[i] = Instruction.Create(OpCodes.Nop);
                return Instruction.Create(op, placeholders);
            }

            throw new InvalidOperationException(
                "unsupported IL operand in card transfer template: " +
                operand.GetType().FullName);
        }

        private static void RequireFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("required input not found", path);
        }

        private static Resource FindResource(ModuleDefinition module, string name)
        {
            int i;
            for (i = 0; i < module.Resources.Count; ++i)
            {
                if (module.Resources[i].Name == name)
                    return module.Resources[i];
            }
            return null;
        }

        private static void AddResource(ModuleDefinition module, string name, string path)
        {
            module.Resources.Add(new EmbeddedResource(
                name,
                ManifestResourceAttributes.Private,
                File.ReadAllBytes(path)));
        }

        private static TypeDefinition FindType(ModuleDefinition module, string fullName)
        {
            int i;
            for (i = 0; i < module.Types.Count; ++i)
            {
                TypeDefinition t = module.Types[i];
                TypeDefinition found = FindTypeRecursive(t, fullName);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static TypeDefinition FindTypeRecursive(TypeDefinition type, string fullName)
        {
            if (type.FullName == fullName)
                return type;
            int i;
            for (i = 0; i < type.NestedTypes.Count; ++i)
            {
                TypeDefinition found = FindTypeRecursive(type.NestedTypes[i], fullName);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static MethodDefinition FindServicesInit(ModuleDefinition module)
        {
            TypeDefinition type = FindType(module, "SparkleServices.ServicesManager");
            if (type == null)
                return null;

            int i;
            for (i = 0; i < type.Methods.Count; ++i)
            {
                MethodDefinition m = type.Methods[i];
                if (m.Name == "Init" && m.Parameters.Count == 2)
                    return m;
            }
            return null;
        }

        private static MethodDefinition AddBootstrapType(ModuleDefinition module)
        {
            TypeDefinition type = new TypeDefinition(
                "NyxTESLOffline",
                "InPlaceBootstrap",
                Mono.Cecil.TypeAttributes.NotPublic |
                Mono.Cecil.TypeAttributes.Abstract |
                Mono.Cecil.TypeAttributes.Sealed |
                Mono.Cecil.TypeAttributes.BeforeFieldInit,
                module.TypeSystem.Object);
            module.Types.Add(type);

            FieldDefinition started = new FieldDefinition(
                "_started",
                Mono.Cecil.FieldAttributes.Private | Mono.Cecil.FieldAttributes.Static,
                module.TypeSystem.Boolean);
            type.Fields.Add(started);

            MethodDefinition loadEmbedded = BuildLoadEmbedded(module, type);
            MethodDefinition ensure = BuildEnsureStarted(module, type, started, loadEmbedded);
            return ensure;
        }

        private static MethodDefinition BuildLoadEmbedded(
            ModuleDefinition module,
            TypeDefinition owner)
        {
            MethodDefinition method = new MethodDefinition(
                "LoadEmbedded",
                Mono.Cecil.MethodAttributes.Private |
                Mono.Cecil.MethodAttributes.Static |
                Mono.Cecil.MethodAttributes.HideBySig,
                module.ImportReference(typeof(Assembly)));
            method.Parameters.Add(new ParameterDefinition(
                "resourceName",
                Mono.Cecil.ParameterAttributes.None,
                module.TypeSystem.String));
            owner.Methods.Add(method);

            MethodReference getExecutingAssembly = module.ImportReference(
                typeof(Assembly).GetMethod("GetExecutingAssembly", Type.EmptyTypes));
            MethodReference getManifestResourceStream = module.ImportReference(
                typeof(Assembly).GetMethod(
                    "GetManifestResourceStream",
                    new Type[] { typeof(string) }));
            MethodReference streamLength = module.ImportReference(
                typeof(Stream).GetProperty("Length").GetGetMethod());
            ConstructorInfo binaryReaderCtorInfo = typeof(BinaryReader).GetConstructor(
                new Type[] { typeof(Stream) });
            MethodReference binaryReaderCtor = module.ImportReference(binaryReaderCtorInfo);
            MethodReference readBytes = module.ImportReference(
                typeof(BinaryReader).GetMethod(
                    "ReadBytes",
                    new Type[] { typeof(int) }));
            MethodReference assemblyLoad = module.ImportReference(
                typeof(Assembly).GetMethod(
                    "Load",
                    new Type[] { typeof(byte[]) }));

            VariableDefinition stream = new VariableDefinition(module.ImportReference(typeof(Stream)));
            VariableDefinition reader = new VariableDefinition(module.ImportReference(typeof(BinaryReader)));
            VariableDefinition bytes = new VariableDefinition(new ArrayType(module.TypeSystem.Byte));
            method.Body.Variables.Add(stream);
            method.Body.Variables.Add(reader);
            method.Body.Variables.Add(bytes);
            method.Body.InitLocals = true;
            method.Body.MaxStackSize = 4;

            ILProcessor il = method.Body.GetILProcessor();
            Instruction haveStream = il.Create(OpCodes.Nop);

            il.Append(il.Create(OpCodes.Call, getExecutingAssembly));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Callvirt, getManifestResourceStream));
            il.Append(il.Create(OpCodes.Stloc, stream));
            il.Append(il.Create(OpCodes.Ldloc, stream));
            il.Append(il.Create(OpCodes.Brtrue, haveStream));
            il.Append(il.Create(OpCodes.Ldnull));
            il.Append(il.Create(OpCodes.Ret));
            il.Append(haveStream);
            il.Append(il.Create(OpCodes.Ldloc, stream));
            il.Append(il.Create(OpCodes.Newobj, binaryReaderCtor));
            il.Append(il.Create(OpCodes.Stloc, reader));
            il.Append(il.Create(OpCodes.Ldloc, reader));
            il.Append(il.Create(OpCodes.Ldloc, stream));
            il.Append(il.Create(OpCodes.Callvirt, streamLength));
            il.Append(il.Create(OpCodes.Conv_I4));
            il.Append(il.Create(OpCodes.Callvirt, readBytes));
            il.Append(il.Create(OpCodes.Stloc, bytes));
            il.Append(il.Create(OpCodes.Ldloc, bytes));
            il.Append(il.Create(OpCodes.Call, assemblyLoad));
            il.Append(il.Create(OpCodes.Ret));

            return method;
        }

        private static MethodDefinition BuildEnsureStarted(
            ModuleDefinition module,
            TypeDefinition owner,
            FieldDefinition started,
            MethodDefinition loadEmbedded)
        {
            MethodDefinition method = new MethodDefinition(
                "EnsureStarted",
                Mono.Cecil.MethodAttributes.Public |
                Mono.Cecil.MethodAttributes.Static |
                Mono.Cecil.MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            owner.Methods.Add(method);

            MethodReference assemblyGetType = module.ImportReference(
                typeof(Assembly).GetMethod("GetType", new Type[] { typeof(string) }));
            MethodReference typeGetMethod = module.ImportReference(
                typeof(Type).GetMethod(
                    "GetMethod",
                    new Type[] { typeof(string), typeof(BindingFlags) }));
            MethodReference methodInvoke = module.ImportReference(
                typeof(MethodBase).GetMethod(
                    "Invoke",
                    new Type[] { typeof(object), typeof(object[]) }));

            VariableDefinition payloadAssembly = new VariableDefinition(
                module.ImportReference(typeof(Assembly)));
            VariableDefinition payloadType = new VariableDefinition(
                module.ImportReference(typeof(Type)));
            VariableDefinition bootstrapMethod = new VariableDefinition(
                module.ImportReference(typeof(MethodInfo)));
            method.Body.Variables.Add(payloadAssembly);
            method.Body.Variables.Add(payloadType);
            method.Body.Variables.Add(bootstrapMethod);
            method.Body.InitLocals = true;
            method.Body.MaxStackSize = 5;

            ILProcessor il = method.Body.GetILProcessor();
            Instruction begin = il.Create(OpCodes.Nop);
            Instruction loadPayload = il.Create(OpCodes.Nop);
            Instruction havePayload = il.Create(OpCodes.Nop);
            Instruction haveType = il.Create(OpCodes.Nop);
            Instruction haveMethod = il.Create(OpCodes.Nop);
            Instruction done = il.Create(OpCodes.Ret);

            il.Append(begin);
            il.Append(il.Create(OpCodes.Ldsfld, started));
            il.Append(il.Create(OpCodes.Brtrue, done));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Stsfld, started));

            EmitLoadAndPop(il, loadEmbedded, CecilResource);
            EmitLoadAndPop(il, loadEmbedded, CecilRocksResource);
            EmitLoadAndPop(il, loadEmbedded, UtilsResource);
            EmitLoadAndPop(il, loadEmbedded, DetourResource);
            EmitLoadAndPop(il, loadEmbedded, HarmonyResource);

            il.Append(loadPayload);
            il.Append(il.Create(OpCodes.Ldstr, PayloadResource));
            il.Append(il.Create(OpCodes.Call, loadEmbedded));
            il.Append(il.Create(OpCodes.Stloc, payloadAssembly));
            il.Append(il.Create(OpCodes.Ldloc, payloadAssembly));
            il.Append(il.Create(OpCodes.Brtrue, havePayload));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Stsfld, started));
            il.Append(il.Create(OpCodes.Br, done));

            il.Append(havePayload);
            il.Append(il.Create(OpCodes.Ldloc, payloadAssembly));
            il.Append(il.Create(OpCodes.Ldstr, "NyxTESLOffline.Plugin"));
            il.Append(il.Create(OpCodes.Callvirt, assemblyGetType));
            il.Append(il.Create(OpCodes.Stloc, payloadType));
            il.Append(il.Create(OpCodes.Ldloc, payloadType));
            il.Append(il.Create(OpCodes.Brtrue, haveType));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Stsfld, started));
            il.Append(il.Create(OpCodes.Br, done));

            il.Append(haveType);
            il.Append(il.Create(OpCodes.Ldloc, payloadType));
            il.Append(il.Create(OpCodes.Ldstr, "Bootstrap"));
            il.Append(il.Create(OpCodes.Ldc_I4, (int)(BindingFlags.Public | BindingFlags.Static)));
            il.Append(il.Create(OpCodes.Callvirt, typeGetMethod));
            il.Append(il.Create(OpCodes.Stloc, bootstrapMethod));
            il.Append(il.Create(OpCodes.Ldloc, bootstrapMethod));
            il.Append(il.Create(OpCodes.Brtrue, haveMethod));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Stsfld, started));
            il.Append(il.Create(OpCodes.Br, done));

            il.Append(haveMethod);
            il.Append(il.Create(OpCodes.Ldloc, bootstrapMethod));
            il.Append(il.Create(OpCodes.Ldnull));
            il.Append(il.Create(OpCodes.Ldnull));
            il.Append(il.Create(OpCodes.Callvirt, methodInvoke));
            il.Append(il.Create(OpCodes.Pop));
            il.Append(done);

            return method;
        }

        private static void EmitLoadAndPop(
            ILProcessor il,
            MethodReference loadEmbedded,
            string resource)
        {
            il.Append(il.Create(OpCodes.Ldstr, resource));
            il.Append(il.Create(OpCodes.Call, loadEmbedded));
            il.Append(il.Create(OpCodes.Pop));
        }
    }
}
